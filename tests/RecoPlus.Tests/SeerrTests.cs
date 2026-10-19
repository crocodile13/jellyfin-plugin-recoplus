using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RecoPlus.Seerr;
using Xunit;

namespace RecoPlus.Tests;

public class SeerrTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        public readonly List<string> Bodies = new();
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;

        public Fake(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return _f(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body)
        => new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    [Fact]
    public void ParsesMovieDetails()
    {
        var j = JsonDocument.Parse("""
        {"title":"Le Film","releaseDate":"2021-05-04","adult":false,"voteAverage":7.4,"posterPath":"/p.jpg",
         "genres":[{"id":1,"name":"Drame"}],"keywords":[{"id":2,"name":"Based On Manga"}],
         "productionCompanies":[{"name":"Studio A"}],"collection":{"id":99,"name":"Saga"},
         "credits":{"cast":[{"name":"B","order":1},{"name":"A","order":0}],"crew":[{"name":"R","job":"Director"},{"name":"S","job":"Screenplay"}]},
         "releases":{"results":[{"iso_3166_1":"FR","release_dates":[{"certification":""},{"certification":"12"}]},{"iso_3166_1":"US","release_dates":[{"certification":"PG-13"}]}]},
         "mediaInfo":{"status":5}}
        """).RootElement;
        var d = SeerrClient.ParseDetails("movie", 42, j);
        Assert.Equal("Le Film", d.Name);
        Assert.Equal(2021, d.Year);
        Assert.Equal(new[] { "A", "B" }, d.Actors);                     // ordre du générique
        Assert.Equal(new[] { "R" }, d.Directors);
        Assert.Equal(new[] { "S" }, d.Writers);
        Assert.Equal(99, d.CollectionId);
        Assert.Equal("12", d.Certifications["FR"]);                      // première classification non vide
        Assert.Equal("PG-13", d.Certifications["US"]);
        Assert.Equal(5, d.MediaStatus);
    }

    [Fact]
    public void ParsesTvDetails()
    {
        var j = JsonDocument.Parse("""
        {"name":"La Série","firstAirDate":"2019-01-01","numberOfEpisodes":24,"createdBy":[{"name":"C"}],"networks":[{"name":"Net"}],
         "contentRatings":{"results":[{"iso_3166_1":"FR","rating":"16"}]},"genres":[],"keywords":[]}
        """).RootElement;
        var d = SeerrClient.ParseDetails("tv", 7, j);
        Assert.Equal(24, d.EpisodeCount);
        Assert.Equal(new[] { "C" }, d.Creators);
        Assert.Equal(new[] { "Net" }, d.Studios);
        Assert.Equal("16", d.Certifications["FR"]);
        Assert.Null(d.CollectionId);
    }

    [Fact]
    public async Task RequestActsAsUserAndMapsErrors()
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/request", StringComparison.Ordinal)
            ? (r.Headers.GetValues("X-Api-User").Single() == "5" ? Json(HttpStatusCode.Created, new { id = 31 }) : Json(HttpStatusCode.Forbidden, new { message = "quota" }))
            : Json(HttpStatusCode.NotFound, new { }));
        var cl = new SeerrClient(new HttpClient(fake), "http://seerr:5055", "clé-secrète-1234");
        Assert.Equal(31, await cl.RequestAsync(5, "movie", 10, CancellationToken.None));
        Assert.Contains("\"mediaId\":10", fake.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("clé-secrète-1234", fake.Seen[0].Headers.GetValues("X-Api-Key").Single());
        var ex = await Assert.ThrowsAsync<SeerrException>(() => cl.RequestAsync(6, "tv", 10, CancellationToken.None));
        Assert.Contains("quota", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1234", ex.Message, StringComparison.Ordinal);    // jamais la clé dans un message
        Assert.Contains("\"seasons\":\"all\"", fake.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagesUsersAndRequests()
    {
        var u1 = Guid.NewGuid();
        var fake = new Fake(r =>
        {
            var q = r.RequestUri!.Query;
            if (r.RequestUri.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(q)["skip"]!, System.Globalization.CultureInfo.InvariantCulture);
                var items = skip == 0
                    ? Enumerable.Range(0, 100).Select(i => (object)new { id = i + 1, jellyfinUserId = i == 0 ? u1.ToString() : null, displayName = "x" }).ToArray()
                    : new object[] { new { id = 101, jellyfinUserId = Guid.NewGuid().ToString("N"), displayName = "y" } };
                return Json(HttpStatusCode.OK, new { results = items });
            }

            return Json(HttpStatusCode.OK, new
            {
                results = new object[]
                {
                    new { status = 2, createdAt = "2026-10-01T10:00:00.000Z", media = new { tmdbId = 5, mediaType = "movie", status = 3 }, requestedBy = new { id = 1 } },
                    new { status = 1, createdAt = "2026-10-02T10:00:00.000Z", media = (object?)null, requestedBy = new { id = 1 } },
                },
            });
        });
        var cl = new SeerrClient(new HttpClient(fake), "http://seerr:5055/", "k");
        var users = await cl.UsersAsync(CancellationToken.None);
        Assert.Equal(2, users.Count);                                         // seuls les comptes liés à Jellyfin
        Assert.Equal(u1.ToString("N"), users[0].JellyfinUserId);             // Guid normalisé
        var reqs = await cl.RequestsAsync(CancellationToken.None);
        Assert.Single(reqs);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), reqs[0].CreatedAt);
    }

    [Fact]
    public async Task ReadsWatchlistPagesAndSkipsUndatedEntries()
    {
        var fake = new Fake(r =>
        {
            Assert.True(r.RequestUri!.AbsolutePath.EndsWith("/api/v1/user/7/watchlist", StringComparison.Ordinal));
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            return page == 1
                ? Json(HttpStatusCode.OK, new
                {
                    page = 1, totalPages = 2, totalResults = 3,
                    results = new object[]
                    {
                        new { id = 1, mediaType = "movie", tmdbId = 603, createdAt = "2026-10-01T10:00:00.000Z" },
                        new { id = 2, mediaType = "tv", tmdbId = 1399 },                       // liste Plex : pas de date
                    },
                })
                : Json(HttpStatusCode.OK, new { page = 2, totalPages = 2, results = new object[] { new { mediaType = "tv", tmdbId = 1396, createdAt = "2026-09-01T00:00:00Z" } } });
        });
        var cl = new SeerrClient(new HttpClient(fake), "http://seerr:5055", "k");
        var w = await cl.WatchlistAsync(7, CancellationToken.None);
        Assert.Equal(2, fake.Seen.Count);
        Assert.Equal(new[] { "movie:603", "tv:1396" }, w.Select(x => x.MediaType + ":" + x.TmdbId).ToArray());
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), w[0].CreatedAt);
    }

    [Fact]
    public void RejectsBadConfiguration()
    {
        Assert.Throws<SeerrException>(() => new SeerrClient(new HttpClient(), "ftp://x", "k"));
        Assert.Throws<SeerrException>(() => new SeerrClient(new HttpClient(), "http://x", " "));
    }
}
