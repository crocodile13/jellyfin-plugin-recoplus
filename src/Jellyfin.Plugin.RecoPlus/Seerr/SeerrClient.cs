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
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Seerr;

/// <summary>Erreur Seerr avec un message lisible (jamais la clé).</summary>
public sealed class SeerrException : Exception
{
    /// <param name="packed">Message emballé (<see cref="Texts.Pack"/>) ; <see cref="Exception.Message"/> en est le rendu anglais (journal).</param>
    /// <param name="status">Code HTTP éventuel.</param>
    public SeerrException(string packed, HttpStatusCode? status = null)
        : base(Texts.Render(packed, "en"))
    {
        Packed = packed;
        Status = status;
    }

    /// <summary>Message emballé, à rendre dans la langue de celui qui le lit.</summary>
    public string Packed { get; }

    /// <summary>Message d'une erreur à montrer : emballé pour une erreur Seerr, tel quel sinon.</summary>
    public static string TextOf(Exception ex) => ex is SeerrException s ? s.Packed : ex.Message;

    /// <summary>Code HTTP éventuel.</summary>
    public HttpStatusCode? Status { get; }
}

/// <summary>Compte Seerr lié à un compte Jellyfin.</summary>
public sealed record SeerrUser(int Id, string JellyfinUserId, string Name);

/// <summary>Demande Seerr.</summary>
public sealed record SeerrRequest(int RequestedBy, string MediaType, int TmdbId, int RequestStatus, int MediaStatus, DateTime CreatedAt);

/// <summary>Titre de la liste de suivi Seerr d'un compte.</summary>
public sealed record SeerrWatch(string MediaType, int TmdbId, DateTime CreatedAt);

/// <summary>Résultat d'une liste TMDb (recommandations, similaires, découverte, saga, filmographie).</summary>
public sealed record SeerrResult(string MediaType, int TmdbId, string Name, bool Adult, int MediaStatus, double Vote, string? PosterPath, string? Date, int VoteCount = 0);

/// <summary>Élément TMDb nommé (genre, mot-clé, personne, studio, chaîne).</summary>
public sealed record SeerrRef(int Id, string Name);

/// <summary>Fiche détaillée d'un titre TMDb (ce qu'il faut au moteur et au contrôle parental).</summary>
public sealed class SeerrDetails
{
    /// <summary>Version du format de fiche : une fiche d'un format plus ancien est relue.</summary>
    public const int CurrentFormat = 2;

    /// <summary>Format de cette fiche (0 : écrite par une version antérieure à 1.0.15).</summary>
    public int Format { get; init; }

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

    public List<string> Genres { get; init; } = new();

    public List<string> Keywords { get; init; } = new();

    public List<string> Studios { get; init; } = new();

    public List<string> Actors { get; init; } = new();

    public List<string> Directors { get; init; } = new();

    public List<string> Writers { get; init; } = new();

    public List<string> Creators { get; init; } = new();

    public int EpisodeCount { get; init; }

    public int? CollectionId { get; init; }

    /// <summary>Genres avec leur identifiant TMDb (exploration par genre).</summary>
    public List<SeerrRef> GenreRefs { get; init; } = new();

    /// <summary>Mots-clés avec leur identifiant TMDb (exploration par thème).</summary>
    public List<SeerrRef> KeywordRefs { get; init; } = new();

    /// <summary>Réalisateurs, créateurs et scénaristes avec leur identifiant TMDb (exploration par auteur).</summary>
    public List<SeerrRef> PeopleRefs { get; init; } = new();

    /// <summary>Studios (films) ou chaînes (séries) avec leur identifiant TMDb (exploration par studio).</summary>
    public List<SeerrRef> StudioRefs { get; init; } = new();

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
            throw new SeerrException(Texts.Pack("Seerr a répondu {0} sur {1}", (int)res.StatusCode, path.Split('?')[0]), res.StatusCode);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SeerrException(Texts.Pack("réponse non JSON sur {0} (mauvaise URL ?)", path.Split('?')[0]));
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

    /// <summary>Recommandations ou titres similaires TMDb (une page de résultats, 20 titres environ).</summary>
    public async Task<List<SeerrResult>> RelatedAsync(string mediaType, int tmdbId, string relation, string language, int page, CancellationToken ct)
    {
        var j = await Get($"api/v1/{mediaType}/{tmdbId}/{relation}?page={Math.Clamp(page, 1, 10)}&language={Uri.EscapeDataString(language)}", ct).ConfigureAwait(false);
        return MapResults(Arr(j, "results"), mediaType);
    }

    /// <summary>Titres d'une liste TMDb (publique pour les tests) ; la sorte de chaque titre vient du titre, sinon de la liste.</summary>
    public static List<SeerrResult> MapResults(IEnumerable<JsonElement> items, string defaultType)
        => items.Select(r =>
            {
                var type = Str(r, "mediaType") is { Length: > 0 } t ? t : defaultType;
                bool movie = type == "movie";
                return new SeerrResult(type, Int(r, "id"), Str(r, movie ? "title" : "name"), Bool(r, "adult"), MediaStatusOf(r),
                    Dbl(r, "voteAverage"), Str(r, "posterPath"), Str(r, movie ? "releaseDate" : "firstAirDate"), Int(r, "voteCount"));
            })
            .Where(r => r.TmdbId > 0 && r.MediaType is "movie" or "tv")
            .ToList();

