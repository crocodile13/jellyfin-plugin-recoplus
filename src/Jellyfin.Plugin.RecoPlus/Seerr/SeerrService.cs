using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RecoPlus.Data;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Seerr;

/// <summary>Instantané Seerr utile au moteur : correspondance des comptes et demandes.</summary>
public sealed class SeerrSnapshot
{
    /// <summary>Compte Jellyfin (« N ») → compte Seerr.</summary>
    public Dictionary<string, int> UserMap { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Compte Jellyfin (« N ») → demandes de ce compte.</summary>
    public Dictionary<string, List<SeerrRequest>> RequestsByUser { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Compte Jellyfin (« N ») → liste de suivi Seerr de ce compte.</summary>
    public Dictionary<string, List<SeerrWatch>> WatchlistByUser { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Clés « movie:123 » demandées (tous comptes), pour exclure du mode étendu ce qui est déjà en cours.</summary>
    public HashSet<string> Requested { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Version de Seerr.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Lecture réussie.</summary>
    public bool Ok { get; init; }

    /// <summary>État lisible (emballé : <see cref="Texts.Pack"/>).</summary>
    public string Status { get; init; } = "désactivé";

    /// <summary>Moment de la lecture.</summary>
    public DateTime At { get; init; } = DateTime.UtcNow;
}

/// <summary>Liste TMDb en cache (sur disque entre deux démarrages).</summary>
public sealed class CachedList
{
    public DateTime At { get; set; }

    public List<SeerrResult> Items { get; set; } = new();
}

/// <summary>Fichier des listes en cache.</summary>
public sealed class ListCacheFile
{
    public Dictionary<string, CachedList> Related { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, CachedList> Lists { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Accès à Seerr : configuration, cache des fiches (disque, 14 jours), recommandations TMDb (3 jours).</summary>
public sealed class SeerrService
{
    private static readonly TimeSpan DetailsTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan RelatedTtl = TimeSpan.FromDays(3);

    // fiche introuvable (404) : pas redemandée avant 7 jours ; autre échec (délai, erreur de Seerr) : avant 1 heure
    private static readonly TimeSpan MissingTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailedTtl = TimeSpan.FromHours(1);

    // listes d'exploration : découverte 3 jours, sagas et filmographies 7 jours (elles bougent peu)
    private static readonly TimeSpan DiscoverTtl = TimeSpan.FromDays(3);
    private static readonly TimeSpan StableListTtl = TimeSpan.FromDays(7);
    private readonly IHttpClientFactory _http;
    private readonly SecretStore _secrets;
    private readonly ILogger<SeerrService> _log;
    private readonly ConcurrentDictionary<string, SeerrDetails> _details = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, List<SeerrResult> Items)> _related = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);   // fiche ou liste → réessai possible après
    private readonly ConcurrentDictionary<string, (DateTime At, List<SeerrResult> Items)> _lists = new(StringComparer.Ordinal);
    // Seerr sert aussi les utilisateurs (Jellyfin Enhanced) : Reco+ n'a jamais plus de 2 appels en cours, et pas plus
    // de N par seconde (réglable), pour ne pas engorger sa file vers TMDb
    private readonly SemaphoreSlim _throttle = new(2);
    private long _nextCall;
    private readonly object _loadLock = new();
    private volatile bool _loaded;
    private int _dirty;
    private int _listsDirty;

    // coupe-circuit : après quelques échecs d'affilée « Seerr ne répond pas » (délai dépassé, connexion refusée, erreur 5xx),
    // plus aucun appel pendant quelques minutes, au lieu d'attendre 20 s par fiche ou liste (des centaines par calcul)
    private const int MaxStrikes = 3;
    private static readonly TimeSpan DownFor = TimeSpan.FromMinutes(5);
    private int _strikes;
    private long _downUntil;

    public SeerrService(IHttpClientFactory http, SecretStore secrets, ILogger<SeerrService> log)
    {
        _http = http;
        _secrets = secrets;
        _log = log;
    }

    /// <summary>Dernier instantané.</summary>
    public SeerrSnapshot Last { get; private set; } = new();

    /// <summary>Vrai si Seerr est activé et configuré.</summary>
    public bool Configured
    {
        get
        {
            var c = Plugin.Instance?.Configuration;
            return c is not null && c.SeerrEnabled && !string.IsNullOrWhiteSpace(c.SeerrUrl) && _secrets.Get(SecretStore.SeerrApiKey) is not null;
        }
    }

    private static string CacheDir => Path.Combine(Plugin.Instance!.DataFolderPath, "jellyseerr");

    /// <summary>Client prêt à l'emploi (exception claire si mal configuré).</summary>
    public SeerrClient Client()
    {
        var c = Plugin.Instance!.Configuration;
        var http = _http.CreateClient("RecoPlusSeerr");
        http.Timeout = TimeSpan.FromSeconds(20);
        return new SeerrClient(http, c.SeerrUrl, _secrets.Get(SecretStore.SeerrApiKey) ?? string.Empty);
    }

    /// <summary>Lit comptes et demandes. N'échoue jamais : renvoie un instantané « non disponible » en cas de problème.</summary>
    public async Task<SeerrSnapshot> RefreshAsync(CancellationToken ct)
    {
        if (!Configured)
        {
            return Last = new SeerrSnapshot { Status = "désactivé ou non configuré" };
        }

        // coupe-circuit ouvert : pas d'attente, la dernière lecture réussie reste servie
        if (Down)
        {
            return Last.Ok ? Last : Last = new SeerrSnapshot { Status = "ne répond plus (nouvel essai dans quelques minutes)" };
        }

        try
        {
            var cl = Client();
            var version = await cl.CheckAsync(ct).ConfigureAwait(false);
            var users = await cl.UsersAsync(ct).ConfigureAwait(false);
            var reqs = await cl.RequestsAsync(ct).ConfigureAwait(false);
            var map = users.GroupBy(u => u.JellyfinUserId).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
            var rev = map.ToDictionary(kv => kv.Value, kv => kv.Key);
            var byUser = new Dictionary<string, List<SeerrRequest>>(StringComparer.Ordinal);
            foreach (var r in reqs)
            {
                if (rev.TryGetValue(r.RequestedBy, out var jf))
                {
                    if (!byUser.TryGetValue(jf, out var l))
                    {
                        byUser[jf] = l = new();
                    }

                    l.Add(r);
                }
            }

            // listes de suivi : facultatives (version de Seerr sans cette route, droits) — un échec ne coupe rien d'autre
            var watch = new Dictionary<string, List<SeerrWatch>>(StringComparer.Ordinal);
            int watchErrors = 0;
            foreach (var (jf, sid) in map)
            {
                try
                {
                    var w = await cl.WatchlistAsync(sid, ct).ConfigureAwait(false);
                    if (w.Count > 0)
                    {
                        watch[jf] = w;
                    }
                }
                catch (Exception ex) when (ex is SeerrException or HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    watchErrors++;
                    if (Unreachable(ex, ct))
                    {
                        break; // Seerr ne répond plus : inutile d'attendre 20 s par compte
                    }
                }
            }

            return Last = new SeerrSnapshot
            {
                UserMap = map,
                RequestsByUser = byUser,
                WatchlistByUser = watch,
                Requested = reqs.Where(r => r.RequestStatus is 1 or 2).Select(r => r.MediaType + ":" + r.TmdbId).ToHashSet(StringComparer.Ordinal),
                Version = version,
                Ok = true,
                Status = watchErrors > 0
                    ? Texts.Pack("connecté (v{0}) — {1} comptes liés, {2} demandes, {3} titres en liste de suivi ({4} listes illisibles)", version, map.Count, reqs.Count, watch.Values.Sum(l => l.Count), watchErrors)
                    : Texts.Pack("connecté (v{0}) — {1} comptes liés, {2} demandes, {3} titres en liste de suivi", version, map.Count, reqs.Count, watch.Values.Sum(l => l.Count)),
            };
        }
        catch (Exception ex) when (ex is SeerrException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            Failed(ex, ct);
            _log.LogWarning("Reco+: Seerr unavailable ({Msg}) — recommendations computed without it", ex.Message);
            return Last = new SeerrSnapshot { Status = Texts.Pack("indisponible : {0}", SeerrException.TextOf(ex)) };
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
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
                var f = Path.Combine(CacheDir, "fiches.json");
                if (File.Exists(f))
                {
                    foreach (var d in JsonSerializer.Deserialize<List<SeerrDetails>>(File.ReadAllText(f)) ?? new())
                    {
                        if (DateTime.UtcNow - d.FetchedAt < DetailsTtl)
                        {
                            _details[d.MediaType + ":" + d.TmdbId] = d;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+: unreadable Seerr details cache, starting from scratch");
            }

            // listes TMDb : gardées entre deux démarrages (sinon toute l'exploration est relue après un redémarrage)
            try
            {
                var f = new FileInfo(Path.Combine(CacheDir, "listes.json"));
                if (f.Exists && f.Length < 256L * 1024 * 1024
                    && JsonSerializer.Deserialize<ListCacheFile>(File.ReadAllText(f.FullName)) is { } lc)
                {
                    var now = DateTime.UtcNow;
                    foreach (var (k, l) in lc.Related)
                    {
                        if (l?.Items is not null && now - l.At < RelatedTtl)
                        {
                            _related[k] = (l.At, l.Items);
                        }
                    }

                    foreach (var (k, l) in lc.Lists)
                    {
                        if (l?.Items is not null && now - l.At < StableListTtl)
                        {
                            _lists[k] = (l.At, l.Items);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.LogWarning("Reco+: unreadable Seerr lists cache ({Type}), starting from scratch", ex.GetType().Name);
            }

            _loaded = true;
        }
    }

    /// <summary>Sauvegarde le cache des fiches, seulement s'il a changé (fiches expirées retirées au passage).</summary>
    public void SaveCache()
    {
        EnsureLoaded(); // jamais écraser sur disque des caches pas encore relus
        var now = DateTime.UtcNow;
        foreach (var (k, d) in _details)
        {
            if (now - d.FetchedAt >= DetailsTtl && _details.TryRemove(k, out _))
            {
                Interlocked.Exchange(ref _dirty, 1);
            }
        }

        foreach (var (k, until) in _failed)
        {
            if (until <= now)
            {
                _failed.TryRemove(k, out _);
            }
        }

        foreach (var (k, l) in _lists)
        {
            if (now - l.At > StableListTtl && _lists.TryRemove(k, out _))
            {
                Interlocked.Exchange(ref _listsDirty, 1);
            }
        }

        foreach (var (k, l) in _related)
        {
            if (now - l.At > RelatedTtl && _related.TryRemove(k, out _))
            {
                Interlocked.Exchange(ref _listsDirty, 1);
            }
        }

        if (Interlocked.Exchange(ref _listsDirty, 0) == 1)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var lf = Path.Combine(CacheDir, "listes.json");
                var file = new ListCacheFile
                {
                    Related = _related.ToDictionary(kv => kv.Key, kv => new CachedList { At = kv.Value.At, Items = kv.Value.Items }, StringComparer.Ordinal),
                    Lists = _lists.ToDictionary(kv => kv.Key, kv => new CachedList { At = kv.Value.At, Items = kv.Value.Items }, StringComparer.Ordinal),
                };
                File.WriteAllText(lf + ".tmp", JsonSerializer.Serialize(file));
                File.Move(lf + ".tmp", lf, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref _listsDirty, 1);
                _log.LogWarning(ex, "Reco+: could not write the Seerr lists cache");
            }
        }

        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            var f = Path.Combine(CacheDir, "fiches.json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(_details.Values.ToList()));
            File.Move(f + ".tmp", f, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _log.LogWarning(ex, "Reco+: could not write the Seerr cache");
        }
    }

    // une place (2 appels au plus en cours) puis le rythme (appels espacés de 1/N s) ; la place est rendue par l'appelant
    private async Task SlotAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int rate = Math.Clamp(Plugin.Instance?.Configuration.SeerrRate ?? 8, 1, 50);
            long gap = TimeSpan.TicksPerSecond / rate, now = DateTime.UtcNow.Ticks, slot;
            while (true)
            {
                long next = Interlocked.Read(ref _nextCall);
                slot = Math.Max(now, next);
                if (Interlocked.CompareExchange(ref _nextCall, slot + gap, next) == next)
                {
                    break;
                }
            }

            if (slot > now)
            {
                await Task.Delay(TimeSpan.FromTicks(slot - now), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            _throttle.Release(); // annulé en attendant son tour : la place est rendue ici
            throw;
        }
    }

    /// <summary>Seerr ne répond plus (coupe-circuit ouvert) : les appels sont sautés jusqu'à la fin du délai.</summary>
    public bool Down => DateTime.UtcNow.Ticks < Interlocked.Read(ref _downUntil);

    // Seerr injoignable ou en panne (pas une fiche introuvable ni une réponse illisible)
    private static bool Unreachable(Exception ex, CancellationToken ct)
        => ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested)
            || ex is SeerrException { Status: >= System.Net.HttpStatusCode.InternalServerError };

    private void Answered() => Interlocked.Exchange(ref _strikes, 0);

    private void Failed(Exception ex, CancellationToken ct)
    {
        if (!Unreachable(ex, ct))
        {
            Answered(); // Seerr a répondu (404, JSON inattendu…) : il est joignable
            return;
        }

        if (Interlocked.Increment(ref _strikes) >= MaxStrikes && !Down)
        {
            Interlocked.Exchange(ref _downUntil, (DateTime.UtcNow + DownFor).Ticks);
            Interlocked.Exchange(ref _strikes, 0);
            _log.LogWarning("Reco+: Seerr is not responding ({Msg}); calls suspended for {Min} min, computation continues without it", ex.Message, DownFor.TotalMinutes);
        }
    }

    // délai dépassé chez Seerr (HttpClient) : un échec comme un autre, sauf si c'est le calcul qui est annulé
    private static bool Transient(Exception ex, CancellationToken ct)
        => ex is SeerrException or HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>Fiche déjà en cache (sans appel réseau), ou null.</summary>
    public SeerrDetails? CachedDetails(string mediaType, int tmdbId)
    {
        EnsureLoaded();
        return _details.TryGetValue(mediaType + ":" + tmdbId, out var d) ? d : null;
    }

    /// <summary>Fiche (cache 14 jours). Null si indisponible.</summary>
    /// <param name="requireVotes">Relire une fiche en cache qui n'a pas encore le nombre de votes (ancienne version).</param>
    public async Task<SeerrDetails?> DetailsAsync(SeerrClient cl, string mediaType, int tmdbId, CancellationToken ct, bool requireVotes = false)
    {
        EnsureLoaded();
        var key = mediaType + ":" + tmdbId;
        if (_details.TryGetValue(key, out var d) && DateTime.UtcNow - d.FetchedAt < DetailsTtl && d.Format >= SeerrDetails.CurrentFormat
            && (!requireVotes || d.VoteCount is not null))
        {
            return d;
        }

        if (_failed.TryGetValue(key, out var until) && until > DateTime.UtcNow)
        {
            return d; // échec récent : pas de nouvel appel (fiche en cache sans votes, ou rien)
        }

        if (Down)
        {
            return d;
        }

        await SlotAsync(ct).ConfigureAwait(false);
        try
        {
            if (Down)
            {
                return d;
            }

            d = await cl.DetailsAsync(mediaType, tmdbId, Plugin.Instance!.Configuration.TmdbLanguage(), ct).ConfigureAwait(false);
            Answered();
            _details[key] = d;
            _failed.TryRemove(key, out _);
            Interlocked.Exchange(ref _dirty, 1);
            return d;
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            Failed(ex, ct);
            _failed[key] = DateTime.UtcNow + (ex is SeerrException { Status: System.Net.HttpStatusCode.NotFound } ? MissingTtl : FailedTtl);
            _log.LogDebug("Reco+: details {Key} unavailable ({Msg})", key, ex.Message);
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Recommandations + similaires TMDb d'un titre, sur <paramref name="pages"/> pages chacune (cache 3 jours).</summary>
    public async Task<List<SeerrResult>> RelatedAsync(SeerrClient cl, string mediaType, int tmdbId, int pages, CancellationToken ct)
    {
        EnsureLoaded();
        pages = Math.Clamp(pages, 1, 10);
        var key = mediaType + ":" + tmdbId + ":" + pages;
        if (_related.TryGetValue(key, out var r) && DateTime.UtcNow - r.At < RelatedTtl)
        {
            return r.Items;
        }

        var all = new List<SeerrResult>();
        bool anyOk = false;
        foreach (var rel in new[] { "recommendations", "similar" })
        {
            for (int page = 1; page <= pages; page++)
            {
                int got = 0;
                if (Down)
                {
                    break;
                }

                await SlotAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Down)
                    {
                        break;
                    }

                    var items = await cl.RelatedAsync(mediaType, tmdbId, rel, Plugin.Instance!.Configuration.TmdbLanguage(), page, ct).ConfigureAwait(false);
                    Answered();
                    got = items.Count;
                    anyOk = true;
                    all.AddRange(items);
                }
                catch (Exception ex) when (Transient(ex, ct))
                {
                    Failed(ex, ct);
                    _log.LogDebug("Reco+: {Rel} of {Key} (page {Page}) unavailable ({Msg})", rel, key, page, ex.Message);
                }
                finally
                {
                    _throttle.Release();
                }

                if (got == 0)
                {
                    break; // page vide ou en échec : pas de page suivante
                }
            }
        }

        // tout a échoué (Seerr injoignable) : rien en cache, on réessaiera au prochain calcul
        if (anyOk)
        {
            _related[key] = (DateTime.UtcNow, all);
            Interlocked.Exchange(ref _listsDirty, 1);
        }

        return all;
    }

    /// <summary>Une page de découverte TMDb (cache 3 jours, partagé entre les comptes).</summary>
    public Task<List<SeerrResult>> DiscoverAsync(SeerrClient cl, string mediaType, IReadOnlyDictionary<string, string> query, int page, CancellationToken ct)
        => CachedListAsync(
            "d:" + mediaType + ":" + page + ":" + string.Join("&", query.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)),
            DiscoverTtl, c => cl.DiscoverAsync(mediaType, query, page, c), ct);

    /// <summary>Films d'une saga TMDb (cache 7 jours).</summary>
    public Task<List<SeerrResult>> CollectionAsync(SeerrClient cl, int collectionId, CancellationToken ct)
        => CachedListAsync("c:" + collectionId, StableListTtl, c => cl.CollectionAsync(collectionId, c), ct);

    /// <summary>Titres réalisés, créés ou écrits par une personne (cache 7 jours).</summary>
    public Task<List<SeerrResult>> PersonWorksAsync(SeerrClient cl, int personId, CancellationToken ct)
        => CachedListAsync("p:" + personId, StableListTtl, c => cl.PersonWorksAsync(personId, c), ct);

    // liste en cache, sinon un appel (limité à 4 simultanés) ; un échec est mémorisé (404 : 7 jours, sinon 1 h) et donne une liste vide
    private async Task<List<SeerrResult>> CachedListAsync(string key, TimeSpan ttl, Func<CancellationToken, Task<List<SeerrResult>>> fetch, CancellationToken ct)
    {
        EnsureLoaded();
        var now = DateTime.UtcNow;
        if (_lists.TryGetValue(key, out var hit) && now - hit.At < ttl)
        {
            return hit.Items;
        }

        if (_failed.TryGetValue("l:" + key, out var until) && until > now)
        {
            return new List<SeerrResult>();
        }

        if (Down)
        {
            return new List<SeerrResult>();
        }

        await SlotAsync(ct).ConfigureAwait(false);
        try
        {
            if (Down)
            {
                return new List<SeerrResult>();
            }

            var items = await fetch(ct).ConfigureAwait(false);
            Answered();
            _lists[key] = (DateTime.UtcNow, items);
            Interlocked.Exchange(ref _listsDirty, 1);
            return items;
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            Failed(ex, ct);
            _failed["l:" + key] = DateTime.UtcNow + (ex is SeerrException { Status: System.Net.HttpStatusCode.NotFound } ? MissingTtl : FailedTtl);
            _log.LogDebug("Reco+: list {Key} unavailable ({Msg})", key, ex.Message);
            return new List<SeerrResult>();
        }
        finally
        {
            _throttle.Release();
        }
    }
}
