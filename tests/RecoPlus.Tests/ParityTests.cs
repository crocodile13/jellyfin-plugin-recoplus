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
                };
            }

            users[u.Name] = h;
        }

        var colls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in d.GetProperty("collections").EnumerateObject())
        {
            colls[c.Name] = c.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
        }

        var ds = new Dataset { Titles = titles, Users = users, Collections = colls, Now = d.GetProperty("now").GetDouble() };
        ds.LinkCollections();
        return ds;
    }

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    internal static EngineSettings Settings(JsonElement c)
    {
        var p = c.GetProperty("P");
        var w = c.GetProperty("W");
        return new EngineSettings
        {
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
            var sc = Recommender.Score(ctx, vec, u.Name, cands, s);
            foreach (var e in u.Value.GetProperty("scores").EnumerateObject())
            {
                Assert.True(Math.Abs(e.Value.GetDouble() - sc[e.Name]) < Tol, $"{name}: score {u.Name}/{e.Name} {e.Value.GetDouble()} ≠ {sc[e.Name]}");
            }

            var list = Recommender.Rank(ctx, vec, cands, sc, 12, s);
            var want = u.Value.GetProperty("list").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal(want, list);
        }
    }
}
