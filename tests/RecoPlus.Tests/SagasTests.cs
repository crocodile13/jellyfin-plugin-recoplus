using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Sagas : collections TMDb, collections de Jellyfin mélangées, suites par le nom, règle dans les rangées.</summary>
public class SagasTests
{
    private const double Now = 1_790_000_000;

    private static Title M(string id, string name, int year, string lang = "en", string studio = "S", string? director = null,
        TitleKind kind = TitleKind.Movie, string[]? tags = null, string genre = "Animation", double addedDaysAgo = 300)
        => new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            Year = year,
            Language = lang,
            Studios = new[] { studio },
            Genres = new[] { genre },
            Tags = tags ?? new[] { "k" + id },
            People = director is null ? new Dictionary<string, IReadOnlyList<string>>() : new Dictionary<string, IReadOnlyList<string>> { ["Director"] = new[] { director } },
            Added = Now - (addedDaysAgo * Core.Day),
        };

    private static Dictionary<string, Title> Ts(params Title[] ts) => ts.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);

    [Fact]
    public void TmdbCollectionCatchesAVolumeJellyfinDidNotFile()
    {
        var t = Ts(M("ts1", "Toy Story", 1995, studio: "Pixar"), M("ts2", "Toy Story 2", 1999, studio: "Pixar"),
            M("ts5", "Toy Story 5", 2026, studio: "Pixar"));
        var box = new Dictionary<string, List<string>> { ["box"] = new() { "ts1", "ts2" } };   // Jellyfin n'a pas rangé le 5
        var r = Sagas.Refine(t, box, new Dictionary<string, int> { ["ts1"] = 10194, ["ts2"] = 10194, ["ts5"] = 10194 });
        var saga = Assert.Single(r.Collections);
        Assert.Equal("tmdbc:10194", saga.Key);
        Assert.Equal(new[] { "ts1", "ts2", "ts5" }, saga.Value);                          // ordre de sortie
        Assert.Empty(r.Thematic);
    }

    [Fact]
    public void BoxsetsMixingTmdbCollectionsAreThematicNotSagas()
    {
        var t = Ts(M("gg", "Les Gardiens de la Galaxie", 2014), M("gg2", "Les Gardiens de la Galaxie Vol. 2", 2017),
            M("xm", "X-Men : Days of Future Past", 2014), M("lo", "Logan", 2017),
            M("t1", "Les Tuche", 2011, "fr"), M("t2", "Les Tuche 2", 2016, "fr"), M("gs", "God Save the Tuche", 2023, "fr"));
        var box = new Dictionary<string, List<string>>
        {
            ["marvel"] = new() { "gg", "xm", "gg2", "lo" },     // plusieurs collections TMDb : mélange prouvé
            ["tuche"] = new() { "gs", "t2", "t1" },             // sans information TMDb : reste une saga
        };
        var r = Sagas.Refine(t, box, new Dictionary<string, int> { ["gg"] = 284433, ["gg2"] = 284433, ["xm"] = 748, ["lo"] = 453993 });
        Assert.Equal(new[] { "marvel" }, r.Thematic.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "gg", "gg2" }, r.Collections["tmdbc:284433"]);               // la vraie saga reste
        Assert.Equal(new[] { "t1", "t2", "gs" }, r.Collections.Values.Single(l => l.Contains("gs")));   // ordre de sortie

        var ds = Dataset(t, r);
        Assert.Equal(("tmdbc:284433", 2), (Recommender.Franchise(ds, "gg")!.Value.Id, Recommender.Franchise(ds, "gg")!.Value.Size));
        Assert.False(Sagas.IsSaga(ds, Recommender.Franchise(ds, "xm")!.Value, 12));       // seulement dans le mélange
    }

    [Fact]
    public void NameRuleLinksContinuationsButNotHomonyms()
    {
        var t = Ts(
            M("ov", "Overlord", 2015, "ja", "Madhouse", kind: TitleKind.Series),
            M("ovf", "Overlord : The Sacred Kingdom", 2024, "ja", "Madhouse"),
            M("hk", "Haikyu!!", 2014, "ja", "Production I.G", kind: TitleKind.Series),
            M("hkf", "HAIKYÛ !! La Guerre des Poubelles", 2024, "ja", "Production I.G"),
            M("it", "It", 2017, studio: "New Line"), M("itf", "It Follows", 2014, studio: "Northern"),     // trop court, autre studio
            M("du84", "Dune", 1984, studio: "Legendary", director: "Lynch"),
            M("du21", "Dune", 2021, studio: "Legendary", director: "Villeneuve"),
            M("du24", "Dune : Deuxième partie", 2024, studio: "Legendary", director: "Villeneuve"),
            M("alf", "Alien", 1979, "en", "Fox"), M("alj", "Alien Covenant", 2017, "fr", "Fox"));           // autre langue
        var r = Sagas.Refine(t, new Dictionary<string, List<string>>(), new Dictionary<string, int> { ["du21"] = 726871, ["du24"] = 726871 });
        List<string> SagaOf(string id) => r.Collections.Values.Single(l => l.Contains(id));
        Assert.Equal(new[] { "ov", "ovf" }, SagaOf("ov"));
        Assert.Equal(new[] { "hk", "hkf" }, SagaOf("hk"));
        Assert.Equal(new[] { "du21", "du24" }, SagaOf("du24"));                            // pas le Dune de 1984
        Assert.DoesNotContain(r.Collections.Values, l => l.Contains("du84") || l.Contains("it") || l.Contains("itf") || l.Contains("alj"));
    }

    [Fact]
    public void RankStartsAFilmSagaWithTheSeriesItContinuesAndDefersOrphanSequels()
    {
        var t = Ts(
            M("ov", "Overlord", 2015, "ja", "Madhouse", kind: TitleKind.Series),
            M("ovf", "Overlord : The Sacred Kingdom", 2024, "ja", "Madhouse"),
            M("hg", "Higurashi GOU", 2020, "ja", "Passione", tags: new[] { "sequel", "kx" }),
            M("a", "A", 2000), M("b", "B", 2001), M("c", "C", 2002));
        var ds = Dataset(t, Sagas.Refine(t, new Dictionary<string, List<string>>(), new Dictionary<string, int>()));
        var s = new EngineSettings { Saga = true, Mmr = 0 };
        var ctx = new Context(ds, Now, ds.Users, s);
        var vec = Core.BuildVectors(t, s);
        var cands = t.Keys.ToList();
        var sc = new Dictionary<string, double> { ["ovf"] = 9, ["hg"] = 8, ["a"] = 3, ["b"] = 2, ["c"] = 1, ["ov"] = 0.5 };
        var ranked = Recommender.Rank(ctx, vec, cands, sc, 10, s);
        Assert.Equal("ov", ranked[0]);                      // la série d'abord, pas le film qui la prolonge
        Assert.DoesNotContain("ovf", ranked);               // un seul volet par saga
        Assert.Equal("hg", ranked[^1]);                     // suite sans son début : en dernier
        Assert.True(Sagas.IsOrphanSequel(ds, "hg"));
    }

    [Fact]
    public void RowsShowOneVolumePerSagaAndNeverALaterOneFirst()
    {
        // saga de 4 films, aucun vu ; une nouveauté au milieu de la saga ; un titre aimé seulement « liké » (jamais vu)
        var t = Ts(
            M("h1", "Harry 1", 2001, studio: "WB", director: "Columbus"), M("h2", "Harry 2", 2002, studio: "WB"),
            M("h3", "Harry 3", 2004, studio: "WB", addedDaysAgo: 2), M("h4", "Harry 4", 2005, studio: "WB"),
            M("x1", "Seen One", 1999, studio: "WB"), M("lk", "Liked Only", 2003, studio: "WB"));
        for (int i = 0; i < 12; i++)
        {
            t["f" + i] = M("f" + i, "Film " + i, 1990 + i, studio: "WB", addedDaysAgo: i < 6 ? 3 : 300);
        }

        var box = new Dictionary<string, List<string>> { ["hp"] = new() { "h3", "h1", "h4", "h2" } };
        var ds = Dataset(t, Sagas.Refine(t, box, new Dictionary<string, int>()));
        ds.Users["u"]["x1"] = new Interaction { Title = "x1", Played = true, Completion = 1, Start = Now - (5 * Core.Day), Last = Now - (5 * Core.Day) };
        ds.Users["u"]["lk"] = new Interaction { Title = "lk", Rating = 1.0, Favorite = true };
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, ds.Users, s);
        var vec = Core.BuildVectors(t, s);
        var cands = t.Keys.Where(k => k is not "x1" and not "lk").ToList();
        // h4, h3, h2 mieux classés que h1 : sans règle, la rangée commencerait par le 4ᵉ volet
        var sc = cands.ToDictionary(k => k, k => k switch { "h4" => 10.0, "h3" => 9.5, "h2" => 9.0, "h1" => 0.1, _ => 5.0 - (cands.IndexOf(k) * 0.01) });
        var ordered = cands.OrderByDescending(k => sc[k]).ThenBy(k => k, StringComparer.Ordinal).ToList();
        var o = new RowOptions { Size = 5, Movies = true, New = true, NewDays = 30, Because = 3 };
        var r = HomeRows.Allocate(ctx, vec, "u", new List<string> { "f11" }, ordered, sc, o);

        foreach (var (row, ids) in r.Rows)
        {
            var hp = ids.Where(id => id.StartsWith('h')).ToList();
            Assert.True(hp.Count <= 1, row + " : " + string.Join(",", hp));             // un volet par saga
            Assert.True(hp.Count == 0 || hp[0] == "h1", row + " : " + string.Join(",", hp));
        }

        Assert.Single(r.Rows.Values, l => l.Contains("h1"));                              // le 1er volet, une fois
        Assert.DoesNotContain(r.Rows[HomeRows.Films], id => id is "h2" or "h3" or "h4");
        Assert.DoesNotContain("h3", r.Rows.GetValueOrDefault(HomeRows.Nouveautes) ?? new List<string>()); // nouveauté au milieu de la saga
        Assert.DoesNotContain("Liked Only", r.Anchors.Values);                           // jamais vu : pas « tu as aimé »
    }

    [Fact]
    public void SagaRowRuleCanBeTurnedOff()
    {
        var t = Ts(M("h1", "Harry 1", 2001), M("h2", "Harry 2", 2002), M("z", "Z", 2003));
        var ds = Dataset(t, Sagas.Refine(t, new Dictionary<string, List<string>> { ["hp"] = new() { "h1", "h2" } }, new Dictionary<string, int>()));
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, ds.Users, s);
        var vec = Core.BuildVectors(t, s);
        var sc = new Dictionary<string, double> { ["h2"] = 3, ["h1"] = 2, ["z"] = 1 };
        var ordered = new List<string> { "h2", "h1", "z" };
        var on = HomeRows.Allocate(ctx, vec, "u", new List<string>(), ordered, sc, new RowOptions { Size = 5, Movies = true });
        var off = HomeRows.Allocate(ctx, vec, "u", new List<string>(), ordered, sc, new RowOptions { Size = 5, Movies = true, Saga = false });
        Assert.Equal(new[] { "h1", "z" }, on.Rows[HomeRows.Films]);
        Assert.Equal(new[] { "h2", "h1", "z" }, off.Rows[HomeRows.Films]);
    }

    private static Dataset Dataset(Dictionary<string, Title> t, Sagas.Result r)
    {
        var ds = new Dataset
        {
            Titles = t,
            Users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal) { ["u"] = new(StringComparer.Ordinal) },
            Collections = r.Collections,
            Thematic = r.Thematic,
            Now = Now,
        };
        ds.LinkCollections();
        return ds;
    }
}
