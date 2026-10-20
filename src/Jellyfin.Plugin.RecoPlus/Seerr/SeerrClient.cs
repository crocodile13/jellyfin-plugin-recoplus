using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RecoPlus.Seerr;

/// <summary>Erreur Seerr avec un message lisible (jamais la clé).</summary>
public sealed class SeerrException : Exception
{
    public SeerrException(string message, HttpStatusCode? status = null)
        : base(message) => Status = status;

    /// <summary>Code HTTP éventuel.</summary>
    public HttpStatusCode? Status { get; }
}

/// <summary>Compte Seerr lié à un compte Jellyfin.</summary>
public sealed record SeerrUser(int Id, string JellyfinUserId, string Name);

/// <summary>Demande Seerr.</summary>
public sealed record SeerrRequest(int RequestedBy, string MediaType, int TmdbId, int RequestStatus, int MediaStatus, DateTime CreatedAt);

/// <summary>Titre de la liste de suivi Seerr d'un compte.</summary>
public sealed record SeerrWatch(string MediaType, int TmdbId, DateTime CreatedAt);

/// <summary>Résultat d'une liste TMDb (recommandations / similaires).</summary>
public sealed record SeerrResult(string MediaType, int TmdbId, string Name, bool Adult, int MediaStatus, double Vote, string? PosterPath, string? Date);

/// <summary>Fiche détaillée d'un titre TMDb (ce qu'il faut au moteur et au contrôle parental).</summary>
public sealed class SeerrDetails
{
    public required string MediaType { get; init; }

    public required int TmdbId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int? Year { get; init; }

    public bool Adult { get; init; }

    public int MediaStatus { get; init; }

    public double Vote { get; init; }

    public string? PosterPath { get; init; }

    /// <summary>Image de fond TMDb (cartes en paysage).</summary>
    public string? BackdropPath { get; init; }

    public string? Overview { get; init; }

    public List<string> Genres { get; init; } = new();

    public List<string> Keywords { get; init; } = new();

    public List<string> Studios { get; init; } = new();

    public List<string> Actors { get; init; } = new();

    public List<string> Directors { get; init; } = new();

    public List<string> Writers { get; init; } = new();

    public List<string> Creators { get; init; } = new();

    public int EpisodeCount { get; init; }

    public int? CollectionId { get; init; }

    /// <summary>Nombre de votes TMDb (null : fiche lue par une ancienne version, à relire).</summary>
    public int? VoteCount { get; init; }

    /// <summary>Date de sortie ou de première diffusion (aaaa-mm-jj).</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Langue originale (ISO 639-1).</summary>
    public string? OriginalLanguage { get; init; }

    /// <summary>Pays de production (noms, comme Jellyfin).</summary>
    public List<string> Countries { get; init; } = new();

    /// <summary>Classification par pays (ISO 3166-1) : « FR » → « 12 ».</summary>
    public Dictionary<string, string> Certifications { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Client Seerr (API v1, clé d'API ; X-Api-User pour agir au nom d'un compte avec SES droits et quotas).
/// Ne dépend pas de Jellyfin : testable seul.
/// </summary>
public sealed class SeerrClient
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _key;

    public SeerrClient(HttpClient http, string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var b) || (b.Scheme != "http" && b.Scheme != "https"))
        {
            throw new SeerrException("URL Seerr invalide (attendu : http(s)://hôte:port)");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SeerrException("clé d'API Seerr non définie");
        }

