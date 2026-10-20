using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Le moteur C# doit reproduire exactement le prototype Python (golden.json, données synthétiques).</summary>
public class ParityTests
{
    private const double Tol = 1e-9;
    private static readonly JsonDocument Golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden.json")));

    internal static Dataset LoadDataset(JsonElement d)
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        foreach (var t in d.GetProperty("titles").EnumerateArray())
        {
            var people = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var p in t.GetProperty("people").EnumerateObject())
            {
                people[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
            }

            titles[t.GetProperty("id").GetString()!] = new Title
            {
                Id = t.GetProperty("id").GetString()!,
                Kind = t.GetProperty("kind").GetString() == "Movie" ? TitleKind.Movie : TitleKind.Series,
                Name = t.GetProperty("name").GetString()!,
                Year = t.GetProperty("year").ValueKind == JsonValueKind.Null ? null : t.GetProperty("year").GetInt32(),
                Genres = Strings(t, "genres"),
                Tags = Strings(t, "tags"),
                Studios = Strings(t, "studios"),
                People = people,
                Community = t.GetProperty("community").ValueKind == JsonValueKind.Null ? null : t.GetProperty("community").GetDouble(),
                Added = t.GetProperty("added").ValueKind == JsonValueKind.Null ? null : t.GetProperty("added").GetDouble(),
                EpisodeCount = t.GetProperty("episodes").GetInt32(),
                Released = NumOrNull(t, "released"),
                Critic = NumOrNull(t, "critic"),
                Countries = t.TryGetProperty("countries", out _) ? Strings(t, "countries") : Array.Empty<string>(),
                Language = t.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String ? lg.GetString() : null,
                Votes = NumOrNull(t, "votes") is double vo ? (int)vo : null,
                External = t.TryGetProperty("external", out var ex) && ex.ValueKind == JsonValueKind.True,
            };
        }

        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal);
        foreach (var u in d.GetProperty("users").EnumerateObject())
        {
            var h = new Dictionary<string, Interaction>(StringComparer.Ordinal);
            foreach (var x in u.Value.EnumerateArray())
            {
                var id = x.GetProperty("title").GetString()!;
                h[id] = new Interaction
                {
                    Title = id,
                    Start = x.GetProperty("start").GetDouble(),
                    Last = x.GetProperty("last").GetDouble(),
                    Completion = x.GetProperty("completion").GetDouble(),
                    Played = x.GetProperty("played").GetBoolean(),
                    Episodes = x.GetProperty("episodes").GetInt32(),
                    Plays = x.GetProperty("plays").GetInt32(),
                    Favorite = x.GetProperty("fav").GetBoolean(),
                    Requested = NumOrNull(x, "requested") ?? 0,
                    Watchlisted = NumOrNull(x, "watchlisted") ?? 0,
                    Rewatch = NumOrNull(x, "rewatch") ?? 0,
                    FavSeasons = (int)(NumOrNull(x, "fav_seasons") ?? 0),
                    FavEpisodes = (int)(NumOrNull(x, "fav_episodes") ?? 0),
                    Rating = NumOrNull(x, "rating"),
                    Hidden = x.TryGetProperty("hidden", out var hd) && hd.GetBoolean(),
                    Hours = NumOrNull(x, "hours") ?? 0,
                    Backfill = x.TryGetProperty("backfill", out var bf) && bf.GetBoolean(),
                };
            }

            users[u.Name] = h;
        }

        var colls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in d.GetProperty("collections").EnumerateObject())
        {
            colls[c.Name] = c.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
        }

        var reviews = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        if (d.TryGetProperty("reviews", out var rv))
        {
            foreach (var t in rv.EnumerateObject())
            {
                reviews[t.Name] = t.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble(), StringComparer.Ordinal);
            }
        }

        int shift = d.TryGetProperty("utc_offset_hours", out var off) && off.ValueKind == JsonValueKind.Number ? off.GetInt32() : 0;
        var ds = new Dataset
        {
            Titles = titles, Users = users, Collections = colls, Now = d.GetProperty("now").GetDouble(), Reviews = reviews,
            LocalHour = shift == 0 ? null : t => (int)((((t + (shift * 3600)) % 86400) + 86400) % 86400 / 3600),
        };
        ds.LinkCollections();
        return ds;
    }

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static double? NumOrNull(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double Num(JsonElement e, string name, double fallback) => NumOrNull(e, name) ?? fallback;

    internal static EngineSettings Settings(JsonElement c)
    {
        var p = c.GetProperty("P");
        var w = c.GetProperty("W");
        var feat = c.TryGetProperty("feat", out var fe) ? fe : default;
        var d = new EngineSettings();
        string rating = w.TryGetProperty("rating", out var rs) ? rs.GetString()! : "community";
        return new EngineSettings
        {
            RequestWeight = Num(p, "request_weight", d.RequestWeight),
            RequestHalfLifeDays = Num(p, "request_half_life", d.RequestHalfLifeDays),
            RequestMaxDays = Num(p, "request_max_days", d.RequestMaxDays),
            WatchlistWeight = Num(p, "watchlist_weight", d.WatchlistWeight),
            FavSeasonShare = Num(p, "fav_season_share", d.FavSeasonShare),
            FavEpisodeShare = Num(p, "fav_episode_share", d.FavEpisodeShare),
            RewatchBoost = Num(p, "rewatch_boost", d.RewatchBoost),
            RatingWeight = Num(p, "rating_weight", d.RatingWeight),
            HiddenWeight = Num(p, "hidden_weight", d.HiddenWeight),
            WRelease = Num(w, "release", d.WRelease),
            ReleaseDays = Num(w, "release_days", d.ReleaseDays),
            RatingPriorVotes = Num(w, "rating_prior_votes", d.RatingPriorVotes),
            WHousehold = Num(w, "household", d.WHousehold),
            HouseholdPrior = Num(w, "household_prior", d.HouseholdPrior),
            Rating = rating switch { "critic" => RatingSource.Critic, "best" => RatingSource.Best, "mean" => RatingSource.Mean, _ => RatingSource.Community },
            FeatCountry = feat.ValueKind == JsonValueKind.Object ? Num(feat, "n", 0) : 0,
            FeatLanguage = feat.ValueKind == JsonValueKind.Object ? Num(feat, "l", 0) : 0,
            SeriesByTime = !p.TryGetProperty("series_by_time", out var sbt) || sbt.GetBoolean(),
            SeriesFullHours = Num(p, "series_full_hours", d.SeriesFullHours),
            SeriesMaxWeight = Num(p, "series_max_weight", d.SeriesMaxWeight),
            HalfLifeDays = p.GetProperty("half_life").GetDouble(),
            RecencyFloor = p.GetProperty("recency_floor").GetDouble(),
            FavoriteBoost = p.GetProperty("fav_boost").GetDouble(),
            MovieDone = p.GetProperty("movie_done").GetDouble(),
            Zap = p.GetProperty("zap").GetDouble(),
            AbandonDays = p.GetProperty("abandon_days").GetDouble(),
            AbandonWeight = p.GetProperty("abandon_weight").GetDouble(),
            SeriesFullEpisodes = p.GetProperty("series_full_eps").GetInt32(),
            SeriesAbandonEpisodes = p.GetProperty("series_abandon_eps").GetInt32(),
            SeriesAbandonFraction = p.GetProperty("series_abandon_frac").GetDouble(),
            SeriesMinEpisodes = p.GetProperty("series_min_eps").GetInt32(),
            Decay = p.GetProperty("decay").GetString() == "power" ? DecayKind.Power : DecayKind.Exponential,
            Shrink = p.GetProperty("shrink").GetDouble(),
            MinCommon = p.GetProperty("min_common").GetInt32(),
            WCentroid = w.GetProperty("centroid").GetDouble(),
            WMaxSim = w.GetProperty("maxsim").GetDouble(),
            WCollab = w.GetProperty("collab").GetDouble(),
            WPopularity = w.GetProperty("pop").GetDouble(),
            WQuality = w.GetProperty("quality").GetDouble(),
            WNegative = w.GetProperty("neg").GetDouble(),
            WFresh = w.GetProperty("fresh").GetDouble(),
            FreshDays = w.GetProperty("fresh_days").GetDouble(),
            GateTop = w.GetProperty("gate_top").GetInt32(),
            Weighting = c.GetProperty("scheme").GetString() == "bm25" ? WeightingScheme.Bm25 : WeightingScheme.TfIdf,
            Mmr = c.GetProperty("mmr").GetDouble(),
            WShortTerm = Num(w, "short", 0),
            WTimeOfDay = Num(w, "tod", 0),
            ShortTermTitles = (int)Num(w, "short_n", 5),
            WImpression = Num(w, "imp", d.WImpression),
            ImpressionDays = Num(w, "imp_days", d.ImpressionDays),
            ImpressionRecoveryDays = Num(w, "imp_recovery", d.ImpressionRecoveryDays),
            Diversity = (c.TryGetProperty("div", out var dv) ? dv.GetString() : "fixed") switch
            {
                "personalized" => DiversityMode.Personalized,
                "calibrated" => DiversityMode.Calibrated,
                _ => DiversityMode.Fixed,
            },
            CalibrationStrength = Num(c, "cal", 0.5),
            Neural = w.TryGetProperty("neural", out var nn) && nn.ValueKind == JsonValueKind.Array ? nn.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null,
            WNeural = Num(w, "w_neural", 1.0),
        };
    }

    public static IEnumerable<object[]> Cases()
        => Golden.RootElement.GetProperty("cases").EnumerateArray().Select((c, i) => new object[] { i, c.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsPrototype(int index, string name)
    {
        var c = Golden.RootElement.GetProperty("cases")[index];
        var ds = LoadDataset(Golden.RootElement.GetProperty("dataset"));
        var s = Settings(c);
        var exp = c.GetProperty("expected");

        var vec = Core.BuildVectors(ds.Titles, s);
        foreach (var v in exp.GetProperty("vectors").EnumerateObject())
        {
            var got = vec[v.Name];
            var want = v.Value.EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetDouble());
            Assert.Equal(want.Count, got.Keys.Length);
            for (int i = 0; i < got.Keys.Length; i++)
            {
                Assert.True(Math.Abs(want[got.Keys[i]] - got.Values[i]) < Tol, $"{name}: vecteur {v.Name} {got.Keys[i]}");
            }
        }

        var ctx = new Context(ds, ds.Now, ds.Users, s);
        foreach (var u in exp.GetProperty("signals").EnumerateObject())
        {
            foreach (var t in u.Value.EnumerateObject())
            {
                Assert.True(Math.Abs(t.Value.GetDouble() - ctx.Sig[u.Name][t.Name]) < Tol, $"{name}: signal {u.Name}/{t.Name}");
            }
        }

        foreach (var i in exp.GetProperty("cooc").EnumerateObject())
        {
            var want = i.Value.EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetDouble());
            Assert.Equal(want.Count, ctx.Cooc[i.Name].Count);
            foreach (var (j, v) in want)
            {
                Assert.True(Math.Abs(v - ctx.Cooc[i.Name][j]) < Tol, $"{name}: cooc {i.Name}/{j}");
            }
        }

        foreach (var u in exp.GetProperty("users").EnumerateObject())
        {
            var cands = u.Value.GetProperty("cands").EnumerateArray().Select(x => x.GetString()!).ToList();
            var expo = Golden.RootElement.GetProperty("dataset").TryGetProperty("exposure", out var ex) && ex.TryGetProperty(u.Name, out var eu)
                ? eu.EnumerateObject().ToDictionary(e => e.Name, e => new Exposure(e.Value[0].GetInt32(), e.Value[1].GetDouble()), StringComparer.Ordinal)
                : null;
            var sc = Recommender.Score(ctx, vec, u.Name, cands, s, exposure: expo);
            foreach (var e in u.Value.GetProperty("scores").EnumerateObject())
            {
                Assert.True(Math.Abs(e.Value.GetDouble() - sc[e.Name]) < Tol, $"{name}: score {u.Name}/{e.Name} {e.Value.GetDouble()} ≠ {sc[e.Name]}");
            }

            var list = Recommender.Rank(ctx, vec, cands, sc, 12, s, u.Name);
            var want = u.Value.GetProperty("list").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal(want, list);
        }
    }
}
