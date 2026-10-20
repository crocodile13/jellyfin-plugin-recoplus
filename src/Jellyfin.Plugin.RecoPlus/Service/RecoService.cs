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

/// <summary>État du moteur, pour la page de configuration.</summary>
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
    private IReadOnlyDictionary<(Guid, Guid), double>? _prCache;
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
                        _log.LogWarning("Reco+ : vecteurs de contenu indisponibles ({Msg})", ex.Message);
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
                        _log.LogInformation("Reco+ : {N} listes rechargées depuis le cache disque", _recs.Count);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+ : cache disque illisible ({Type}), il sera recalculé", ex.GetType().Name);
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
            _log.LogWarning(ex, "Reco+ : écriture du cache impossible");
        }
    }

    /// <summary>Oublie le cache (après un changement de réglage, le prochain calcul repart de zéro).</summary>
    public void Invalidate()
    {
        _recs.Clear();
        Persist();
    }

    private IReadOnlyDictionary<(Guid, Guid), double>? PlaybackReporting(bool force)
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
            Status.PlaybackReporting = "indisponible (" + ex.GetType().Name + ")";
            _prCache = null;
        }

        _prAt = DateTime.UtcNow;
        return _prCache;
    }

    // anciens lancements rattachés au titre actuel (fichiers remplacés par Radarr/Sonarr) : il faut la bibliothèque
    private Dictionary<(Guid, Guid), double> ReadPr(out string status)
    {
        var rows = _pr.FirstStarts(out status);
        var lib = _index.Get();
        var movies = HistoryMath.NameIndex(lib.Titles.Values.Where(t => t.Kind == TitleKind.Movie && !t.External).Select(t => (Guid.ParseExact(t.Id, "N"), t.Name)));
        var series = HistoryMath.NameIndex(lib.Titles.Values.Where(t => t.Kind == TitleKind.Series && !t.External).Select(t => (Guid.ParseExact(t.Id, "N"), t.Name)));
        var map = HistoryMath.MapPlaybackStarts(rows, g => lib.Titles.ContainsKey(g.ToString("N")) || lib.EpisodeSeries.ContainsKey(g), movies, series, out var remapped);
        if (remapped > 0)
        {
            status += $", {remapped} lectures d'anciens fichiers rattachées";
        }

        return map;
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
            }
            else
            {
                Status.LastUserRun = DateTime.UtcNow;
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
            _log.LogError(ex, "Reco+ : échec du calcul (les listes précédentes restent servies)");
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

    private static double Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;

    private async Task ComputeCore(Guid? only, CancellationToken ct, IProgress<double>? progress)
    {
        EnsureLoaded();
        var cfg = Plugin.Instance!.Configuration;
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

        // Seerr : relu à chaque calcul complet, réutilisé (30 min) pour un calcul d'un seul compte
        SeerrSnapshot seerr = _seerr.Last;
        if (_seerr.Configured && (only is null || !seerr.Ok))
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
                h = _history.Build(u.Id, rows.GetValueOrDefault(u.Id), lib, pr);
            }

            hist[key] = h.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);
        }

        // copie de travail : les demandes Seerr ajoutées ci-dessous ne doivent pas polluer le cache
        _lastHist = hist.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal), StringComparer.Ordinal);
        progress?.Report(25);

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
        var cl = seerr.Ok ? _seerr.Client() : null;

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
            if (only is null && seerr.Ok && cl is not null)
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
        if (seerr.Ok && cl is not null)
        {
            // fiches des titres demandés hors bibliothèque : chargées en parallèle d'abord (ensuite lues dans le cache)
            await Task.WhenAll(seerr.RequestsByUser.Where(kv => hist.ContainsKey(kv.Key)).SelectMany(kv => kv.Value)
                .Where(r => r.RequestStatus != 3 && !lib.ByTmdb.ContainsKey(r.MediaType + ":" + r.TmdbId))
                .DistinctBy(r => r.MediaType + ":" + r.TmdbId)
                .Select(r => _seerr.DetailsAsync(cl, r.MediaType, r.TmdbId, ct))).ConfigureAwait(false);

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
                        var d = await _seerr.DetailsAsync(cl, r.MediaType, r.TmdbId, ct).ConfigureAwait(false);
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
            for (int i = 0; i < extTargets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var explored = await ExtendedCandidates(extTargets[i], ctx, lib, tmdbOf, seerr, cl, titles, colls, cfg, ct).ConfigureAwait(false);
                if (_seerr.Down)
                {
                    break; // Seerr a lâché en route : ce compte et les suivants gardent leurs titres hors bibliothèque précédents
                }

                extCands[extTargets[i].Id.ToString("N")] = explored;
                progress?.Report(40 + (20.0 * (i + 1) / extTargets.Count));
            }

            _seerr.SaveCache();
        }

        Status.ExternalTitles = titles.Values.Count(t => t.External);
        ds.LinkCollections();
        var vec = Core.BuildVectors(titles, s);
        progress?.Report(60);

        int done = 0;
        foreach (var u in targets)
        {
            ct.ThrowIfCancellationRequested();
            var key = u.Id.ToString("N");
            var h = hist[key];
            var accessible = _history.Accessible(u);
            var cands = accessible.Where(id => titles.TryGetValue(id, out var ct0) && !Seen(h, id) && ListPolicy.Allows(ct0, lo))
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            var r = new UserRecs { ComputedAt = DateTime.UtcNow, SeerrUserId = seerr.UserMap.GetValueOrDefault(key) };

            // demandés, arrivés, jamais commencés : en tête
            // seulement si le titre est arrivé récemment : c'est une nouvelle pour ce compte, pas une liste permanente
            double pinCut = now - (Math.Clamp(cfg.PinnedMaxDays, 1, 365) * Core.Day);
            var pinned = h.Values.Where(x => x.Requested > 0 && x.Last == 0 && !x.Played && accessible.Contains(x.Title)
                    && lib.Titles.TryGetValue(x.Title, out var lt) && (lt.Added ?? 0) >= pinCut)
                .OrderByDescending(x => x.Requested).Take(Math.Max(0, cfg.PinnedRequests)).Select(x => x.Title).ToList();

            // nouveaux épisodes d'une série rattrapée et aimée (nouvelle saison) : en tête aussi, pendant un temps
            if (cfg.NewEpisodesPinned > 0)
            {
                double newCut = now - (Math.Clamp(cfg.NewEpisodesMaxDays, 1, 365) * Core.Day);
                var sig = ctx.Sig.GetValueOrDefault(key);
                pinned.AddRange(h.Values
                    .Where(x => x.NewEpisodes > 0 && !x.Hidden && accessible.Contains(x.Title) && sig is not null && sig.GetValueOrDefault(x.Title) > 0
                        && Guid.TryParse(x.Title, out var sg) && lib.EpisodeAdded.TryGetValue(sg, out var ad) && ad.Length > 0 && ad[^1] >= newCut)
                    .OrderByDescending(x => lib.EpisodeAdded[Guid.Parse(x.Title)][^1])
                    .Take(cfg.NewEpisodesPinned).Select(x => x.Title).Where(id => !pinned.Contains(id)).ToList());
            }

            r.Pinned = pinned;

            // rangées que HSS montre à ce compte : les autres ne « réservent » aucun titre
            var rowOpts = RowOptionsOf(cfg, HomeScreenSectionsIntegration.EnabledFor(_services, u.Id));

            // montré sans être ouvert (seulement ce que le compte a vraiment vu à l'écran)
            var expo = cfg.TrackImpressions && s.WImpression > 0 ? _impressions.For(key, now) : null;
            if (cands.Count > 0)
            {
                // heure de la journée : une liste par tranche horaire, servie selon l'heure de la demande
                var slots = s.WTimeOfDay > 0 ? new int?[] { 0, 1, 2, 3 } : new int?[] { null };
                foreach (var slot in slots)
                {
                    ctx.Slot = slot;
                    var sc = Recommender.Score(ctx, vec, key, cands, s, exposure: expo);
                    var ranked = Recommender.Rank(ctx, vec, cands, sc, Math.Clamp(cfg.ListSize, 20, 1000), s, key).Where(id => !pinned.Contains(id)).ToList();

                    // présentation : hasard du jour et minimums films/séries (le classement par goût reste intact pour les catégories)
                    var pool = cands.Where(id => !pinned.Contains(id)).ToList();
                    var shaped = ListPolicy.Shape(ranked, pool, id => titles.TryGetValue(id, out var kt) ? (TitleKind?)kt.Kind : null, lo, ListPolicy.Seed(key, DateTime.UtcNow));

                    // rangées d'accueil supplémentaires, sans doublons entre elles ni avec « Pour toi »
                    var shownSet = new HashSet<string>(shaped, StringComparer.Ordinal);
                    var ordered = shaped.Concat(pool.Where(id => !shownSet.Contains(id)).OrderByDescending(id => sc[id]).ThenBy(id => id, StringComparer.Ordinal)).ToList();
                    var home = HomeRows.Allocate(ctx, vec, key, r.Pinned.Concat(shaped).ToList(), ordered, sc, rowOpts);
                    var view = new SlotRecs
                    {
                        Ranked = shaped,
                        Taste = shaped.ToDictionary(id => id, id => Math.Round(sc[id], 5), StringComparer.Ordinal),
                        Rows = home.Rows,
                        RowAnchors = home.Anchors,
                    };
                    if (slot is int sl)
                    {
                        r.Slots[sl] = view;
                    }

                    if (slot is null || slot == ctx.Ds.HourOf(now) / 6)
                    {
                        // vue par défaut (et seule vue sans heure de la journée)
                        (r.Ranked, r.Taste, r.Rows, r.RowAnchors) = (view.Ranked, view.Taste, view.Rows, view.RowAnchors);
                        r.MovieCategories = Categories(ctx, vec, key, ranked, titles);
                    }
                }

                ctx.Slot = null;
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
                var sc = Exploration.Noisy(Recommender.Score(ctx, vec, key, extIds, s, ex.Agree, exposure: expo), noise, noiseSeed);
                var extRanked = Recommender.Rank(ctx, vec, extIds, sc, Math.Clamp(cfg.ExtendedCount, 1, 100), s, key);
                var recs = extRanked.Select(id => (Id: id, Rec: ToExternalRec(id, titles, ex.Because, ex.Reasons))).Where(p => p.Rec is not null).ToList();
                foreach (var p in recs)
                {
                    p.Rec!.Source = ex.SourceOf.GetValueOrDefault(p.Id, string.Empty);
                }

                r.Extended = recs.Select(p => p.Rec!).ToList();
                List<string>? placedFrom = null;

                // mélange libre : bibliothèque et hors bibliothèque notés ensemble sur ce qu'ils ont en commun, chacun à sa place dans la rangée
                if (cfg.ExtendedMixMode == Configuration.MixMode.Score && r.Ranked.Count > 0)
                {
                    var shown = r.Ranked.Take(Math.Max(0, Math.Clamp(cfg.RowSize, 5, 60) - r.Pinned.Count)).ToList();
                    var joint = Recommender.Score(ctx, vec, key, shown.Concat(recs.Select(p => p.Id)).ToList(), Recommender.Shared(s), exposure: expo);
                    var jointNoisy = Exploration.Noisy(joint, noise, noiseSeed);   // même bruit pour la place des titres hors bibliothèque
                    var places = HomeRows.FreePlaces(shown.Select(x => joint[x]).ToList(), recs.Select(p => jointNoisy[p.Id]).ToList(), Math.Clamp(cfg.ExtendedMinFree, 0, 20));

                    // places « découverte » : les mieux notés qui n'ont pas trouvé de place d'eux-mêmes
                    places = HomeRows.ForcePlaces(places, shown.Count,
                        Enumerable.Range(0, recs.Count).OrderByDescending(i => titles[recs[i].Id].Community ?? 0).ThenBy(i => i),
                        Math.Clamp(cfg.ExtendedDiscoverySlots, 0, 10));
                    for (int i = 0; i < recs.Count; i++)
                    {
                        recs[i].Rec!.After = places[i];
                    }

                    placedFrom = recs.Where(p => p.Rec!.After >= 0 && p.Rec!.After < shown.Count).Select(p => p.Rec!.Source).ToList();
                }

                // bilan par source (aperçu) : trouvés, détaillés et autorisés, proposés, placés dans la rangée
                r.Sources = Exploration.Stats(ex.Found, extIds.Select(id => ex.SourceOf.GetValueOrDefault(id, string.Empty)), r.Extended.Select(e => e.Source), placedFrom);
            }

            // pas d'exploration pour ce compte (inactif, ou Seerr injoignable) : ses titres hors bibliothèque précédents restent
            if (cfg.ExtendedEnabled && !extCands.ContainsKey(key) && _recs.TryGetValue(key, out var prevRecs))
            {
                r.Extended = prevRecs.Extended;
                r.Sources = prevRecs.Sources;
            }

            _recs[key] = r;
            progress?.Report(60 + (40.0 * ++done / Math.Max(1, targets.Count)));
        }

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

    /// <summary>Jeu de données pour le calibrage : bibliothèque + historiques complets (sans Seerr).</summary>
    public Dataset BuildCalibrationDataset()
    {
        var lib = _index.Get();
        var pr = PlaybackReporting(true);
        var hist = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal);
        var rows = _userData.ReadAll();
        foreach (var u in _users.GetUsers())
        {
            hist[u.Id.ToString("N")] = _history.Build(u.Id, rows.GetValueOrDefault(u.Id), lib, pr);
        }

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

        var ds = new Dataset
        {
            Titles = titles,
            Users = hist,
            Collections = lib.Collections.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
            Now = Unix(DateTime.UtcNow),
            LocalHour = LocalHourFunc(Plugin.Instance!.Configuration),
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
    public List<string> Preview(Guid user, int n = 20)
    {
        var r = For(user);
        var lib = Library ?? _index.Current;
        if (r is null)
        {
            return new List<string>();
        }

        string Name(string id) => lib is not null && lib.Titles.TryGetValue(id, out var t) ? $"{t.Name}{(t.Kind == TitleKind.Series ? " (série)" : string.Empty)}" : id;
        return r.Pinned.Select(id => "📌 " + Name(id)).Concat(r.Ranked.Select(Name)).Take(n).ToList();
    }

    /// <summary>Aperçu des rangées d'accueil supplémentaires d'un compte : libellé → titres (page de configuration).</summary>
    public Dictionary<string, List<string>> PreviewRows(Guid user)
    {
        var r = For(user);
        var lib = Library ?? _index.Current;
        var cfg = Plugin.Instance?.Configuration;
        var res = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (r is null || cfg is null)
        {
            return res;
        }

        string Name(string id) => lib is not null && lib.Titles.TryGetValue(id, out var t) ? $"{t.Name}{(t.Kind == TitleKind.Series ? " (série)" : string.Empty)}" : id;
        string tpl = string.IsNullOrWhiteSpace(cfg.RowBecauseTitle) ? "Parce que tu as aimé {titre}" : cfg.RowBecauseTitle;
        int size = Math.Clamp(cfg.RowSize, 5, 60);
        foreach (var (row, ids) in r.Rows.Where(kv => kv.Key != HomeRows.PourToi))
        {
            string label = row switch
            {
                HomeRows.Films => cfg.RowMoviesTitle,
                HomeRows.Series => cfg.RowSeriesTitle,
                HomeRows.Nouveautes => cfg.RowNewTitle,
                _ => r.RowAnchors.TryGetValue(row, out var a) ? tpl.Replace("{titre}", a, StringComparison.Ordinal) : row,
            };
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

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "japonais", ["ko"] = "coréen", ["zh"] = "chinois", ["cn"] = "cantonais", ["fr"] = "français", ["en"] = "anglais",
        ["es"] = "espagnol", ["it"] = "italien", ["de"] = "allemand", ["pt"] = "portugais", ["hi"] = "hindi", ["ru"] = "russe",
        ["sv"] = "suédois", ["da"] = "danois", ["no"] = "norvégien", ["pl"] = "polonais", ["tr"] = "turc", ["th"] = "thaï",
    };

    /// <summary>Candidats hors bibliothèque d'un compte : identifiants, accord TMDb, titre aimé d'origine et raison affichée.</summary>
    private sealed record ExtCandidates(
        List<string> Ids, Dictionary<string, double> Agree, Dictionary<string, string> Because, Dictionary<string, string> Reasons,
        Dictionary<string, string> SourceOf, Dictionary<string, int> Found);

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
        bool Skip(SeerrResult r, string k)
            => r.Adult || r.MediaStatus is 2 or 3 or 4 or 5 or 6 || lib.ByTmdb.ContainsKey(k) || seerr.Requested.Contains(k) || Seen(seen, "tmdb:" + k);

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
                Discover("top", t, new Dictionary<string, string> { ["voteCountGte"] = votes }, 1, "Parmi les mieux notés", Math.Clamp(cfg.ExtendedTopRatedPages, 1, 10));
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
        string LanguageLabel(string code) => LanguageNames.TryGetValue(code, out var ln) ? ln : code;
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
                        Discover("genre", t, new Dictionary<string, string> { ["genre"] = string.Join("|", genreIds) }, w, "Bien noté en " + genreNames[gid]);
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
                    Discover("keyword", t, new Dictionary<string, string> { ["keywords"] = Inv(kid) }, w, "Sur le thème « " + keywordNames[kid] + " »");
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
                .Select(p => (Genre: p.A, Extra: "keywords", Value: Inv(p.B), p.Weight, Reason: "Bien noté en " + genreNames[p.A] + ", thème « " + keywordNames[p.B] + " »"));
            var byLanguage = Exploration.Pairs(g5, Norm(topLanguages.Where(l => l.Item != "en").Take(3)), maxCross)
                .Select(p => (Genre: p.A, Extra: "language", Value: p.B, p.Weight, Reason: "Bien noté en " + genreNames[p.A] + ", en " + LanguageLabel(p.B) + " (version originale)"));
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
                Discover("gems", t, gem, 1, "Pépite peu connue");
                foreach (var (gid, w) in topGenres.Take(Math.Clamp(cfg.ExtendedGemGenres, 0, 20)))
                {
                    var genreIds = Exploration.GenreFor(gid, t).ToList();
                    if (genreIds.Count > 0)
                    {
                        Discover("gems", t, new Dictionary<string, string>(gem) { ["genre"] = string.Join("|", genreIds) }, 1 + w, "Pépite peu connue en " + genreNames[gid]);
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
                    Discover("studio", t, new Dictionary<string, string> { [t == "tv" ? "network" : "studio"] = id }, w, (t == "tv" ? "De la chaîne " : "Du studio ") + names[sid]);
                }
            }
        }

        if (cfg.ExtendedByLanguage)
        {
            foreach (var (code, share) in topLanguages)
            {
                foreach (var t in types)
                {
                    Discover("language", t, new Dictionary<string, string> { ["language"] = code }, share, "Bien noté en " + LanguageLabel(code) + " (version originale)");
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
                    Add("saga", r, sagas[si].Weight / (1.0 + (0.1 * rank++)), "Dans la saga de " + sagas[si].Anchor);
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
                    Add("people", r, people[pi].Weight * (0.5 + (r.Vote / 20.0)), "De " + names[people[pi].Item]);
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
        var country = string.IsNullOrWhiteSpace(cfg.RatingCountry) ? "US" : cfg.RatingCountry.Trim().ToUpperInvariant();
        var ids = new List<string>();
        var agree = new Dictionary<string, double>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < merged.Count; i++)
        {
            var (k, source) = merged[i];
            var d = details[i];
            if (d is null || d.MediaStatus is 2 or 3 or 4 or 5 or 6 || !ParentalAllowed(policy, d, country))
            {
                continue;
            }

            var id = "tmdb:" + k;
            if (!titles.ContainsKey(id))
            {
                titles[id] = ExternalTitle(d, id, colls);
            }

            ids.Add(id);
            sourceOf[id] = source;
            agree[id] = count.GetValueOrDefault(k) / (double)Math.Max(1, anchors.Count);
            reasons[id] = source == "related"
                ? (because.TryGetValue(id, out var b) && b.Length > 0 ? "Parce que tu as aimé " + b : string.Empty)
                : bySource[source][k].Reason;
        }

        var found = bySource.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        return new ExtCandidates(ids, agree, because, reasons, sourceOf, found);
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
