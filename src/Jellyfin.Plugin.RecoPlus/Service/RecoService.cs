using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.RecoPlus.Data;
using Jellyfin.Plugin.RecoPlus.Integration;
using Jellyfin.Plugin.RecoPlus.Seerr;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Service;

/// <summary>État du moteur, pour la page de configuration (textes emballés : <see cref="Texts.Pack"/>).</summary>
public sealed class EngineStatus
{
    public DateTime? LastFullRun { get; set; }

    public double LastFullRunSeconds { get; set; }

    public DateTime? LastUserRun { get; set; }

    public int UsersWithRecs { get; set; }

    public string? LastError { get; set; }

    public string PlaybackReporting { get; set; } = "non lue";

    public string Seerr { get; set; } = "désactivé";

    public string JellyfinEnhanced { get; set; } = "non lu";

    public int ExternalTitles { get; set; }

    public bool Running { get; set; }
}

/// <summary>
/// Calcule les recommandations (en arrière-plan, un seul calcul à la fois) et les sert depuis un cache mémoire + disque :
/// aucune page n'attend un calcul. Un compte sans résultat prêt reçoit la réponse normale de Jellyfin.
/// </summary>
public sealed class RecoService : IDisposable
{
    private static (string Id, TimeZoneInfo Zone)? _zone;   // fuseau configuré, résolu une fois

    private readonly LibraryIndex _index;
    private readonly UserHistory _history;
    private readonly UserDataReader _userData;
    private readonly PlaybackReportingReader _pr;
    private readonly SeerrService _seerr;
    private readonly IUserManager _users;
    private readonly ILocalizationManager _loc;
    private readonly ILogger<RecoService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JellyfinEnhancedReader _je;
    private readonly ImpressionStore _impressions;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, UserRecs> _recs = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, Interaction>> _lastHist = new(StringComparer.Ordinal);
    private PrDates? _prCache;

    /// <summary>
    /// Exploration Seerr récente de chaque compte : un recalcul d'un seul compte (après une lecture) la reprend au lieu
    /// de tout relire (la partie longue du calcul) ; le calcul complet la refait.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime At, ExtCandidates C)> _extMemo = new(StringComparer.Ordinal);

    private static readonly TimeSpan ExtMemoTtl = TimeSpan.FromHours(6);
    private DateTime _prAt;
    private readonly object _loadLock = new();
    private readonly object _vecLock = new();
    private readonly Timer _flush;
    private volatile bool _loaded;
    private int _dirty;
    private IReadOnlyDictionary<string, SparseVector>? _vectors;
    private LibrarySnapshot? _library;

    public RecoService(LibraryIndex index, UserHistory history, UserDataReader userData, PlaybackReportingReader pr, SeerrService seerr,
        JellyfinEnhancedReader je, ImpressionStore impressions, IUserManager users, ILocalizationManager loc, ILogger<RecoService> log, IServiceProvider services)
    {
        _services = services;
        _je = je;
        _impressions = impressions;
        _index = index;
        _history = history;
        _userData = userData;
        _pr = pr;
        _seerr = seerr;
        _users = users;
        _loc = loc;
        _log = log;
        // listes recalculées compte par compte : écrites sur disque au plus toutes les 5 minutes (et à l'arrêt)
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>État courant.</summary>
    public EngineStatus Status { get; } = new();

    /// <summary>
    /// Vecteurs de contenu (pour « Plus comme ceci ») : ceux du dernier calcul, sinon construits à la demande depuis la
    /// bibliothèque (après un redémarrage, sans attendre un calcul). Null seulement si la bibliothèque est illisible.
    /// </summary>
    public IReadOnlyDictionary<string, SparseVector>? Vectors
    {
        get
        {
            if (_vectors is { } v)
            {
                return v;
            }

            lock (_vecLock)
            {
                if (_vectors is null)
                {
                    try
                    {
                        var lib = _index.Get();
                        (_library, _vectors) = (lib, Core.BuildVectors(lib.Titles, Settings));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _log.LogWarning("Reco+: content vectors unavailable ({Msg})", ex.Message);
                    }
                }

                return _vectors;
            }
        }
    }

    /// <summary>Instantané de la bibliothèque des vecteurs ci-dessus.</summary>
    public LibrarySnapshot? Library
    {
        get
        {
            _ = Vectors; // construits ensemble
            return _library;
        }
    }

    private static string CachePath => Path.Combine(Plugin.Instance!.DataFolderPath, "recommandations.json");

    private static EngineSettings Settings => (Plugin.Instance?.Configuration.Engine ?? new EngineSettings()).Sanitized();

    /// <summary>
    /// Retire un titre hors bibliothèque des propositions d'un compte (il vient d'être demandé) : dans la liste gardée,
    /// pas seulement dans la vue de la tranche horaire servie.
    /// </summary>
    public void DropExtended(Guid user, string mediaType, int tmdbId)
    {
        if (_recs.TryGetValue(user.ToString("N"), out var r))
        {
            r.Extended = r.Extended.Where(e => !(e.MediaType == mediaType && e.TmdbId == tmdbId)).ToList();
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    /// <summary>Résultat prêt pour un compte (null sinon).</summary>
    public UserRecs? For(Guid user)
    {
        EnsureLoaded();
        var key = user.ToString("N");
        if (Excluded(key))
        {
            return null; // compte exclu : Jellyfin répond normalement, effet immédiat
        }

        if (!_recs.TryGetValue(key, out var r))
        {
            return null;
        }

        // heure de la journée : la liste de la tranche horaire de la demande
        if (r.Slots.Count == 4 && Plugin.Instance is { } pl)
        {
            int slot = LocalHourFunc(pl.Configuration)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) / 6;
            if (r.Slots.TryGetValue(slot, out var v))
            {
                return r.WithSlot(v);
            }
        }

        return r;
    }

    /// <summary>Comptes suivis par Reco+ (non ignorés), et ceux qui n'ont pas encore de liste (ni calculée, ni reprise du cache disque).</summary>
    public (int Tracked, List<Guid> Missing) UsersWithoutRecs()
    {
        var tracked = _users.GetUsers().Where(u => !Excluded(u.Id.ToString("N"))).ToList();
        return (tracked.Count, tracked.Where(u => For(u.Id) is null).Select(u => u.Id).ToList());
    }

    /// <summary>Compte ignoré par Reco+ (réglage « Comptes ignorés ») : Jellyfin lui répond normalement partout.</summary>
    public static bool IsExcluded(Guid user) => Excluded(user.ToString("N"));

    private static bool Excluded(string userKey)
        => Plugin.Instance?.Configuration.ExcludedUsers is { Count: > 0 } ex
           && ex.Any(e => Guid.TryParse(e, out var g) && g.ToString("N") == userKey);

    private void EnsureLoaded()
    {
        if (_loaded || Plugin.Instance is null)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(CachePath))
                {
                    var f = JsonSerializer.Deserialize<RecsFile>(File.ReadAllText(CachePath));
                    if (f?.Format == 1)
                    {
                        foreach (var (u, r) in f.Users)
                        {
                            _recs.TryAdd(u, r);
                        }

                        Status.UsersWithRecs = _recs.Count;
                        _log.LogInformation("Reco+: {N} lists reloaded from the disk cache", _recs.Count);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+: unreadable disk cache ({Type}), it will be recomputed", ex.GetType().Name);
            }

            _loaded = true;
        }
    }

    /// <summary>Écrit les listes sur disque si elles ont changé depuis la dernière écriture.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            Persist();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _flush.Dispose();
        Flush();
    }

