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

    /// <summary>État lisible.</summary>
    public string Status { get; init; } = "désactivé";
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
    private readonly SemaphoreSlim _throttle = new(4);
    private readonly object _loadLock = new();
    private volatile bool _loaded;
    private int _dirty;

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
                Status = $"connecté (v{version}) — {map.Count} comptes liés, {reqs.Count} demandes, {watch.Values.Sum(l => l.Count)} titres en liste de suivi"
                    + (watchErrors > 0 ? $" ({watchErrors} listes illisibles)" : string.Empty),
            };
        }
        catch (Exception ex) when (ex is SeerrException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            _log.LogWarning("Reco+ : Seerr indisponible ({Msg}) — recommandations calculées sans lui", ex.Message);
            return Last = new SeerrSnapshot { Status = "indisponible : " + ex.Message };
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
                _log.LogWarning("Reco+ : cache des fiches Seerr illisible, repart de zéro");
            }

            _loaded = true;
        }
    }

    /// <summary>Sauvegarde le cache des fiches, seulement s'il a changé (fiches expirées retirées au passage).</summary>
    public void SaveCache()
    {
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
            if (now - l.At > StableListTtl)
            {
                _lists.TryRemove(k, out _);
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
            _log.LogWarning(ex, "Reco+ : écriture du cache Seerr impossible");
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

        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            d = await cl.DetailsAsync(mediaType, tmdbId, Plugin.Instance!.Configuration.SeerrLanguage, ct).ConfigureAwait(false);
            _details[key] = d;
            _failed.TryRemove(key, out _);
            Interlocked.Exchange(ref _dirty, 1);
            return d;
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            _failed[key] = DateTime.UtcNow + (ex is SeerrException { Status: System.Net.HttpStatusCode.NotFound } ? MissingTtl : FailedTtl);
            _log.LogDebug("Reco+ : fiche {Key} indisponible ({Msg})", key, ex.Message);
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
                await _throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var items = await cl.RelatedAsync(mediaType, tmdbId, rel, Plugin.Instance!.Configuration.SeerrLanguage, page, ct).ConfigureAwait(false);
                    got = items.Count;
                    anyOk = true;
                    all.AddRange(items);
                }
                catch (Exception ex) when (Transient(ex, ct))
                {
                    _log.LogDebug("Reco+ : {Rel} de {Key} (page {Page}) indisponible ({Msg})", rel, key, page, ex.Message);
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
        var now = DateTime.UtcNow;
        if (_lists.TryGetValue(key, out var hit) && now - hit.At < ttl)
        {
            return hit.Items;
        }

        if (_failed.TryGetValue("l:" + key, out var until) && until > now)
        {
            return new List<SeerrResult>();
        }

        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await fetch(ct).ConfigureAwait(false);
            _lists[key] = (DateTime.UtcNow, items);
            return items;
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            _failed["l:" + key] = DateTime.UtcNow + (ex is SeerrException { Status: System.Net.HttpStatusCode.NotFound } ? MissingTtl : FailedTtl);
            _log.LogDebug("Reco+ : liste {Key} indisponible ({Msg})", key, ex.Message);
            return new List<SeerrResult>();
        }
        finally
        {
            _throttle.Release();
        }
    }
}
