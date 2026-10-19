using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.RecoPlus.Data;
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
public sealed class RecoService
{
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
    private readonly ConcurrentDictionary<string, UserRecs> _recs = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, Interaction>> _lastHist = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<(Guid, Guid), double>? _prCache;
    private DateTime _prAt;
    private bool _loaded;

    public RecoService(LibraryIndex index, UserHistory history, UserDataReader userData, PlaybackReportingReader pr, SeerrService seerr,
        JellyfinEnhancedReader je, IUserManager users, ILocalizationManager loc, ILogger<RecoService> log)
    {
        _je = je;
        _index = index;
        _history = history;
        _userData = userData;
        _pr = pr;
        _seerr = seerr;
        _users = users;
        _loc = loc;
        _log = log;
    }

    /// <summary>État courant.</summary>
    public EngineStatus Status { get; } = new();

    /// <summary>Vecteurs de contenu du dernier calcul (pour « Plus comme ceci »).</summary>
    public IReadOnlyDictionary<string, SparseVector>? Vectors { get; private set; }

    /// <summary>Instantané de la bibliothèque du dernier calcul.</summary>
    public LibrarySnapshot? Library { get; private set; }

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

        return _recs.TryGetValue(key, out var r) ? r : null;
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

        _loaded = true;
        try
        {
            if (File.Exists(CachePath))
            {
                var f = JsonSerializer.Deserialize<RecsFile>(File.ReadAllText(CachePath));
                if (f?.Format == 1)
                {
                    foreach (var (u, r) in f.Users)
                    {
                        _recs[u] = r;
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

    private Dictionary<(Guid, Guid), double> ReadPr(out string status) => _pr.FirstStarts(out status);

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
        catch (OperationCanceledException)
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
            int fetched = 0;
            foreach (var (id, t) in titles)
            {
                if (t.External || !tmdbOf.TryGetValue(id, out var tk))
                {
                    continue;
                }

                var parts = tk.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var tid))
                {
                    continue;
                }

                var d = _seerr.CachedDetails(parts[0], tid);
                if (d?.VoteCount is null && only is null && seerr.Ok && cl is not null && fetched < 150)
                {
                    fetched++;
                    d = await _seerr.DetailsAsync(cl, parts[0], tid, ct, requireVotes: true).ConfigureAwait(false);
                }

                if (d?.VoteCount is int v)
                {
                    t.Votes = v;
                }
            }

            if (fetched > 0)
            {
                _seerr.SaveCache();
            }
        }

        // demandes Seerr → signal (titre voulu) ; titres demandés absents → titres externes (profil seulement)
        if (seerr.Ok && cl is not null)
        {
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
        var ds = new Dataset { Titles = titles, Users = hist, Collections = colls, Now = now, Reviews = reviews };
        var ctx = new Context(ds, now, hist, s);
        progress?.Report(40);

        // mode étendu : candidats TMDb des titres aimés de chaque compte ciblé
        var targets = only is null ? users : users.Where(u => u.Id == only).ToList();
        var extCands = new Dictionary<string, (List<string> Ids, Dictionary<string, double> Agree, Dictionary<string, string> Because)>(StringComparer.Ordinal);
        if (cfg.ExtendedEnabled && seerr.Ok && cl is not null)
        {
            foreach (var u in targets)
            {
                ct.ThrowIfCancellationRequested();
                extCands[u.Id.ToString("N")] = await ExtendedCandidates(u, ctx, lib, tmdbOf, seerr, cl, titles, colls, cfg, ct).ConfigureAwait(false);
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

            if (cands.Count > 0)
            {
                var sc = Recommender.Score(ctx, vec, key, cands, s);
                var ranked = Recommender.Rank(ctx, vec, cands, sc, Math.Clamp(cfg.ListSize, 20, 1000), s).Where(id => !pinned.Contains(id)).ToList();

                // présentation : hasard du jour et minimums films/séries (le classement par goût reste intact pour les catégories)
                var pool = cands.Where(id => !pinned.Contains(id)).ToList();
                r.Ranked = ListPolicy.Shape(ranked, pool, id => titles.TryGetValue(id, out var kt) ? (TitleKind?)kt.Kind : null, lo, ListPolicy.Seed(key, DateTime.UtcNow));
                r.Taste = r.Ranked.ToDictionary(id => id, id => Math.Round(sc[id], 5), StringComparer.Ordinal);
                r.MovieCategories = Categories(ctx, vec, key, ranked, titles);
            }

            var hid = hiddenTmdb.GetValueOrDefault(key);
            var extIds = extCands.TryGetValue(key, out var ex)
                ? ex.Ids.Where(id => ListPolicy.Allows(titles[id], lo) && (hid is null || !hid.Contains(id.StartsWith("tmdb:", StringComparison.Ordinal) ? id[5..] : id))).ToList()
                : new List<string>();
            if (extIds.Count > 0)
            {
                var sc = Recommender.Score(ctx, vec, key, extIds, s, ex.Agree);
                var extRanked = Recommender.Rank(ctx, vec, extIds, sc, Math.Clamp(cfg.ExtendedCount, 1, 100), s);
                var recs = extRanked.Select(id => (Id: id, Rec: ToExternalRec(id, titles, ex.Because))).Where(p => p.Rec is not null).ToList();
                r.Extended = recs.Select(p => p.Rec!).ToList();

                // mélange libre : bibliothèque et hors bibliothèque notés ensemble, chacun à sa place dans la rangée
                if (cfg.ExtendedMixMode == Configuration.MixMode.Score && r.Ranked.Count > 0)
                {
                    var shown = r.Ranked.Take(Math.Max(0, 20 - r.Pinned.Count)).ToList();
                    var joint = Recommender.Score(ctx, vec, key, shown.Concat(recs.Select(p => p.Id)).ToList(), s, ex.Agree);
                    foreach (var (id, rec) in recs)
                    {
                        var se = joint[id];
                        rec!.After = shown.Count(x => joint[x] > se);
                    }
                }
            }

            _recs[key] = r;
            progress?.Report(60 + (40.0 * ++done / Math.Max(1, targets.Count)));
        }

        Vectors = vec;
        Library = lib;
        Status.UsersWithRecs = _recs.Count;
        Persist();
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

    private ExternalRec? ToExternalRec(string id, Dictionary<string, Title> titles, Dictionary<string, string> because)
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
            Overview = d?.Overview,
            Vote = t.Community ?? 0,
            Because = because.GetValueOrDefault(id, string.Empty),
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

    private bool ParentalAllowed(User u, SeerrDetails d, string country)
    {
        if (d.Adult)
        {
            return false;
        }

        if (u.MaxParentalRatingScore is not int max)
        {
            return true;
        }

        string? cert = d.Certifications.TryGetValue(country, out var c) ? c : (d.Certifications.TryGetValue("US", out var us) ? us : null);
        var cc = cert is not null && d.Certifications.ContainsKey(country) ? country : "US";
        if (cert is null)
        {
            return false; // plus strict que Jellyfin : classification inconnue + limite fixée → exclu
        }

        var score = _loc.GetRatingScore(cert, cc);
        if (score is null)
        {
            return false;
        }

        if (score.Score != max)
        {
            return score.Score < max;
        }

        return u.MaxParentalRatingSubScore is not int sub || (score.SubScore ?? 0) <= sub;
    }

    private async Task<(List<string> Ids, Dictionary<string, double> Agree, Dictionary<string, string> Because)> ExtendedCandidates(
        User u, Context ctx, LibrarySnapshot lib, Dictionary<string, string> tmdbOf, SeerrSnapshot seerr, SeerrClient cl,
        Dictionary<string, Title> titles, Dictionary<string, List<string>> colls, Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        var key = u.Id.ToString("N");
        var sig = ctx.Sig.GetValueOrDefault(key) ?? new Dictionary<string, double>();
        var anchors = sig.Where(e => e.Value > 0).OrderByDescending(e => e.Value)
            .Select(e => (Id: e.Key, Tmdb: tmdbOf.GetValueOrDefault(e.Key) ?? (e.Key.StartsWith("tmdb:", StringComparison.Ordinal) ? e.Key[5..] : null)))
            .Where(a => a.Tmdb is not null)
            .Take(Math.Clamp(cfg.ExtendedAnchors, 1, 50))
            .ToList();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var meta = new Dictionary<string, SeerrResult>(StringComparer.Ordinal);
        var because = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = ctx.Hist.GetValueOrDefault(key) ?? new Dictionary<string, Interaction>();
        foreach (var a in anchors)
        {
            var p = a.Tmdb!.Split(':');
            if (p.Length != 2 || !int.TryParse(p[1], out var tid))
            {
                continue;
            }

            foreach (var r in await _seerr.RelatedAsync(cl, p[0], tid, ct).ConfigureAwait(false))
            {
                var k = r.MediaType + ":" + r.TmdbId;
                if (r.Adult || r.MediaStatus is 2 or 3 or 4 or 5 or 6 || lib.ByTmdb.ContainsKey(k) || seerr.Requested.Contains(k) || Seen(seen, "tmdb:" + k))
                {
                    continue;
                }

                count[k] = count.GetValueOrDefault(k) + 1;
                meta.TryAdd(k, r);
                because.TryAdd("tmdb:" + k, titles.TryGetValue(a.Id, out var at) ? at.Name : string.Empty);
            }
        }

        var top = count.OrderByDescending(kv => kv.Value).ThenByDescending(kv => meta[kv.Key].Vote).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Clamp(cfg.ExtendedDetails, 10, 300)).ToList();
        var ids = new List<string>();
        var agree = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, n) in top)
        {
            var p = k.Split(':');
            var d = await _seerr.DetailsAsync(cl, p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (d is null || d.MediaStatus is 2 or 3 or 4 or 5 or 6 || !ParentalAllowed(u, d, cfg.RatingCountry))
            {
                continue;
            }

            var id = "tmdb:" + k;
            if (!titles.ContainsKey(id))
            {
                titles[id] = ExternalTitle(d, id, colls);
            }

            ids.Add(id);
            agree[id] = n / (double)Math.Max(1, anchors.Count);
        }

        return (ids, agree, because);
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