    private void Persist()
    {
        try
        {
            var f = new RecsFile
            {
                PluginVersion = typeof(RecoService).Assembly.GetName().Version?.ToString() ?? string.Empty,
                Users = new Dictionary<string, UserRecs>(_recs, StringComparer.Ordinal),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath + ".tmp", JsonSerializer.Serialize(f));
            File.Move(CachePath + ".tmp", CachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _log.LogWarning(ex, "Reco+: could not write the cache");
        }
    }

    /// <summary>Oublie le cache (après un changement de réglage, le prochain calcul repart de zéro).</summary>
    public void Invalidate()
    {
        _recs.Clear();
        _extMemo.Clear();
        Persist();
    }

    /// <summary>Dates de Playback Reporting : premier et dernier lancement réels de chaque élément par compte.</summary>
    private sealed record PrDates(IReadOnlyDictionary<(Guid, Guid), double> First, IReadOnlyDictionary<(Guid, Guid), double> Last);

    private PrDates? PlaybackReporting(bool force)
    {
        if (!force && _prCache is not null && DateTime.UtcNow - _prAt < TimeSpan.FromHours(6))
        {
            return _prCache;
        }

        try
        {
            _prCache = ReadPr(out var st);
            Status.PlaybackReporting = st;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or DllNotFoundException or TypeInitializationException)
        {
            // pilote SQLite absent de cet environnement : on s'en passe
            Status.PlaybackReporting = Texts.Pack("indisponible ({0})", ex.GetType().Name);
            _prCache = null;
        }

        _prAt = DateTime.UtcNow;
        return _prCache;
    }

    // anciens lancements rattachés au titre actuel (fichiers remplacés par Radarr/Sonarr) : il faut la bibliothèque
    private PrDates ReadPr(out string status)
    {
        var rows = _pr.FirstStarts(out status);
        var lib = _index.Get();
        var movies = HistoryMath.NameIndex(lib.Titles.Values.Where(t => t.Kind == TitleKind.Movie && !t.External).Select(t => (Guid.ParseExact(t.Id, "N"), t.Name)));
        var series = HistoryMath.NameIndex(lib.Titles.Values.Where(t => t.Kind == TitleKind.Series && !t.External).Select(t => (Guid.ParseExact(t.Id, "N"), t.Name)));
        bool Known(Guid g) => lib.Titles.ContainsKey(g.ToString("N")) || lib.EpisodeSeries.ContainsKey(g);
        var map = HistoryMath.MapPlaybackStarts(rows, Known, movies, series, out var remapped);
        if (remapped > 0)
        {
            status = Texts.Pack("{0}, {1} lectures d'anciens fichiers rattachées", status, remapped);
        }

        return new PrDates(map, HistoryMath.MapPlaybackLasts(rows, Known, movies, series));
    }

    /// <summary>
    /// Recalcule. `only` = un compte (après une lecture) ; null = tous (nuit, changement de bibliothèque).
    /// Un seul calcul à la fois, sur un seul cœur (le calcul est séquentiel).
    /// </summary>
    public async Task ComputeAsync(Guid? only, CancellationToken ct, IProgress<double>? progress = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Status.Running = true;
            var sw = Stopwatch.StartNew();
            await ComputeCore(only, ct, progress).ConfigureAwait(false);
            if (only is null)
            {
                Status.LastFullRun = DateTime.UtcNow;
                Status.LastFullRunSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
                _log.LogInformation("Reco+: full computation finished in {S:0.0} s — {N} accounts, {E} titles outside the library; Seerr: {Seerr}",
                    sw.Elapsed.TotalSeconds, Status.UsersWithRecs, Status.ExternalTitles, Texts.Render(Status.Seerr, "en"));
            }
            else
            {
                Status.LastUserRun = DateTime.UtcNow;
                _log.LogInformation("Reco+: account {User} recomputed in {Ms} ms", _users.GetUserById(only.Value)?.Username ?? only.Value.ToString("N"), sw.ElapsedMilliseconds);
            }

            Status.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status.LastError = $"{DateTime.UtcNow:u} {ex.GetType().Name}: {ex.Message}";
            _log.LogError(ex, "Reco+: computation failed (previous lists are still served)");
        }
        finally
        {
            Status.Running = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Déjà vu ou écarté : regardé (même un peu), masqué ou mal noté. Un titre seulement voulu (liste de suivi, demande
    /// ancienne, favori sans visionnage) reste proposable — c'est même le meilleur candidat.
    /// </summary>
    internal static bool Seen(IReadOnlyDictionary<string, Interaction> h, string id)
        => h.TryGetValue(id, out var x) && (x.Hidden || x.Last != 0 || x.Played || x.Episodes > 0 || x.Completion > 0 || x.Rating is < 0);

    /// <summary>
    /// Déjà connu du compte, donc pas à recommander : vu ou écarté (<see cref="Seen"/>), et — sauf si la configuration
    /// le demande — ce qu'il a déjà marqué : noté (un avis ou un pouce, c'est un titre vu), en favori (titre, saison ou
    /// épisode), et hors bibliothèque, en liste de suivi ou demandé. Ces marques restent des signaux de goût pour tout le
    /// reste.
    /// </summary>
    /// <param name="h">Historique du compte.</param>
    /// <param name="id">Titre.</param>
    /// <param name="proposeMarked">Proposer aussi les titres marqués.</param>
    /// <param name="intentAllowed">
    /// Un titre demandé ou mis de côté reste candidat (titres de la bibliothèque) : classé sans ses propres signaux et
    /// d'abord écarté, il revient peu à peu s'il n'est toujours pas vu (<see cref="Recommender.ScoreAll"/>).
    /// </param>
    internal static bool Known(IReadOnlyDictionary<string, Interaction> h, string id, bool proposeMarked, bool intentAllowed = false)
        => Seen(h, id) || (!proposeMarked && h.TryGetValue(id, out var x)
            && (x.Rating is not null || x.Favorite || x.FavSeasons > 0 || x.FavEpisodes > 0 || (!intentAllowed && (x.Watchlisted > 0 || x.Requested > 0))));

    private static double Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;

    private async Task ComputeCore(Guid? only, CancellationToken ct, IProgress<double>? progress)
    {
        EnsureLoaded();
        var cfg = Plugin.Instance!.Configuration;
        Cpu.Threads = cfg.Threads;
        var s = Settings;
        var lib = _index.Get();
        var pr = PlaybackReporting(only is null);
        // comptes exclus : ni recommandations, ni historique pris en compte pour le foyer
        var users = _users.GetUsers().Where(u => !Excluded(u.Id.ToString("N"))).ToList();
        foreach (var gone in _recs.Keys.Where(Excluded).ToList())
        {
            _recs.TryRemove(gone, out _);
        }

        var lo = cfg.Lists.Sanitized();
        progress?.Report(5);

        // Seerr : relu à chaque calcul complet ; pour un calcul d'un seul compte, réutilisé s'il a moins de 30 min (sinon
        // les demandes faites depuis ne seraient vues qu'au prochain calcul complet, qui n'a plus lieu chaque nuit)
        SeerrSnapshot seerr = _seerr.Last;
        if (_seerr.Configured && (only is null || !seerr.Ok || DateTime.UtcNow - seerr.At > TimeSpan.FromMinutes(30)))
        {
            seerr = await _seerr.RefreshAsync(ct).ConfigureAwait(false);
        }
        else if (!_seerr.Configured)
        {
            seerr = new SeerrSnapshot { Status = "désactivé ou non configuré" };
        }

        Status.Seerr = seerr.Status;

        // historiques (tous les comptes servent : popularité dans le foyer, « vus ensemble »)
        var hist = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal);
        bool needAll = only is null || users.Any(u => !_lastHist.ContainsKey(u.Id.ToString("N")));
        var rows = _userData.ReadAll(needAll ? null : only);   // une seule requête légère pour tous les comptes
        foreach (var u in users)
        {
            var key = u.Id.ToString("N");
            if (needAll || only == u.Id || !_lastHist.TryGetValue(key, out var h))
            {
                h = _history.Build(u.Id, rows.GetValueOrDefault(u.Id), lib, pr?.First, pr?.Last);
            }

            hist[key] = h.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);
        }

        // copie de travail : les demandes Seerr ajoutées ci-dessous ne doivent pas polluer le cache
        _lastHist = hist.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal), StringComparer.Ordinal);
        progress?.Report(25);

        var cl = seerr.Ok ? _seerr.Client() : null;
        var (titles, colls, tmdbOf, reviews, hiddenTmdb) = await Assemble(lib, hist, users, seerr, cl, only is null, cfg, ct).ConfigureAwait(false);

        double now = Unix(DateTime.UtcNow);
        var ds = new Dataset { Titles = titles, Users = hist, Collections = colls, Now = now, Reviews = reviews, LocalHour = LocalHourFunc(cfg) };
        var ctx = new Context(ds, now, hist, s);
        progress?.Report(40);

        // mode étendu : candidats TMDb des titres aimés de chaque compte ciblé
        var targets = only is null ? users : users.Where(u => u.Id == only).ToList();
        var extCands = new Dictionary<string, ExtCandidates>(StringComparer.Ordinal);
        if (cfg.ExtendedEnabled && seerr.Ok && cl is not null)
        {
            // au calcul complet, l'exploration Seerr (la partie longue) n'est refaite que pour les comptes actifs récemment ;
            // les autres gardent leurs titres hors bibliothèque jusqu'à leur prochaine activité
            int activeDays = Math.Clamp(cfg.ExtendedActiveDays, 0, 3650);
            bool Active(Jellyfin.Database.Implementations.Entities.User u)
            {
                var k = u.Id.ToString("N");
                if (only is not null || activeDays == 0 || !_recs.TryGetValue(k, out var prev) || prev.Extended.Count == 0)
                {
                    return true;
                }

                return hist.TryGetValue(k, out var h) && h.Values.Any(x => Math.Max(x.Last, x.Start) >= now - (activeDays * Core.Day));
            }

            var extTargets = targets.Where(Active).ToList();
            if (only is null)
            {
                _log.LogInformation("Reco+: Seerr exploration for {N} active account(s) out of {T}", extTargets.Count, targets.Count);
            }
            for (int i = 0; i < extTargets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var uk = extTargets[i].Id.ToString("N");
                if (only is not null && _extMemo.TryGetValue(uk, out var memo) && DateTime.UtcNow - memo.At < ExtMemoTtl)
                {
                    extCands[uk] = Reuse(memo.C, hist.GetValueOrDefault(uk), lib, seerr, titles, colls, cfg);
                    continue;
                }

                var explored = await ExtendedCandidates(extTargets[i], ctx, lib, tmdbOf, seerr, cl, titles, colls, cfg, ct).ConfigureAwait(false);
                if (_seerr.Down)
                {
                    _log.LogWarning("Reco+: Seerr went down during exploration; {N} account(s) keep their previous titles outside the library", extTargets.Count - i);
                    break; // Seerr a lâché en route : ce compte et les suivants gardent leurs titres hors bibliothèque précédents
                }

                extCands[uk] = explored;
                _extMemo[uk] = (DateTime.UtcNow, explored);
                progress?.Report(40 + (20.0 * (i + 1) / extTargets.Count));
            }

            _seerr.SaveCache();
        }

        Status.ExternalTitles = titles.Values.Count(t => t.External);
        ds.LinkCollections();
        var vec = Core.BuildVectors(titles, s);
        // produits scalaires gardés le temps du calcul (classement avec diversité : ~10× plus rapide, mêmes listes) ;
        // budget mémoire borné, au-delà calcul direct
        var simIdx = new SimilarityIndex(vec, 8_000_000);
        progress?.Report(60);