    /// <summary>
    /// Découverte TMDb (films ou séries) : <paramref name="query"/> = paramètres Seerr (genre, keywords, studio, network,
    /// sortBy, voteAverageGte, voteCountGte, language = langue ORIGINALE ou « all »…). Une page.
    /// </summary>
    public async Task<List<SeerrResult>> DiscoverAsync(string mediaType, IReadOnlyDictionary<string, string> query, int page, CancellationToken ct)
    {
        var qs = string.Join("&", query.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        var j = await Get($"api/v1/discover/{(mediaType == "tv" ? "tv" : "movies")}?page={Math.Clamp(page, 1, 500)}" + (qs.Length > 0 ? "&" + qs : string.Empty), ct).ConfigureAwait(false);
        return MapResults(Arr(j, "results"), mediaType == "tv" ? "tv" : "movie");
    }

    /// <summary>Films d'une saga TMDb, par date de sortie.</summary>
    public async Task<List<SeerrResult>> CollectionAsync(int collectionId, CancellationToken ct)
    {
        var j = await Get($"api/v1/collection/{collectionId}", ct).ConfigureAwait(false);
        return MapResults(Arr(j, "parts"), "movie");
    }

    /// <summary>Titres qu'une personne a réalisés, créés ou écrits (filmographie TMDb, sans ses rôles d'acteur).</summary>
    public async Task<List<SeerrResult>> PersonWorksAsync(int personId, CancellationToken ct)
    {
        var j = await Get($"api/v1/person/{personId}/combined_credits", ct).ConfigureAwait(false);
        return MapResults(Arr(j, "crew").Where(IsAuthorCredit), "movie").DistinctBy(r => r.MediaType + ":" + r.TmdbId).ToList();
    }

    /// <summary>Crédit d'auteur : réalisation, création, scénario (pas le reste de l'équipe technique).</summary>
    public static bool IsAuthorCredit(JsonElement c)
        => Str(c, "job") is "Director" or "Creator" or "Screenplay" or "Writer" or "Story" or "Novel" or "Original Story" or "Series Composition";   // i18n:skip (valeurs TMDb)

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
                // comme Jellyfin Enhanced : la sortie en salle (type 3) d'abord, sinon la première classification
                var dates = Arr(c, "release_dates").ToList();
                var cert = dates.Where(r => Int(r, "type") == 3).Select(r => Str(r, "certification")).FirstOrDefault(x => x.Length > 0)
                           ?? dates.Select(r => Str(r, "certification")).FirstOrDefault(x => x.Length > 0);
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
        static List<SeerrRef> Refs(IEnumerable<JsonElement> items)
            => items.Select(g => new SeerrRef(Int(g, "id"), Str(g, "name"))).Where(r => r.Id > 0 && r.Name.Length > 0).DistinctBy(r => r.Id).ToList();
        var authors = crew.Where(IsAuthorCredit).Concat(Arr(d, "createdBy"));
        return new SeerrDetails
        {
            Format = SeerrDetails.CurrentFormat,
            GenreRefs = Refs(Arr(d, "genres")),
            KeywordRefs = Refs(Arr(d, "keywords")),
            PeopleRefs = Refs(authors),
            StudioRefs = Refs(movie ? Arr(d, "productionCompanies") : Arr(d, "networks")),
            MediaType = mediaType,
            TmdbId = tmdbId,
            Name = Str(d, movie ? "title" : "name"),
            Year = year,
            Adult = Bool(d, "adult"),
            MediaStatus = MediaStatusOf(d),
            Vote = Dbl(d, "voteAverage"),
            PosterPath = Str(d, "posterPath"),
            BackdropPath = Str(d, "backdropPath") is { Length: > 0 } bp ? bp : null,
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
            try
            {
                using var doc = JsonDocument.Parse(text);
                return Int(doc.RootElement, "id");
            }
            catch (JsonException)
            {
                return 0; // demande créée, réponse illisible : la demande existe quand même
            }
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
            403 => new SeerrException(msg.Length > 0 ? Texts.Pack("demande refusée par Seerr (droits ou quota) : {0}", msg) : "demande refusée par Seerr (droits ou quota)", res.StatusCode),
            409 => new SeerrException("déjà demandé", res.StatusCode),
            202 => new SeerrException("rien à demander (saisons déjà disponibles)", res.StatusCode),
            _ => new SeerrException(msg.Length > 0 ? Texts.Pack("Seerr a répondu {0} : {1}", (int)res.StatusCode, msg) : Texts.Pack("Seerr a répondu {0}", (int)res.StatusCode), res.StatusCode),
        };
    }

    /// <summary>Guid Jellyfin normalisé en « N » minuscule, ou null.</summary>
    public static string? NormalizeGuid(string? s) => Guid.TryParse(s, out var g) ? g.ToString("N") : null;
}