        _http = http;
        _base = b;
        _key = apiKey;
    }

    private async Task<JsonElement> Get(string path, CancellationToken ct, int? asUser = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path.TrimStart('/')));
        req.Headers.Add("X-Api-Key", _key);
        if (asUser is int u)
        {
            req.Headers.Add("X-Api-User", u.ToString(CultureInfo.InvariantCulture));
        }

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new SeerrException($"Seerr a répondu {(int)res.StatusCode} sur {path.Split('?')[0]}", res.StatusCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SeerrException($"réponse non JSON sur {path.Split('?')[0]} (mauvaise URL ?)");
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static int MediaStatusOf(JsonElement e)
        => e.TryGetProperty("mediaInfo", out var m) && m.ValueKind == JsonValueKind.Object ? Int(m, "status") : 0;

    /// <summary>Version de Seerr (et vérification de la clé : /status est public, /auth/me non).</summary>
    public async Task<string> CheckAsync(CancellationToken ct)
    {
        var st = await Get("api/v1/status", ct).ConfigureAwait(false);
        var me = await Get("api/v1/auth/me", ct).ConfigureAwait(false);
        if (Int(me, "id") == 0)
        {
            throw new SeerrException("clé d'API refusée");
        }

        return Str(st, "version");
    }

    /// <summary>Tous les comptes Seerr liés à un compte Jellyfin.</summary>
    public async Task<List<SeerrUser>> UsersAsync(CancellationToken ct)
    {
        var res = new List<SeerrUser>();
        for (int skip = 0; skip < 10_000; skip += 100)
        {
            var page = await Get($"api/v1/user?take=100&skip={skip}", ct).ConfigureAwait(false);
            var items = Arr(page, "results").ToList();
            foreach (var u in items)
            {
                var jf = NormalizeGuid(Str(u, "jellyfinUserId"));
                if (jf is not null)
                {
                    res.Add(new SeerrUser(Int(u, "id"), jf, Str(u, "displayName")));
                }
            }

            if (items.Count < 100)
            {
                break;
            }
        }

        return res;
    }

    /// <summary>Toutes les demandes (tous statuts).</summary>
    public async Task<List<SeerrRequest>> RequestsAsync(CancellationToken ct)
    {
        var res = new List<SeerrRequest>();
        for (int skip = 0; skip < 100_000; skip += 100)
        {
            var page = await Get($"api/v1/request?take=100&skip={skip}&filter=all&sort=added", ct).ConfigureAwait(false);
            var items = Arr(page, "results").ToList();
            foreach (var r in items)
            {
                if (!r.TryGetProperty("media", out var m) || m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int by = r.TryGetProperty("requestedBy", out var rb) && rb.ValueKind == JsonValueKind.Object ? Int(rb, "id") : 0;
                var created = DateTime.TryParse(Str(r, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c) ? c : DateTime.UtcNow;
                var type = Str(m, "mediaType");
                if (Int(m, "tmdbId") > 0 && (type == "movie" || type == "tv"))
                {
                    res.Add(new SeerrRequest(by, type, Int(m, "tmdbId"), Int(r, "status"), Int(m, "status"), created));
                }
            }

            if (items.Count < 100)
            {
                break;
            }
        }

        return res;
    }

    /// <summary>
    /// Liste de suivi d'un compte Seerr (20 par page, au plus 25 pages). Les entrées sans date (liste Plex) sont
    /// ignorées : une intention sans date ne peut pas vieillir.
    /// </summary>
    public async Task<List<SeerrWatch>> WatchlistAsync(int seerrUserId, CancellationToken ct)
    {
        var res = new List<SeerrWatch>();
        for (int page = 1; page <= 25; page++)
        {
            var j = await Get($"api/v1/user/{seerrUserId}/watchlist?page={page}", ct).ConfigureAwait(false);
            foreach (var r in Arr(j, "results"))
            {
                var type = Str(r, "mediaType");
                int tmdb = Int(r, "tmdbId");
                if (tmdb > 0 && (type == "movie" || type == "tv")
                    && DateTime.TryParse(Str(r, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c))
                {
                    res.Add(new SeerrWatch(type, tmdb, c));
                }
            }

            if (page >= Int(j, "totalPages"))
            {
                break;
            }
        }

        return res;
    }

    /// <summary>Recommandations ou titres similaires TMDb (page 1).</summary>
    public async Task<List<SeerrResult>> RelatedAsync(string mediaType, int tmdbId, string relation, string language, CancellationToken ct)
    {
        var j = await Get($"api/v1/{mediaType}/{tmdbId}/{relation}?page=1&language={Uri.EscapeDataString(language)}", ct).ConfigureAwait(false);
        return Arr(j, "results").Select(r => new SeerrResult(
                string.IsNullOrEmpty(Str(r, "mediaType")) ? mediaType : Str(r, "mediaType"),
                Int(r, "id"),
                Str(r, mediaType == "movie" ? "title" : "name"),
                Bool(r, "adult"),
                MediaStatusOf(r),
                Dbl(r, "voteAverage"),
                Str(r, "posterPath"),
                Str(r, mediaType == "movie" ? "releaseDate" : "firstAirDate")))
            .Where(r => r.TmdbId > 0)
            .ToList();
    }

    /// <summary>Fiche détaillée.</summary>
    public async Task<SeerrDetails> DetailsAsync(string mediaType, int tmdbId, string language, CancellationToken ct)
    {
        var d = await Get($"api/v1/{mediaType}/{tmdbId}?language={Uri.EscapeDataString(language)}", ct).ConfigureAwait(false);
        return ParseDetails(mediaType, tmdbId, d);
    }

    /// <summary>Analyse une fiche (publique pour les tests).</summary>
    public static SeerrDetails ParseDetails(string mediaType, int tmdbId, JsonElement d)
    {
        bool movie = mediaType == "movie";
        var date = Str(d, movie ? "releaseDate" : "firstAirDate");
        int? year = date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        var credits = d.TryGetProperty("credits", out var cr) ? cr : default;
        var cast = credits.ValueKind == JsonValueKind.Object ? Arr(credits, "cast").OrderBy(c => Int(c, "order")).Select(c => Str(c, "name")).Where(n => n.Length > 0).Distinct().ToList() : new();
        var crew = credits.ValueKind == JsonValueKind.Object ? Arr(credits, "crew").ToList() : new();
        var certs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (movie && d.TryGetProperty("releases", out var rel))
        {
            foreach (var c in Arr(rel, "results"))
            {
                var cert = Arr(c, "release_dates").Select(r => Str(r, "certification")).FirstOrDefault(x => x.Length > 0);
                if (cert is not null)
                {
                    certs[Str(c, "iso_3166_1")] = cert;
                }
            }
        }
        else if (!movie && d.TryGetProperty("contentRatings", out var cra))
        {
            foreach (var c in Arr(cra, "results"))
            {
                var r = Str(c, "rating");
                if (r.Length > 0)
                {
                    certs[Str(c, "iso_3166_1")] = r;
                }
            }
        }

        int? coll = movie && d.TryGetProperty("collection", out var co) && co.ValueKind == JsonValueKind.Object && Int(co, "id") > 0 ? Int(co, "id") : null;
        return new SeerrDetails
        {
            MediaType = mediaType,
            TmdbId = tmdbId,
            Name = Str(d, movie ? "title" : "name"),
            Year = year,
            Adult = Bool(d, "adult"),
            MediaStatus = MediaStatusOf(d),
            Vote = Dbl(d, "voteAverage"),
            PosterPath = Str(d, "posterPath"),
            BackdropPath = Str(d, "backdropPath") is { Length: > 0 } bp ? bp : null,
            Overview = Str(d, "overview"),
            Genres = Arr(d, "genres").Select(g => Str(g, "name")).Where(n => n.Length > 0).ToList(),
            Keywords = Arr(d, "keywords").Select(g => Str(g, "name")).Where(n => n.Length > 0).ToList(),
            Studios = (movie ? Arr(d, "productionCompanies") : Arr(d, "networks")).Select(g => Str(g, "name")).Where(n => n.Length > 0).ToList(),
            Actors = cast,
            Directors = crew.Where(c => Str(c, "job") == "Director").Select(c => Str(c, "name")).Distinct().ToList(),
            Writers = crew.Where(c => Str(c, "job") is "Screenplay" or "Writer").Select(c => Str(c, "name")).Distinct().ToList(),
            Creators = Arr(d, "createdBy").Select(c => Str(c, "name")).Where(n => n.Length > 0).ToList(),
            EpisodeCount = Int(d, "numberOfEpisodes"),
            CollectionId = coll,
            Certifications = certs,
            VoteCount = d.TryGetProperty("voteCount", out var vc) && vc.ValueKind == JsonValueKind.Number && vc.TryGetInt32(out var vcn) ? vcn : null,
            ReleaseDate = date.Length >= 10 ? date[..10] : null,
            OriginalLanguage = Str(d, "originalLanguage") is { Length: > 0 } ol ? ol : null,
            Countries = Arr(d, "productionCountries").Select(c => Str(c, "name")).Where(n => n.Length > 0).ToList(),
        };
    }

    /// <summary>Crée une demande au nom du compte Seerr `asUser` (ses droits, ses quotas, son approbation automatique).</summary>
    public async Task<int> RequestAsync(int asUser, string mediaType, int tmdbId, CancellationToken ct)
    {
        var body = mediaType == "tv"
            ? JsonSerializer.Serialize(new { mediaType, mediaId = tmdbId, seasons = "all" })
            : JsonSerializer.Serialize(new { mediaType, mediaId = tmdbId });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "api/v1/request"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Api-Key", _key);
        req.Headers.Add("X-Api-User", asUser.ToString(CultureInfo.InvariantCulture));
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Created)
        {
            using var doc = JsonDocument.Parse(text);
            return Int(doc.RootElement, "id");
        }

        string msg = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            msg = Str(doc.RootElement, "message");
        }
        catch (JsonException)
        {
        }

        throw (int)res.StatusCode switch
        {
            403 => new SeerrException("demande refusée par Seerr (droits ou quota) " + msg, res.StatusCode),
            409 => new SeerrException("déjà demandé", res.StatusCode),
            202 => new SeerrException("rien à demander (saisons déjà disponibles)", res.StatusCode),
            _ => new SeerrException($"Seerr a répondu {(int)res.StatusCode} " + msg, res.StatusCode),
        };
    }

    /// <summary>Guid Jellyfin normalisé en « N » minuscule, ou null.</summary>
    public static string? NormalizeGuid(string? s) => Guid.TryParse(s, out var g) ? g.ToString("N") : null;
}