        int done = 0;
        // droits d'accès et rangées Home Screen Sections de chaque compte : lus d'abord, un compte à la fois (bibliothèque
        // de Jellyfin, autre plugin) ; ensuite les comptes sont calculés en parallèle (réglage « Threads »)
        var prep = targets.Select(u => (U: u, Accessible: _history.Accessible(u), Rows: RowOptionsOf(cfg, HomeScreenSectionsIntegration.EnabledFor(_services, u.Id)))).ToList();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Cpu.Degree, CancellationToken = ct };
        Parallel.ForEach(prep, po, job =>
        {
            var u = job.U;
            var uc = ctx.Fork(); // tranche horaire propre à ce calcul, le reste partagé (lecture seule)
            var key = u.Id.ToString("N");
            var h = hist[key];
            var accessible = job.Accessible;
            var cands = accessible.Where(id => titles.TryGetValue(id, out var ct0) && !Known(h, id, cfg.ProposeMarked, intentAllowed: true) && ListPolicy.Allows(ct0, lo))
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            var r = new UserRecs { ComputedAt = DateTime.UtcNow, SeerrUserId = seerr.UserMap.GetValueOrDefault(key) };

            // en tête : les nouveautés de ce que le compte suit (nouveaux épisodes, nouvelle saison, volet suivant d'une
            // saga), pendant un temps ; jamais un titre qu'il a lui-même demandé (il sait déjà qu'il le veut)
            var pinned = Pins.Select(ds, h, uc.Sig.GetValueOrDefault(key) ?? new Dictionary<string, double>(), accessible.Contains,
                id => Guid.TryParse(id, out var sg) && lib.EpisodeDates.TryGetValue(sg, out var ed) ? ed : null, now,
                new PinOptions(Math.Clamp(cfg.NewEpisodesPinned, 0, 20), Math.Clamp(cfg.NewEpisodesMaxDays, 1, 365), s.SagaMax));
            r.Pinned = pinned;

            // rangées que HSS montre à ce compte : les autres ne « réservent » aucun titre
            var rowOpts = job.Rows;

            // montré sans être ouvert (seulement ce que le compte a vraiment vu à l'écran)
            var expo = cfg.TrackImpressions && s.WImpression > 0 ? _impressions.For(key, now) : null;
            var views = new List<HomeView>();
            if (cands.Count > 0)
            {
                // heure de la journée : une liste par tranche horaire, servie selon l'heure de la demande
                var slots = s.WTimeOfDay > 0 ? new int?[] { 0, 1, 2, 3 } : new int?[] { null };
                foreach (var slot in slots)
                {
                    uc.Slot = slot;
                    var sc = Recommender.ScoreAll(uc, vec, key, cands, s, exposure: expo);
                    var ranked = Recommender.Rank(uc, vec, cands, sc, Math.Clamp(cfg.ListSize, 20, 1000), s, key, simIdx).Where(id => !pinned.Contains(id)).ToList();

                    // présentation : hasard du jour et minimums films/séries (le classement par goût reste intact pour les catégories)
                    var pool = cands.Where(id => !pinned.Contains(id)).ToList();
                    var shaped = ListPolicy.Shape(ranked, pool, id => titles.TryGetValue(id, out var kt) ? (TitleKind?)kt.Kind : null, lo, ListPolicy.Seed(key, DateTime.UtcNow));

                    // rangées d'accueil supplémentaires, sans doublons entre elles ni avec « Pour toi »
                    var shownSet = new HashSet<string>(shaped, StringComparer.Ordinal);
                    var ordered = shaped.Concat(pool.Where(id => !shownSet.Contains(id)).OrderByDescending(id => sc[id]).ThenBy(id => id, StringComparer.Ordinal)).ToList();
                    var home = HomeRows.Allocate(uc, vec, key, r.Pinned.Concat(shaped).ToList(), ordered, sc, rowOpts);
                    var view = new SlotRecs
                    {
                        Ranked = shaped,
                        Taste = shaped.ToDictionary(id => id, id => Math.Round(sc[id], 5), StringComparer.Ordinal),
                        Rows = home.Rows,
                        RowAnchors = home.Anchors,
                    };
                    views.Add(new HomeView(slot, view, shaped, ordered, sc));
                    if (slot is int sl)
                    {
                        r.Slots[sl] = view;
                    }

                    if (slot is null || slot == uc.Ds.HourOf(now) / 6)
                    {
                        // vue par défaut (et seule vue sans heure de la journée)
                        (r.Ranked, r.Taste, r.Rows, r.RowAnchors) = (view.Ranked, view.Taste, view.Rows, view.RowAnchors);
                        r.MovieCategories = Categories(uc, vec, key, ranked, titles);
                        views[^1].Default = true;
                    }
                }

                uc.Slot = null;
            }

            var hid = hiddenTmdb.GetValueOrDefault(key);
            var extIds = extCands.TryGetValue(key, out var ex)
                ? ex.Ids.Where(id => ListPolicy.Allows(titles[id], lo) && (hid is null || !hid.Contains(id.StartsWith("tmdb:", StringComparison.Ordinal) ? id[5..] : id))).ToList()
                : new List<string>();
            if (ex is not null && extIds.Count > 0)
            {
                // bruit du jour (réglable) : les titres de valeur proche changent de place d'un jour à l'autre
                double noise = double.IsFinite(cfg.ExtendedNoise) ? Math.Clamp(cfg.ExtendedNoise, 0, 1) : 0;
                string noiseSeed = key + "|" + Exploration.Day(DateTime.UtcNow);
                var sc = Exploration.Noisy(Recommender.Score(uc, vec, key, extIds, s, ex.Agree, exposure: expo), noise, noiseSeed);
                var extRanked = Recommender.Rank(uc, vec, extIds, sc, Math.Clamp(cfg.ExtendedCount, 1, 100), s, key, simIdx);
                var recs = extRanked.Select(id => (Id: id, Rec: ToExternalRec(id, titles, ex.Because, ex.Reasons))).Where(p => p.Rec is not null).ToList();
                foreach (var p in recs)
                {
                    p.Rec!.Source = ex.SourceOf.GetValueOrDefault(p.Id, string.Empty);
                }

                r.Extended = recs.Select(p => p.Rec!).ToList();
                List<string>? placedFrom = null;

                // accueil : titres hors bibliothèque dans les rangées qui en reçoivent, pour chaque vue (tranche horaire)
                if (cfg.AnyExternalRow() && views.Count > 0)
                {
                    foreach (var hv in views)
                    {
                        uc.Slot = hv.Slot;
                        var placed = PlaceExternals(uc, vec, key, r.Pinned, recs.Select(p => (p.Id, p.Rec!)).ToList(), hv, rowOpts, cfg, s, expo, noise, noiseSeed, titles, now);
                        if (hv.Default)
                        {
                            (r.Rows, r.RowAnchors, r.Layouts) = (hv.View.Rows, hv.View.RowAnchors, hv.View.Layouts);
                            foreach (var (rec, row, after) in placed)
                            {
                                (rec.Row, rec.After) = (row, after);
                            }

                            placedFrom = placed.Select(x => x.Rec.Source).ToList();
                        }
                    }

                    uc.Slot = null;
                }

                // bilan par source (aperçu) : trouvés, détaillés et autorisés, proposés, placés dans la rangée
                r.Sources = Exploration.Stats(ex.Found, extIds.Select(id => ex.SourceOf.GetValueOrDefault(id, string.Empty)), r.Extended.Select(e => e.Source), placedFrom);
            }

            // pas d'exploration pour ce compte (inactif, ou Seerr injoignable) : ses titres hors bibliothèque précédents restent
            // (sans ce qui a changé depuis : vu, masqué, demandé, arrivé dans la bibliothèque, ou interdit au compte)
            if (cfg.ExtendedEnabled && !extCands.ContainsKey(key) && _recs.TryGetValue(key, out var prevRecs))
            {
                var policy = PolicyOf(u);
                var country = cfg.ClassificationCountry();
                r.Extended = prevRecs.Extended.Where(e =>
                {
                    var k = e.MediaType + ":" + e.TmdbId;
                    var d = _seerr.CachedDetails(e.MediaType, e.TmdbId);
                    return !Known(h, "tmdb:" + k, cfg.ProposeMarked) && !lib.ByTmdb.ContainsKey(k) && !seerr.Requested.Contains(k) && hid?.Contains(k) != true
                           && (d is null || (d.MediaStatus is not (2 or 3 or 4 or 5 or 6) && ParentalAllowed(policy, d, country)));
                }).ToList();
                r.Sources = prevRecs.Sources;
                // leur place dans les rangées aussi (une rangée recalculée depuis se recompose sans ce qui a disparu)
                var still = r.Extended.Select(e => e.MediaType + ":" + e.TmdbId).ToHashSet(StringComparer.Ordinal);
                r.Layouts = prevRecs.Layouts.ToDictionary(kv => kv.Key, kv => kv.Value.Where(t => t.Length == 0 || still.Contains(t)).ToList(), StringComparer.Ordinal);
            }

            _recs[key] = r;
            progress?.Report(60 + (40.0 * Interlocked.Increment(ref done) / Math.Max(1, targets.Count)));
        });

        lock (_vecLock)
        {
            (_library, _vectors) = (lib, vec);
        }

        Status.UsersWithRecs = _recs.Count;
        if (only is null)
        {
            Interlocked.Exchange(ref _dirty, 0);
            Persist();
        }
        else
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    /// <summary>Fuseau des heures de visionnage : celui de la configuration (identifiant IANA), sinon celui du serveur.</summary>
    internal static TimeZoneInfo Zone(Configuration.PluginConfiguration cfg)
    {
        var id = cfg.TimeZone?.Trim() ?? string.Empty;
        var cached = _zone;
        if (cached is not null && cached.Value.Id == id)
        {
            return cached.Value.Zone;
        }

        var z = FindZone(cfg);
        _zone = (id, z);
        return z;
    }

    private static TimeZoneInfo FindZone(Configuration.PluginConfiguration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.TimeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(cfg.TimeZone.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // fuseau inconnu : celui du serveur
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>Heure locale d'un instant (secondes Unix), selon le fuseau configuré.</summary>
    internal static Func<double, int> LocalHourFunc(Configuration.PluginConfiguration cfg)
    {
        var tz = Zone(cfg);
        return t => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(t), tz).Hour;
    }

    /// <summary>
    /// Rangées d'accueil à remplir pour un compte : celles que Home Screen Sections lui affiche (null = toutes, si HSS
    /// ne dit rien). « Pour toi » est toujours remplie (Suggestions, mode étendu).
    /// </summary>
    internal static RowOptions RowOptionsOf(Configuration.PluginConfiguration cfg, HashSet<string>? shown)
    {
        bool On(string row) => shown is null || shown.Contains(row);
        int because = 0;
        for (int i = 1; i <= 3; i++)
        {
            if (On(HomeRows.ParceQue + i))
            {
                because = i;
            }
        }

        return new RowOptions
        {
            Size = Math.Clamp(cfg.RowSize, 5, 60),
            Movies = On(HomeRows.Films),
            Series = On(HomeRows.Series),
            New = On(HomeRows.Nouveautes),
            NewDays = Math.Clamp(cfg.RowNewDays, 1, 365),
            Because = because,
        };
    }

    /// <summary>Une vue des rangées d'accueil (tranche horaire) et ce qui a servi à la calculer.</summary>
    private sealed class HomeView(int? slot, SlotRecs view, List<string> shaped, List<string> ordered, Dictionary<string, double> sc)
    {
        public int? Slot { get; } = slot;

        public SlotRecs View { get; } = view;

        public List<string> Shaped { get; } = shaped;

        public List<string> Ordered { get; } = ordered;

        public Dictionary<string, double> Sc { get; } = sc;

        public bool Default { get; set; }
    }

    /// <summary>
    /// Titres hors bibliothèque sur l'accueil : chaque rangée reçoit ceux qui lui vont (Films : films, Séries : séries,
    /// Nouveautés : sortis récemment, « Parce que … » : proches du titre aimé, « Pour toi » : tous), selon le contenu
    /// choisi (Seerr seul ou mélange) ; un titre ne va que dans une rangée. La répartition des rangées est refaite : les
    /// titres de la bibliothèque sortis d'une rangée passent aux suivantes. Met à jour la vue (rangées et composition) et
    /// renvoie les titres placés, leur rangée et leur place.
    /// </summary>
    private static List<(ExternalRec Rec, string Row, int After)> PlaceExternals(Context ctx, IReadOnlyDictionary<string, SparseVector> vec,
        string key, List<string> pinned, List<(string Id, ExternalRec Rec)> recs, HomeView hv, RowOptions rowOpts,
        Configuration.PluginConfiguration cfg, EngineSettings s, IReadOnlyDictionary<string, Exposure>? expo, double noise, string noiseSeed,
        IReadOnlyDictionary<string, Title> titles, double now)
    {
        int size = Math.Clamp(cfg.RowSize, 5, 60);
        // score commun (ce que bibliothèque et hors bibliothèque ont en commun), même bruit du jour pour les titres hors bibliothèque
        var joint = Recommender.ScoreAll(ctx, vec, key, hv.Ordered.Concat(recs.Select(p => p.Id)).Distinct().ToList(), Recommender.Shared(s), exposure: expo);
        var jointExt = Exploration.Noisy(recs.ToDictionary(p => p.Id, p => joint[p.Id], StringComparer.Ordinal), noise, noiseSeed);
        double Key(string id) => jointExt.TryGetValue(id, out var e) ? e : joint.GetValueOrDefault(id);

        var mix = new MixOptions
        {
            Free = cfg.ExtendedMixMode == Configuration.MixMode.Score,
            Every = Math.Clamp(cfg.ExtendedMixEvery, 2, 20),
            MinFree = Math.Clamp(cfg.ExtendedMinFree, 0, 20),
            MaxFree = Math.Clamp(cfg.ExtendedMaxFree, 0, 60),
            Discovery = Math.Clamp(cfg.ExtendedDiscoverySlots, 0, 10),
        };
        int discoveryVotes = Math.Clamp(cfg.ExtendedGemMinVotes, 0, 1_000_000);
        double releasedCut = now - (Math.Clamp(cfg.RowNewReleasedDays, 1, 3650) * Core.Day);
        var taken = new HashSet<int>();
        var placed = new List<(ExternalRec, string, int)>();
        var layouts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pinnedSet = new HashSet<string>(pinned, StringComparer.Ordinal);

        int Fit(string row, string? anchor, IReadOnlyList<string> shown)
        {
            var source = cfg.SourceOf(row);
            if (source == RowSource.Local)
            {
                return -1;
            }

            // demandes et nouvelles saisons en tête de « Pour toi » : jamais déplacées
            int skip = row == HomeRows.PourToi ? shown.TakeWhile(pinnedSet.Contains).Count() : 0;
            var lib = shown.Skip(skip).ToList();
            bool Fits(Title t) => row switch
            {
                HomeRows.Films => t.Kind == TitleKind.Movie,
                HomeRows.Series => t.Kind == TitleKind.Series,
                HomeRows.Nouveautes => (t.Released ?? 0) >= releasedCut && t.Released <= now,
                _ => true,
            };
            var cand = Enumerable.Range(0, recs.Count).Where(i => !taken.Contains(i) && titles.TryGetValue(recs[i].Id, out var t) && Fits(t)).ToList();

            // clé de classement : celle de la rangée (« Parce que … » : surtout la ressemblance avec le titre aimé)
            Func<string, double> rank = Key;
            if (anchor is not null && vec.TryGetValue(anchor, out var av))
            {
                double Sim(string id) => vec.TryGetValue(id, out var v) ? av.Dot(v) : 0;
                cand = cand.Where(i => Sim(recs[i].Id) >= HomeRows.MinSimilarity).ToList();
                var all = lib.Concat(cand.Select(i => recs[i].Id)).Select(Key).ToList();
                double min = all.Count > 0 ? all.Min() : 0, span = all.Count > 0 ? all.Max() - min : 0;
                rank = id => (0.7 * Sim(id)) + (0.3 * (span > 0 ? (Key(id) - min) / span : 0));
            }

            if (cand.Count == 0)
            {
                return -1; // rien pour cette rangée : la bibliothèque seule
            }

            cand = cand.OrderByDescending(i => rank(recs[i].Id)).ThenBy(i => i).ToList();
            // places découverte : les mieux notés (note fiable, votes minimum des pépites), sinon un 9,2 sur 4 votes passe devant tout
            var discovery = Enumerable.Range(0, cand.Count)
                .Where(j => (titles[recs[cand[j]].Id].Votes ?? 0) >= discoveryVotes)
                .OrderByDescending(j => Exploration.ConfidentRating(titles[recs[cand[j]].Id].Community, titles[recs[cand[j]].Id].Votes))
                .ThenBy(j => j);
            var (after, kept) = HomeRows.PlaceRow(lib.Select(rank).ToList(), cand.Select(i => rank(recs[i].Id)).ToList(), source, mix, discovery, size - skip);
            var mine = source == RowSource.Seerr
                ? Enumerable.Range(0, Math.Min(cand.Count, size - skip)).ToList()
                : Enumerable.Range(0, cand.Count).Where(j => after[j] < lib.Count).ToList();
            if (mine.Count == 0)
            {
                return -1;
            }

            // composition : demandes en tête, puis bibliothèque et hors bibliothèque dans l'ordre des places
            var layout = Enumerable.Repeat(string.Empty, skip).ToList();
            for (int l = 0; l <= kept; l++)
            {
                foreach (var j in mine.Where(j => after[j] == l))
                {
                    var rec = recs[cand[j]].Rec;
                    layout.Add(rec.MediaType + ":" + rec.TmdbId);
                    taken.Add(cand[j]);
                    placed.Add((rec, row, l));
                }

                if (l < kept)
                {
                    layout.Add(string.Empty);
                }
            }

            layouts[row] = layout;
            return skip + kept;
        }

        var home = HomeRows.Allocate(ctx, vec, key, pinned.Concat(hv.Shaped).ToList(), hv.Ordered, hv.Sc, rowOpts, Fit);
        (hv.View.Rows, hv.View.RowAnchors, hv.View.Layouts) = (home.Rows, home.Anchors, layouts);
        return placed;
    }

    /// <summary>
    /// Données d'un calcul à partir des historiques : titres (copies, date d'ajout effective), sagas, votes TMDb (fiches
    /// Seerr en cache, complétées au calcul complet), demandes et listes de suivi Seerr, masquages et avis de Jellyfin
    /// Enhanced. Sans client Seerr (calibrage), rien n'est demandé à Seerr : seul le cache sert. Modifie les historiques.
    /// </summary>
    private async Task<(Dictionary<string, Title> Titles, Dictionary<string, List<string>> Colls, Dictionary<string, string> TmdbOf,
        Dictionary<string, Dictionary<string, double>> Reviews, Dictionary<string, HashSet<string>> HiddenTmdb)> Assemble(LibrarySnapshot lib,
        Dictionary<string, Dictionary<string, Interaction>> hist, IReadOnlyList<User> users, SeerrSnapshot seerr, SeerrClient? cl, bool full,
        Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        // titres : copies de la bibliothèque (date d'ajout effective = min(ajout, premier contact connu))
        var titles = lib.Titles.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);
        foreach (var h in hist.Values)
        {
            foreach (var (tid, x) in h)
            {
                if (x.Start != 0 && titles.TryGetValue(tid, out var t) && (t.Added is null || x.Start < t.Added))
                {
                    t.Added = x.Start;
                }
            }
        }

        var colls = lib.Collections.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        var tmdbOf = lib.ByTmdb.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

        // confiance dans les notes : nombre de votes TMDb, lu dans les fiches Seerr en cache ; au calcul complet, on
        // complète au plus 150 fiches manquantes (le cache se remplit en quelques nuits, sans charger Seerr)
        if (cfg.UseSeerrVotes)
        {
            var keys = new List<(Title T, string Type, int Tmdb)>();
            foreach (var (id, t) in titles)
            {
                var parts = !t.External && tmdbOf.TryGetValue(id, out var tk) ? tk.Split(':') : null;
                if (parts is { Length: 2 } && int.TryParse(parts[1], out var tid))
                {
                    keys.Add((t, parts[0], tid));
                }
            }

            // au plus 150 fiches manquantes par calcul complet, demandées en parallèle
            if (full && seerr.Ok && cl is not null)
            {
                await Task.WhenAll(keys.Where(k => _seerr.CachedDetails(k.Type, k.Tmdb)?.VoteCount is null).Take(150)
                    .Select(k => _seerr.DetailsAsync(cl, k.Type, k.Tmdb, ct, requireVotes: true))).ConfigureAwait(false);
                _seerr.SaveCache();
            }

            foreach (var (t, type, tmdb) in keys)
            {
                if (_seerr.CachedDetails(type, tmdb)?.VoteCount is int v)
                {
                    t.Votes = v;
                }
            }
        }

        // demandes Seerr → signal (titre voulu) ; titres demandés absents → titres externes (profil seulement)
        if (seerr.Ok)
        {
            // fiches des titres demandés hors bibliothèque : chargées en parallèle d'abord (ensuite lues dans le cache) ;
            // sans client (calibrage) : le cache seul
            await Task.WhenAll(cl is null ? Array.Empty<Task>() : seerr.RequestsByUser.Where(kv => hist.ContainsKey(kv.Key)).SelectMany(kv => kv.Value)
                .Where(r => r.RequestStatus != 3 && !lib.ByTmdb.ContainsKey(r.MediaType + ":" + r.TmdbId))
                .DistinctBy(r => r.MediaType + ":" + r.TmdbId)
                .Select(r => (Task)_seerr.DetailsAsync(cl, r.MediaType, r.TmdbId, ct)).ToArray()).ConfigureAwait(false);

            foreach (var (jf, reqs) in seerr.RequestsByUser)
            {
                if (!hist.TryGetValue(jf, out var h))
                {
                    continue;
                }

                foreach (var r in reqs.Where(r => r.RequestStatus != 3)) // refusées ignorées
                {
                    var key = r.MediaType + ":" + r.TmdbId;
                    string? id = lib.ByTmdb.TryGetValue(key, out var lid) ? lid : null;
                    if (id is null)
                    {
                        var d = cl is null ? _seerr.CachedDetails(r.MediaType, r.TmdbId) : await _seerr.DetailsAsync(cl, r.MediaType, r.TmdbId, ct).ConfigureAwait(false);
                        if (d is null || d.Adult)
                        {
                            continue;
                        }

                        id = "tmdb:" + key;
                        if (!titles.ContainsKey(id))
                        {
                            titles[id] = ExternalTitle(d, id, colls);
                        }
                    }

                    if (!h.TryGetValue(id, out var x))
                    {
                        h[id] = x = new Interaction { Title = id };
                    }

                    x.Requested = x.Requested == 0 ? Unix(r.CreatedAt) : Math.Min(x.Requested, Unix(r.CreatedAt));
                }
            }

            // liste de suivi Seerr : intention plus faible qu'une demande, même traitement (titres de la bibliothèque,
            // ou titres externes déjà connus — on n'interroge pas Seerr pour chaque titre suivi)
            if (cfg.UseSeerrWatchlist)
            {
                foreach (var (jf, list) in seerr.WatchlistByUser)
                {
                    if (!hist.TryGetValue(jf, out var h))
                    {
                        continue;
                    }

                    foreach (var w in list)
                    {
                        var key = w.MediaType + ":" + w.TmdbId;
                        string? id = lib.ByTmdb.TryGetValue(key, out var lid) ? lid : titles.ContainsKey("tmdb:" + key) ? "tmdb:" + key : null;
                        if (id is null)
                        {
                            var d = _seerr.CachedDetails(w.MediaType, w.TmdbId);
                            if (d is null || d.Adult)
                            {
                                continue;
                            }

                            id = "tmdb:" + key;
                            titles[id] = ExternalTitle(d, id, colls);
                        }

                        if (!h.TryGetValue(id, out var x))
                        {
                            h[id] = x = new Interaction { Title = id };
                        }

                        double at = Unix(w.CreatedAt);
                        x.Watchlisted = x.Watchlisted == 0 ? at : Math.Min(x.Watchlisted, at);
                    }
                }
            }
        }

        // Jellyfin Enhanced : « pas intéressé » (masqué partout) et avis notés — ce que le compte a dit explicitement
        var hiddenTmdb = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var reviews = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        if (cfg.UseJellyfinEnhanced)
        {
            var fb = _je.ReadAll(users.Select(u => u.Id));
            Status.JellyfinEnhanced = _je.State;
            foreach (var (jf, f) in fb)
            {
                if (!hist.TryGetValue(jf, out var h))
                {
                    continue;
                }

                hiddenTmdb[jf] = f.HiddenTmdb;
                Interaction Get(string id) => h.TryGetValue(id, out var x) ? x : h[id] = new Interaction { Title = id };
                foreach (var id in f.HiddenItems.Where(titles.ContainsKey)
                             .Concat(f.HiddenTmdb.Select(k => lib.ByTmdb.GetValueOrDefault(k)).OfType<string>()).Distinct())
                {
                    Get(id).Hidden = true;
                }

                foreach (var (k, stars) in f.Ratings)
                {
                    var id = lib.ByTmdb.TryGetValue(k, out var lid) ? lid : titles.ContainsKey("tmdb:" + k) ? "tmdb:" + k : null;
                    if (id is not null)
                    {
                        Get(id).Rating = HistoryMath.ReviewRating(stars); // un avis prime sur la note ou le pouce Jellyfin
                        if (!reviews.TryGetValue(id, out var by))
                        {
                            reviews[id] = by = new Dictionary<string, double>(StringComparer.Ordinal);
                        }

                        by[jf] = stars;                                    // avis du foyer, pour les autres comptes
                    }
                }
            }
        }
        else
        {
            Status.JellyfinEnhanced = "désactivé";
        }

        return (titles, colls, tmdbOf, reviews, hiddenTmdb);
    }

    /// <summary>
    /// Jeu de données pour le calibrage : les mêmes données que le calcul (comptes ignorés exclus, historiques complets,
    /// demandes et listes de suivi Seerr, votes TMDb, masquages et avis de Jellyfin Enhanced), sans aucun appel à Seerr :
    /// ce qui vient de Seerr est lu dans le dernier relevé et dans le cache des fiches.
    /// </summary>
    public async Task<Dataset> BuildCalibrationDatasetAsync(CancellationToken ct = default)
    {
        var cfg = Plugin.Instance!.Configuration;
        var lib = _index.Get();
        var pr = PlaybackReporting(true);
        var users = _users.GetUsers().Where(u => !Excluded(u.Id.ToString("N"))).ToList();
        var hist = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal);
        var rows = _userData.ReadAll();
        foreach (var u in users)
        {
            hist[u.Id.ToString("N")] = _history.Build(u.Id, rows.GetValueOrDefault(u.Id), lib, pr?.First, pr?.Last).ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);
        }

        var seerr = _seerr.Configured ? _seerr.Last : new SeerrSnapshot { Status = "désactivé ou non configuré" };
        var (titles, colls, _, reviews, _) = await Assemble(lib, hist, users, seerr, null, false, cfg, ct).ConfigureAwait(false);
        var ds = new Dataset
        {
            Titles = titles,
            Users = hist,
            Collections = colls,
            Reviews = reviews,
            Now = Unix(DateTime.UtcNow),
            LocalHour = LocalHourFunc(cfg),
        };
        ds.LinkCollections();
        return ds;
    }

    /// <summary>Genres présents dans la bibliothèque (page de configuration).</summary>
    public List<string> Genres()
        => (Library ?? _index.Current)?.Titles.Values.SelectMany(t => t.Genres).Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(ListOptions.Normalize).Select(g => g.First()).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList()
           ?? new List<string>();

    /// <summary>Aperçu lisible des 20 premiers titres d'un compte (page de configuration).</summary>
    public List<string> Preview(Guid user, string lang, int n = 20)
    {
        var r = For(user);
        var lib = Library ?? _index.Current;
        if (r is null)
        {
            return new List<string>();
        }

        string series = " (" + Texts.T("série", lang) + ")";
        string Name(string id) => lib is not null && lib.Titles.TryGetValue(id, out var t) ? t.Name + (t.Kind == TitleKind.Series ? series : string.Empty) : id;
        return r.Pinned.Select(id => "📌 " + Name(id)).Concat(r.Ranked.Select(Name)).Take(n).ToList();
    }

    /// <summary>Aperçu des rangées d'accueil supplémentaires d'un compte : libellé → titres (page de configuration).</summary>
    public Dictionary<string, List<string>> PreviewRows(Guid user, string lang)
    {
        var r = For(user);
        var lib = Library ?? _index.Current;
        var cfg = Plugin.Instance?.Configuration;
        var res = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (r is null || cfg is null)
        {
            return res;
        }

        string series = " (" + Texts.T("série", lang) + ")";
        string Name(string id) => lib is not null && lib.Titles.TryGetValue(id, out var t) ? t.Name + (t.Kind == TitleKind.Series ? series : string.Empty) : id;
        int size = Math.Clamp(cfg.RowSize, 5, 60);
        foreach (var (row, ids) in r.Rows.Where(kv => kv.Key != HomeRows.PourToi))
        {
            string label = row is HomeRows.Films or HomeRows.Series or HomeRows.Nouveautes
                ? HomeScreenSectionsIntegration.RowTitle(cfg, row, lang)
                : r.RowAnchors.TryGetValue(row, out var a) ? HomeScreenSectionsIntegration.Because(cfg, a, lang) : row;
            res[label] = ids.Take(size).Select(Name).ToList();
        }

        return res;
    }

    private ExternalRec? ToExternalRec(string id, Dictionary<string, Title> titles, Dictionary<string, string> because, Dictionary<string, string> reasons)
    {
        // id = "tmdb:movie:123"
        var parts = id.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out var tmdb))
        {
            return null;
        }

        var d = _seerr.CachedDetails(parts[1], tmdb);
        var t = titles[id];
        return new ExternalRec
        {
            MediaType = parts[1],
            TmdbId = tmdb,
            Name = t.Name,
            Year = t.Year,
            PosterPath = d?.PosterPath,
            BackdropPath = d?.BackdropPath,
            Vote = t.Community ?? 0,
            Because = because.GetValueOrDefault(id, string.Empty),
            Reason = reasons.GetValueOrDefault(id, string.Empty),
        };
    }

    private static Title ExternalTitle(SeerrDetails d, string id, Dictionary<string, List<string>> colls)
    {
        var people = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Actor"] = d.Actors,
            ["Director"] = d.Directors,
            ["Writer"] = d.Writers,
            ["Creator"] = d.Creators,
        };
        if (d.CollectionId is int c)
        {
            var cid = "tmdbc:" + c;
            if (!colls.TryGetValue(cid, out var l))
            {
                colls[cid] = l = new List<string>();
            }

            if (!l.Contains(id))
            {
                l.Add(id);
            }
        }

        return new Title
        {
            Id = id,
            Kind = d.MediaType == "tv" ? TitleKind.Series : TitleKind.Movie,
            Name = d.Name,
            Year = d.Year,
            Genres = d.Genres,
            Tags = d.Keywords,
            Studios = d.Studios,
            People = people,
            Community = d.Vote > 0 ? d.Vote : null,
            Added = null,
            EpisodeCount = d.EpisodeCount,
            External = true,
            Votes = d.VoteCount,
            Released = DateTime.TryParse(d.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var rd) ? Unix(rd) : null,
            Language = d.OriginalLanguage,
            Countries = d.Countries,
        };
    }

    /// <summary>Contrôle parental d'un compte, lu comme Jellyfin (et Jellyfin Enhanced) le lit.</summary>
    private sealed record ParentalPolicy(int? Max, int? MaxSub, bool BlockUnratedMovies, bool BlockUnratedSeries, string[] Blocked, string[] Allowed);

    private static ParentalPolicy PolicyOf(User u)
    {
        var unrated = u.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems) ?? Array.Empty<UnratedItem>();
        return new ParentalPolicy(u.MaxParentalRatingScore, u.MaxParentalRatingSubScore, unrated.Contains(UnratedItem.Movie), unrated.Contains(UnratedItem.Series),
            u.GetPreference(PreferenceKind.BlockedTags) ?? Array.Empty<string>(), u.GetPreference(PreferenceKind.AllowedTags) ?? Array.Empty<string>());
    }

    /// <summary>
    /// Titre hors bibliothèque autorisé pour le compte, avec les mêmes règles que Jellyfin Enhanced (qui refuse sinon la
    /// fiche) : classification du pays choisi seulement, titre non classé autorisé sauf si le compte bloque les non classés
    /// de cette sorte, deux plafonds si le compte a un sous-niveau, étiquettes bloquées / autorisées.
    /// </summary>
    private bool ParentalAllowed(ParentalPolicy p, SeerrDetails d, string country)
    {
        if (d.Adult)
        {
            return false;
        }

        int? score = null, sub = null;
        if (d.Certifications.TryGetValue(country, out var cert) && !string.IsNullOrWhiteSpace(cert) && _loc.GetRatingScore(cert, country) is { } rs)
        {
            (score, sub) = (rs.Score, rs.SubScore);
        }

        return Parental.IsAllowed(score, sub, d.MediaType == "tv" ? p.BlockUnratedSeries : p.BlockUnratedMovies, p.Max, p.MaxSub)
               && Parental.TagsAllowed(d.Keywords, d.Genres, p.Blocked, p.Allowed);
    }

    /// <summary>Candidats hors bibliothèque d'un compte : identifiants, accord TMDb, titre aimé d'origine et raison affichée.</summary>
    private sealed record ExtCandidates(
        List<string> Ids, Dictionary<string, double> Agree, Dictionary<string, string> Because, Dictionary<string, string> Reasons,
        Dictionary<string, string> SourceOf, Dictionary<string, int> Found, Dictionary<string, SeerrDetails> Details);

    /// <summary>
    /// Exploration précédente reprise : ses titres hors bibliothèque sont remis dans le jeu de données, sauf ceux qui ne
    /// sont plus à proposer (arrivés dans la bibliothèque, demandés, vus ou marqués depuis).
    /// </summary>
    private static ExtCandidates Reuse(ExtCandidates c, IReadOnlyDictionary<string, Interaction>? h, LibrarySnapshot lib, SeerrSnapshot seerr,
        Dictionary<string, Title> titles, Dictionary<string, List<string>> colls, Configuration.PluginConfiguration cfg)
    {
        h ??= new Dictionary<string, Interaction>();
        var ids = new List<string>();
        foreach (var id in c.Ids)
        {
            var k = id[5..];
            if (!c.Details.TryGetValue(id, out var d) || lib.ByTmdb.ContainsKey(k) || seerr.Requested.Contains(k) || Known(h, id, cfg.ProposeMarked))
            {
                continue;
            }

            if (!titles.ContainsKey(id))
            {
                titles[id] = ExternalTitle(d, id, colls);
            }

            ids.Add(id);
        }

        return c with { Ids = ids };
    }

    /// <summary>
    /// Candidats hors bibliothèque, de plusieurs sources fusionnées à parts équilibrées : recommandations et titres
    /// similaires des titres aimés, volets manquants des sagas, filmographies des auteurs récurrents, et les mieux notés
    /// des thèmes, genres, studios, langues préférés et de tout TMDb. Les listes triées par note sont lues à des pages
    /// tirées au hasard du jour. Seuls les titres retenus ont une fiche détaillée ; le moteur les classe ensuite tous de la
    /// même façon.
    /// </summary>
    private async Task<ExtCandidates> ExtendedCandidates(
        User u, Context ctx, LibrarySnapshot lib, Dictionary<string, string> tmdbOf, SeerrSnapshot seerr, SeerrClient cl,
        Dictionary<string, Title> titles, Dictionary<string, List<string>> colls, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        var key = u.Id.ToString("N");
        var day = Exploration.Day(DateTime.UtcNow);
        var sig = ctx.Sig.GetValueOrDefault(key) ?? new Dictionary<string, double>();
        var anchors = sig.Where(e => e.Value > 0).OrderByDescending(e => e.Value)
            .Select(e => (Id: e.Key, Tmdb: tmdbOf.GetValueOrDefault(e.Key) ?? (e.Key.StartsWith("tmdb:", StringComparison.Ordinal) ? e.Key[5..] : null), Weight: e.Value))
            .Where(a => a.Tmdb is not null)
            .Take(Math.Clamp(cfg.ExtendedAnchors, 1, 500))
            .ToList();
        var seen = ctx.Hist.GetValueOrDefault(key) ?? new Dictionary<string, Interaction>();
        string AnchorName(string id) => titles.TryGetValue(id, out var at) ? at.Name : string.Empty;
        // toutes sources : une note TMDb ne veut rien dire avec une poignée de votes (courts métrages, avant-premières,
        // titres pas encore sortis notés par quelques fans)
        int minVotesAll = Math.Clamp(cfg.ExtendedMinVotesAll, 0, 1_000_000);
        int minRuntime = Math.Clamp(cfg.ExtendedMinRuntime, 0, 600);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        bool Skip(SeerrResult r, string k)
            => r.Adult || r.MediaStatus is 2 or 3 or 4 or 5 or 6 || lib.ByTmdb.ContainsKey(k) || seerr.Requested.Contains(k) || Known(seen, "tmdb:" + k, cfg.ProposeMarked)
               || (r.VoteCount > 0 && r.VoteCount < minVotesAll);   // peu de votes connus dès la liste : pas la peine d'en demander la fiche

        // pistes par source : titre → (score dans la source, raison) ; on garde la meilleure piste d'un titre dans une source
        var bySource = new Dictionary<string, Dictionary<string, (double Score, string Reason)>>(StringComparer.Ordinal);
        void Add(string source, SeerrResult r, double score, string reason)
        {
            var k = r.MediaType + ":" + r.TmdbId;
            if (Skip(r, k))
            {
                return;
            }

            if (!bySource.TryGetValue(source, out var m))
            {
                bySource[source] = m = new Dictionary<string, (double, string)>(StringComparer.Ordinal);
            }

            if (!m.TryGetValue(k, out var cur) || score > cur.Score)
            {
                m[k] = (score, reason);
            }
        }

        // 1) recommandations et similaires des titres aimés (en parallèle ; Seerr est limité à 4 appels simultanés)
        int pages = Math.Clamp(cfg.ExtendedPages, 1, 10);
        var related = await Task.WhenAll(anchors.Select(a =>
        {
            var p = a.Tmdb!.Split(':');
            return p.Length == 2 && int.TryParse(p[1], out var tid)
                ? _seerr.RelatedAsync(cl, p[0], tid, pages, ct)
                : Task.FromResult(new List<SeerrResult>());
        })).ConfigureAwait(false);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var because = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int ai = 0; ai < anchors.Count; ai++)
        {
            var a = anchors[ai];
            foreach (var r in related[ai].DistinctBy(x => x.MediaType + ":" + x.TmdbId))
            {
                var k = r.MediaType + ":" + r.TmdbId;
                if (Skip(r, k))
                {
                    continue;
                }

                count[k] = count.GetValueOrDefault(k) + 1;
                because.TryAdd("tmdb:" + k, AnchorName(a.Id));
                // proposé par les titres les plus aimés, par plusieurs titres aimés, puis le mieux noté (départage)
                var cur = bySource.TryGetValue("related", out var rm) && rm.TryGetValue(k, out var c) ? c.Score : 0;
                Add("related", r, cur + a.Weight + (r.Vote * 1e-4), string.Empty);
            }
        }

        // 2) fiches des titres aimés : leurs genres, thèmes, auteurs, studios, langue et saga orientent l'exploration
        bool needLiked = cfg.ExtendedByGenre || cfg.ExtendedByKeyword || cfg.ExtendedCrossings || cfg.ExtendedGems || cfg.ExtendedSagas || cfg.ExtendedByPeople || cfg.ExtendedByStudio || cfg.ExtendedByLanguage;
        var likedDetails = needLiked
            ? await Task.WhenAll(anchors.Select(a =>
            {
                var p = a.Tmdb!.Split(':');
                return p.Length == 2 && int.TryParse(p[1], out var tid) ? _seerr.DetailsAsync(cl, p[0], tid, ct) : Task.FromResult<SeerrDetails?>(null);
            })).ConfigureAwait(false)
            : Array.Empty<SeerrDetails?>();
        var liked = anchors.Zip(likedDetails).Where(x => x.Second is not null).Select(x => (A: x.First, D: x.Second!)).ToList();

        var rated = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sortBy"] = "vote_average.desc",
            ["voteAverageGte"] = Math.Clamp(cfg.ExtendedMinRating, 0, 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
            ["voteCountGte"] = Math.Clamp(cfg.ExtendedMinVotes, 0, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = "all",                                  // langue d'ORIGINE : toutes (sauf la source « langues »)
        };
        int dPages = Math.Clamp(cfg.ExtendedDiscoverPages, 1, 10), depth = Math.Clamp(cfg.ExtendedDiscoverDepth, 1, 50);
        var discoverJobs = new List<(string Source, string Type, Dictionary<string, string> Query, int Pages, double Weight, string Reason)>();
        void Discover(string source, string type, Dictionary<string, string> extra, double weight, string reason, int? pagesOverride = null)
        {
            var q = new Dictionary<string, string>(rated, StringComparer.Ordinal);
            foreach (var (k, v) in extra)
            {
                q[k] = v;
            }

            discoverJobs.Add((source, type, q, pagesOverride ?? dPages, weight, reason));
        }

        string[] types = { "movie", "tv" };
        if (cfg.ExtendedTopRated)
        {
            var votes = Math.Clamp(cfg.ExtendedTopRatedMinVotes, 0, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var t in types)
            {
                Discover("top", t, new Dictionary<string, string> { ["voteCountGte"] = votes }, 1, Texts.Pack("Parmi les mieux notés"), Math.Clamp(cfg.ExtendedTopRatedPages, 1, 10));
            }
        }

        // pistes les plus fortes des titres aimés : genres, thèmes, langues d'origine (partagées par plusieurs sources)
        var genreNames = liked.SelectMany(x => x.D.GenreRefs).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name.ToLowerInvariant());
        var keywordNames = liked.SelectMany(x => x.D.KeywordRefs).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
        int genreCount = Math.Max(cfg.ExtendedByGenre ? Math.Clamp(cfg.ExtendedGenres, 0, 20) : 0, Math.Max(cfg.ExtendedGems ? Math.Clamp(cfg.ExtendedGemGenres, 0, 20) : 0, cfg.ExtendedCrossings ? 5 : 0));
        var topGenres = Exploration.Top(liked.Select(x => (x.D.GenreRefs.Select(g => g.Id), x.A.Weight)), 1, genreCount);
        var topKeywords = Exploration.Top(liked.Select(x => (x.D.KeywordRefs.Select(g => g.Id), x.A.Weight)), 2, Math.Max(cfg.ExtendedByKeyword ? Math.Clamp(cfg.ExtendedKeywords, 0, 50) : 0, cfg.ExtendedCrossings ? 5 : 0));
        double likedTotal = Math.Max(1e-9, liked.Sum(x => x.A.Weight));
        var topLanguages = liked.Where(x => !string.IsNullOrEmpty(x.D.OriginalLanguage)).GroupBy(x => x.D.OriginalLanguage!.ToLowerInvariant())
            .Select(g => (Item: g.Key, Weight: g.Sum(x => x.A.Weight) / likedTotal))
            .Where(g => g.Weight * 100 >= Math.Clamp(cfg.ExtendedLanguageMinShare, 1, 100))
            .OrderByDescending(g => g.Weight).ThenBy(g => g.Item, StringComparer.Ordinal).ToList();
        string LanguageLabel(string code) => Exploration.LanguageNames.TryGetValue(code, out var ln) ? Texts.Translatable(ln) : code;   // nom traduit au rendu
        string Inv(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (cfg.ExtendedByGenre)
        {
            foreach (var (gid, w) in topGenres.Take(Math.Clamp(cfg.ExtendedGenres, 0, 20)))
            {
                foreach (var t in types)
                {
                    var genreIds = Exploration.GenreFor(gid, t).ToList();
                    if (genreIds.Count > 0)
                    {
                        Discover("genre", t, new Dictionary<string, string> { ["genre"] = string.Join("|", genreIds) }, w, Texts.Pack("Bien noté en {0}", genreNames[gid]));
                    }
                }
            }
        }

        if (cfg.ExtendedByKeyword)
        {
            foreach (var (kid, w) in topKeywords.Take(Math.Clamp(cfg.ExtendedKeywords, 0, 50)))
            {
                foreach (var t in types)
                {
                    Discover("keyword", t, new Dictionary<string, string> { ["keywords"] = Inv(kid) }, w, Texts.Pack("Sur le thème « {0} »", keywordNames[kid]));
                }
            }
        }

        // croisements : genre × thème et genre × langue d'origine (hors anglais, déjà majoritaire partout), listes plus
        // étroites donc moins de votes exigés
        if (cfg.ExtendedCrossings && topGenres.Count > 0)
        {
            static List<(T Item, double Weight)> Norm<T>(IEnumerable<(T Item, double Weight)> l)
            {
                var x = l.ToList();
                double m = x.Count == 0 ? 1 : Math.Max(1e-9, x.Max(e => e.Weight));
                return x.Select(e => (e.Item, e.Weight / m)).ToList();
            }

            int maxCross = Math.Clamp(cfg.ExtendedCrossingsMax, 0, 30);
            var g5 = Norm(topGenres.Take(5));
            var byKeyword = Exploration.Pairs(g5, Norm(topKeywords.Take(5)), maxCross)
                .Select(p => (Genre: p.A, Extra: "keywords", Value: Inv(p.B), p.Weight, Reason: Texts.Pack("Bien noté en {0}, thème « {1} »", genreNames[p.A], keywordNames[p.B])));
            var byLanguage = Exploration.Pairs(g5, Norm(topLanguages.Where(l => l.Item != "en").Take(3)), maxCross)
                .Select(p => (Genre: p.A, Extra: "language", Value: p.B, p.Weight, Reason: Texts.Pack("Bien noté en {0}, en {1} (version originale)", genreNames[p.A], LanguageLabel(p.B))));
            var votes = Inv(Math.Clamp(cfg.ExtendedMinVotes, 0, 1_000_000) / 2);
            foreach (var c in byKeyword.Concat(byLanguage).OrderByDescending(c => c.Weight).Take(maxCross))
            {
                foreach (var t in types)
                {
                    var genreIds = Exploration.GenreFor(c.Genre, t).ToList();
                    if (genreIds.Count > 0)
                    {
                        Discover("cross", t, new Dictionary<string, string> { ["genre"] = string.Join("|", genreIds), [c.Extra] = c.Value, ["voteCountGte"] = votes }, c.Weight, c.Reason);
                    }
                }
            }
        }

        // pépites : très bien notées mais peu connues (peu de votes), tous genres et dans les genres préférés
        if (cfg.ExtendedGems)
        {
            int lo = Math.Clamp(cfg.ExtendedGemMinVotes, 0, 1_000_000), hi = Math.Max(lo, Math.Clamp(cfg.ExtendedGemMaxVotes, 0, 1_000_000));
            var gem = new Dictionary<string, string>
            {
                ["voteAverageGte"] = Math.Clamp(cfg.ExtendedGemMinRating, 0, 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                ["voteCountGte"] = Inv(lo),
                ["voteCountLte"] = Inv(hi),
            };
            foreach (var t in types)
            {
                Discover("gems", t, gem, 1, Texts.Pack("Pépite peu connue"));
                foreach (var (gid, w) in topGenres.Take(Math.Clamp(cfg.ExtendedGemGenres, 0, 20)))
                {
                    var genreIds = Exploration.GenreFor(gid, t).ToList();
                    if (genreIds.Count > 0)
                    {
                        Discover("gems", t, new Dictionary<string, string>(gem) { ["genre"] = string.Join("|", genreIds) }, 1 + w, Texts.Pack("Pépite peu connue en {0}", genreNames[gid]));
                    }
                }
            }
        }

        if (cfg.ExtendedByStudio && liked.Count > 0)
        {
            int min = Math.Clamp(cfg.ExtendedStudioMinTitles, 1, 50), max = Math.Clamp(cfg.ExtendedStudios, 0, 30);
            foreach (var t in types)
            {
                var mine = liked.Where(x => x.D.MediaType == t).ToList();
                var names = mine.SelectMany(x => x.D.StudioRefs).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
                foreach (var (sid, w) in Exploration.Top(mine.Select(x => (x.D.StudioRefs.Select(g => g.Id), x.A.Weight)), min, max))
                {
                    var id = sid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Discover("studio", t, new Dictionary<string, string> { [t == "tv" ? "network" : "studio"] = id }, w, Texts.Pack(t == "tv" ? "De la chaîne {0}" : "Du studio {0}", names[sid]));
                }
            }
        }

        if (cfg.ExtendedByLanguage)
        {
            foreach (var (code, share) in topLanguages)
            {
                foreach (var t in types)
                {
                    Discover("language", t, new Dictionary<string, string> { ["language"] = code }, share, Texts.Pack("Bien noté en {0} (version originale)", LanguageLabel(code)));
                }
            }
        }

        // listes de découverte : la page 1 et des pages tirées au hasard du jour (propres au compte), en parallèle
        var jobPages = discoverJobs.Select((j, i) => (Job: j, Pages: Exploration.DailyPages($"{key}|{day}|{j.Source}|{j.Type}|{i}", j.Pages, depth))).ToList();
        var lists = await Task.WhenAll(jobPages.Select(jp => Task.WhenAll(jp.Pages.Select(pg => _seerr.DiscoverAsync(cl, jp.Job.Type, jp.Job.Query, pg, ct))))).ConfigureAwait(false);
        for (int ji = 0; ji < jobPages.Count; ji++)
        {
            var job = jobPages[ji].Job;
            int rank = 0;
            foreach (var r in lists[ji].SelectMany(l => l))
            {
                Add(job.Source, r, job.Weight / (1.0 + (0.05 * rank++)), job.Reason);
            }
        }

        // sagas : volets manquants (ordre de sortie), avant les autres d'une même saga
        if (cfg.ExtendedSagas)
        {
            var sagas = liked.Where(x => x.D.CollectionId is not null).GroupBy(x => x.D.CollectionId!.Value)
                .Select(g => (Id: g.Key, Weight: g.Sum(x => x.A.Weight), Anchor: AnchorName(g.OrderByDescending(x => x.A.Weight).First().A.Id))).ToList();
            var parts = await Task.WhenAll(sagas.Select(sg => _seerr.CollectionAsync(cl, sg.Id, ct))).ConfigureAwait(false);
            for (int si = 0; si < sagas.Count; si++)
            {
                int rank = 0;
                foreach (var r in parts[si])
                {
                    Add("saga", r, sagas[si].Weight / (1.0 + (0.1 * rank++)), Texts.Pack("Dans la saga de {0}", sagas[si].Anchor));
                }
            }
        }

        // auteurs : réalisateurs, créateurs, scénaristes récurrents → le reste de leur filmographie
        if (cfg.ExtendedByPeople && liked.Count > 0)
        {
            var names = liked.SelectMany(x => x.D.PeopleRefs).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
            var people = Exploration.Top(liked.Select(x => (x.D.PeopleRefs.Select(g => g.Id), x.A.Weight)), Math.Clamp(cfg.ExtendedPeopleMinTitles, 1, 50), Math.Clamp(cfg.ExtendedPeople, 0, 50));
            var works = await Task.WhenAll(people.Select(pp => _seerr.PersonWorksAsync(cl, pp.Item, ct))).ConfigureAwait(false);
            for (int pi = 0; pi < people.Count; pi++)
            {
                foreach (var r in works[pi])
                {
                    Add("people", r, people[pi].Weight * (0.5 + (r.Vote / 20.0)), Texts.Pack("De {0}", names[people[pi].Item]));
                }
            }
        }

        // fusion à parts équilibrées (aucune source ne prend tout), puis fiches détaillées du budget seulement
        static IReadOnlyList<string> Ordered(Dictionary<string, (double Score, string Reason)>? m)
            => m is null ? Array.Empty<string>() : m.OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
        var sources = new List<Exploration.Source>
        {
            new("related", Ordered(bySource.GetValueOrDefault("related")), 3),
            new("saga", Ordered(bySource.GetValueOrDefault("saga")), 2),
            new("people", Ordered(bySource.GetValueOrDefault("people")), 2),
            new("cross", Ordered(bySource.GetValueOrDefault("cross")), 1),
            new("keyword", Ordered(bySource.GetValueOrDefault("keyword")), 1),
            new("gems", Ordered(bySource.GetValueOrDefault("gems")), 1),
            new("genre", Ordered(bySource.GetValueOrDefault("genre")), 1),
            new("studio", Ordered(bySource.GetValueOrDefault("studio")), 1),
            new("language", Ordered(bySource.GetValueOrDefault("language")), 1),
            new("top", Ordered(bySource.GetValueOrDefault("top")), 1),
        };
        var merged = Exploration.Merge(sources, Math.Clamp(cfg.ExtendedDetails, 10, 2000));
        var details = await Task.WhenAll(merged.Select(m =>
        {
            var p = m.Key.Split(':');
            return _seerr.DetailsAsync(cl, p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), ct);
        })).ConfigureAwait(false);

        var policy = PolicyOf(u);
        var country = cfg.ClassificationCountry();
        var ids = new List<string>();
        var agree = new Dictionary<string, double>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var kept = new Dictionary<string, SeerrDetails>(StringComparer.Ordinal);
        for (int i = 0; i < merged.Count; i++)
        {
            var (k, source) = merged[i];
            var d = details[i];
            if (d is null || d.MediaStatus is 2 or 3 or 4 or 5 or 6 || !ParentalAllowed(policy, d, country)
                || (d.VoteCount ?? 0) < minVotesAll
                || (minRuntime > 0 && d.Runtime is int rt && rt < minRuntime)   // court métrage (durée inconnue : gardé)
                || d.ReleaseDate is null || string.CompareOrdinal(d.ReleaseDate, today) > 0)   // pas encore sorti
            {
                continue;
            }

            var id = "tmdb:" + k;
            if (!titles.ContainsKey(id))
            {
                titles[id] = ExternalTitle(d, id, colls);
            }

            ids.Add(id);
            kept[id] = d;
            sourceOf[id] = source;
            agree[id] = count.GetValueOrDefault(k) / (double)Math.Max(1, anchors.Count);
            reasons[id] = source == "related"
                ? (because.TryGetValue(id, out var b) && b.Length > 0 ? Texts.Pack("Parce que tu as aimé {0}", b) : string.Empty)
                : bySource[source][k].Reason;
        }

        var found = bySource.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        return new ExtCandidates(ids, agree, because, reasons, sourceOf, found, kept);
    }

    /// <summary>Catégories « Parce que tu as aimé X » : chaque film recommandé va sous le titre aimé dont il est le plus proche.</summary>
    private static List<AnchorCategory> Categories(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user, List<string> ranked, Dictionary<string, Title> titles)
    {
        var sig = ctx.Sig.GetValueOrDefault(user) ?? new Dictionary<string, double>();
        var hist = ctx.Hist.GetValueOrDefault(user) ?? new Dictionary<string, Interaction>();
        var anchors = sig.Where(e => e.Value > 0 && titles.TryGetValue(e.Key, out var t) && !t.External && t.Kind == TitleKind.Movie)
            .OrderByDescending(e => e.Value).Take(10).Select(e => e.Key).ToList();
        var cats = anchors.Select(a => new AnchorCategory
        {
            AnchorId = a,
            AnchorName = titles[a].Name,
            Recent = hist.TryGetValue(a, out var x) && ctx.Now - x.Last < 30 * Core.Day,
        }).ToList();
        if (cats.Count == 0)
        {
            return cats;
        }

        foreach (var id in ranked.Where(id => titles[id].Kind == TitleKind.Movie))
        {
            int best = -1;
            double bs = 0.05;
            for (int i = 0; i < cats.Count; i++)
            {
                double sim = vec[cats[i].AnchorId].Dot(vec[id]);
                if (sim > bs)
                {
                    bs = sim;
                    best = i;
                }
            }

            if (best >= 0 && cats[best].Items.Count < 30)
            {
                cats[best].Items.Add(id);
            }
        }

        return cats.Where(c => c.Items.Count > 0).ToList();
    }
}
