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
    private readonly IHttpClientFactory _http;
    private readonly SecretStore _secrets;
    private readonly ILogger<SeerrService> _log;
    private readonly ConcurrentDictionary<string, SeerrDetails> _details = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, List<SeerrResult> Items)> _related = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(4);
    private bool _loaded;

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

            return Last = new SeerrSnapshot
            {
                UserMap = map,
                RequestsByUser = byUser,
                Requested = reqs.Where(r => r.RequestStatus is 1 or 2).Select(r => r.MediaType + ":" + r.TmdbId).ToHashSet(StringComparer.Ordinal),
                Version = version,
                Ok = true,
                Status = $"connecté (v{version}) — {map.Count} comptes liés, {reqs.Count} demandes",
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

        _loaded = true;
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
    }

    /// <summary>Sauvegarde le cache des fiches.</summary>
    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var f = Path.Combine(CacheDir, "fiches.json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(_details.Values.ToList()));
            File.Move(f + ".tmp", f, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Reco+ : écriture du cache Seerr impossible");
        }
    }

    /// <summary>Fiche déjà en cache (sans appel réseau), ou null.</summary>
    public SeerrDetails? CachedDetails(string mediaType, int tmdbId)
    {
        EnsureLoaded();
        return _details.TryGetValue(mediaType + ":" + tmdbId, out var d) ? d : null;
    }

    /// <summary>Fiche (cache 14 jours). Null si indisponible.</summary>
    public async Task<SeerrDetails?> DetailsAsync(SeerrClient cl, string mediaType, int tmdbId, CancellationToken ct)
    {
        EnsureLoaded();
        var key = mediaType + ":" + tmdbId;
        if (_details.TryGetValue(key, out var d) && DateTime.UtcNow - d.FetchedAt < DetailsTtl)
        {
            return d;
        }

        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            d = await cl.DetailsAsync(mediaType, tmdbId, Plugin.Instance!.Configuration.SeerrLanguage, ct).ConfigureAwait(false);
            _details[key] = d;
            return d;
        }
        catch (Exception ex) when (ex is SeerrException or HttpRequestException or JsonException)
        {
            _log.LogDebug("Reco+ : fiche {Key} indisponible ({Msg})", key, ex.Message);
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Recommandations + similaires TMDb d'un titre (cache 3 jours).</summary>
    public async Task<List<SeerrResult>> RelatedAsync(SeerrClient cl, string mediaType, int tmdbId, CancellationToken ct)
    {
        var key = mediaType + ":" + tmdbId;
        if (_related.TryGetValue(key, out var r) && DateTime.UtcNow - r.At < RelatedTtl)
        {
            return r.Items;
        }

        var all = new List<SeerrResult>();
        foreach (var rel in new[] { "recommendations", "similar" })
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                all.AddRange(await cl.RelatedAsync(mediaType, tmdbId, rel, Plugin.Instance!.Configuration.SeerrLanguage, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SeerrException or HttpRequestException or JsonException)
            {
                _log.LogDebug("Reco+ : {Rel} de {Key} indisponible ({Msg})", rel, key, ex.Message);
            }
            finally
            {
                _throttle.Release();
            }
        }

        _related[key] = (DateTime.UtcNow, all);
        return all;
    }
}
