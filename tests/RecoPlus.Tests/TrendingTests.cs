using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Listes TMDb du moment (tendances, populaires, à venir), rangées séparées films / séries.</summary>
public class TrendingTests
{
    private const double Now = 1_790_000_000;

    [Fact]
    public void ScoresDecreaseContinuouslyAndKeepTheStrongestPlace()
    {
        var s = Trending.Scores(new[] { "a", "b", "c", "d" }, new[] { "x", "c" });
        Assert.Equal(1.0, s["a"], 9);
        Assert.Equal(1.0, s["x"], 9);                       // chaque liste a son premier
        Assert.Equal(0.5, s["d"], 9);                       // 4e : 1/√4
        Assert.Equal(1 / Math.Sqrt(2), s["c"], 9);          // 3e d'une liste, 2e de l'autre : la plus forte place
        Assert.True(s["a"] > s["b"] && s["b"] > s["d"]);
    }

    [Fact]
    public void ScoresIgnoreRepeatsInAList()
    {
        var s = Trending.Scores(new[] { "a", "a", "b" });
        Assert.Equal(1 / Math.Sqrt(2), s["b"], 9);          // le doublon ne prend pas de place
    }

    [Fact]
    public void OrderGoesFromTmdbOrderToTasteContinuously()
    {
        var ids = new[] { "a", "b", "c" };
        var trend = Trending.Scores(ids);
        var taste = new Dictionary<string, double> { ["a"] = 0.1, ["b"] = 0.5, ["c"] = 0.9 };
        Assert.Equal(ids, Trending.Order(ids, trend, taste, 0));
        Assert.Equal(new[] { "c", "b", "a" }, Trending.Order(ids, trend, taste, 1));
        // goût absent = le plus bas ; part hors bornes ramenée à 0..1 ; NaN = ordre de TMDb
        Assert.Equal(new[] { "c", "a", "b" }, Trending.Order(ids, trend, new Dictionary<string, double> { ["b"] = 1, ["c"] = 2 }, 1));   // à égalité, la tendance
        Assert.Equal(new[] { "c", "b", "a" }, Trending.Order(ids, trend, taste, 7));
        Assert.Equal(ids, Trending.Order(ids, trend, taste, double.NaN));
    }

    [Fact]
    public void AllowsFollowsTheKind()
    {
        Assert.True(Trending.Allows(KindFilter.Both, TitleKind.Movie));
        Assert.True(Trending.Allows(KindFilter.Split, TitleKind.Series));
        Assert.False(Trending.Allows(KindFilter.Movies, TitleKind.Series));
        Assert.True(Trending.Allows(KindFilter.Series, TitleKind.Series));
    }

    [Fact]
    public void RowIdsSplitIntoBaseAndKind()
    {
        Assert.Equal(("Tendances", (KindFilter?)KindFilter.Movies), HomeRows.SplitOf("TendancesFilms"));
        Assert.Equal(("ParceQue2", (KindFilter?)KindFilter.Series), HomeRows.SplitOf("ParceQue2Series"));
        Assert.Equal(("Films", (KindFilter?)KindFilter.Movies), HomeRows.SplitOf("Films"));
        Assert.Equal(("Series", (KindFilter?)KindFilter.Series), HomeRows.SplitOf("Series"));
        Assert.Equal(("PourToi", (KindFilter?)null), HomeRows.SplitOf("PourToi"));
        Assert.Equal("ParceQue", HomeRows.KindOfRow("ParceQue3Films"));
        Assert.Equal("AVenir", HomeRows.KindOfRow("AVenirSeries"));
        Assert.Equal(new[] { "NouveautesFilms", "NouveautesSeries" }, HomeRows.Expand("Nouveautes", KindFilter.Split));
        Assert.Equal(new[] { "Nouveautes" }, HomeRows.Expand("Nouveautes", KindFilter.Movies));

        var o = new RowOptions { Kinds = new() { ["ParceQue"] = KindFilter.Split, ["PourToi"] = KindFilter.Series } };
        Assert.Equal(KindFilter.Movies, o.KindOf("ParceQue1Films"));
        Assert.Equal(KindFilter.Both, o.KindOf("ParceQue1"));        // rangée réunie d'un ancien réglage : tout
        Assert.Equal(KindFilter.Series, o.KindOf("PourToi"));
        Assert.Equal(KindFilter.Both, o.KindOf("Tendances"));
    }

    [Fact]
    public void BaseRowsFollowTheAllocationOrderAndListRowsNeedTheLists()
    {
        Assert.Equal(new[] { "PourToi", "Tendances", "Populaires", "AVenir", "Nouveautes", "ParceQue1", "ParceQue2", "ParceQue3", "Films", "Series" }, HomeRows.BaseRows(true));
        Assert.Equal(new[] { "PourToi", "Nouveautes", "ParceQue1", "ParceQue2", "ParceQue3", "Films", "Series" }, HomeRows.BaseRows(false));
        Assert.Equal(HomeRows.BaseRows(true).Select(HomeRows.KindOfRow).Distinct().OrderBy(x => x, StringComparer.Ordinal), HomeRows.SourceRows.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void SplitLayoutReplacesInPlaceAndRespectsChoices()
    {
        const string P = "RecoPlus";
        var l = new List<string> { "A", P + "PourToi", "B" };
        Assert.True(SplitLayout.Apply(l, P, "PourToi", KindFilter.Split));
        Assert.Equal(new[] { "A", P + "PourToiFilms", P + "PourToiSeries", "B" }, l);

        // réunie : les deux moitiés laissent place à la rangée, à la place de la première
        Assert.True(SplitLayout.Apply(l, P, "PourToi", KindFilter.Both));
        Assert.Equal(new[] { "A", P + "PourToi", "B" }, l);

        // une seule moitié gardée par le compte : son choix
        var half = new List<string> { P + "TendancesSeries" };
        Assert.False(SplitLayout.Apply(half, P, "Tendances", KindFilter.Split));
        Assert.Equal(new[] { P + "TendancesSeries" }, half);

        // rangée absente : rien n'est ajouté
        var none = new List<string> { "A" };
        Assert.False(SplitLayout.Apply(none, P, "Tendances", KindFilter.Split));
        Assert.Equal(new[] { "A" }, none);

        // forme à jour et forme périmée ensemble : la périmée part
        var both = new List<string> { P + "Nouveautes", P + "NouveautesFilms" };
        Assert.True(SplitLayout.Apply(both, P, "Nouveautes", KindFilter.Split));
        Assert.Equal(new[] { P + "NouveautesFilms" }, both);

        // une autre rangée au nom proche n'est pas touchée
        var other = new List<string> { P + "ParceQue1", P + "ParceQue2" };
        Assert.True(SplitLayout.Apply(other, P, "ParceQue1", KindFilter.Split));
        Assert.Equal(new[] { P + "ParceQue1Films", P + "ParceQue1Series", P + "ParceQue2" }, other);
    }

    private static (Context Ctx, Dictionary<string, SparseVector> Vec, List<string> Ordered, Dictionary<string, double> Sc) Setup()
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        for (int i = 0; i < 60; i++)
        {
            var id = $"t{i:00}";
            titles[id] = new Title
            {
                Id = id, Name = id, Kind = i % 2 == 0 ? TitleKind.Movie : TitleKind.Series,
                Genres = new[] { i < 30 ? "Drame" : "Comédie" }, Tags = new[] { $"k{i % 5}" }, Added = Now - (300 * Core.Day),
            };
        }

        Interaction L(string id) => new() { Title = id, Played = true, Completion = 1, Start = Now - (2 * Core.Day), Last = Now - Core.Day };
        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal)
        {
            ["u"] = new[] { L("t00"), L("t01") }.ToDictionary(x => x.Title, x => x, StringComparer.Ordinal),
        };
        var ds = new Dataset { Titles = titles, Users = users, Now = Now };
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, users, s);
        var vec = Core.BuildVectors(titles, s);
        var cands = titles.Keys.Where(k => !users["u"].ContainsKey(k)).ToList();
        var sc = Recommender.Score(ctx, vec, "u", cands, s);
        return (ctx, vec, cands.OrderByDescending(c => sc[c]).ThenBy(c => c, StringComparer.Ordinal).ToList(), sc);
    }

    [Fact]
    public void ListRowsComeRightAfterTheMainRowAndSplitRowsKeepTheirKind()
    {
        var (ctx, vec, ordered, sc) = Setup();
        var trending = new[] { "t59", "t58", "t57", "t56", "t55", "t54", "t53", "t52", "t02", "t03" };
        var o = new RowOptions
        {
            Size = 5, Movies = true, Series = true,
            Lists = new() { ["Tendances"] = trending },
            Kinds = new() { ["Tendances"] = KindFilter.Split, ["PourToi"] = KindFilter.Movies },
        };
        var r = HomeRows.Allocate(ctx, vec, "u", ordered.Take(11).ToList(), ordered, sc, o);

        Assert.All(r.Rows["PourToi"], id => Assert.Equal(TitleKind.Movie, ctx.Ds.Titles[id].Kind));
        Assert.False(r.Rows.ContainsKey("Tendances"));
        Assert.All(r.Rows["TendancesFilms"], id => Assert.Equal(TitleKind.Movie, ctx.Ds.Titles[id].Kind));
        Assert.All(r.Rows["TendancesSeries"], id => Assert.Equal(TitleKind.Series, ctx.Ds.Titles[id].Kind));
        // ordre de la liste gardé, sans ce que « Pour toi » montre déjà
        var films = trending.Where(id => ctx.Ds.Titles[id].Kind == TitleKind.Movie && !r.Rows["PourToi"].Contains(id)).ToList();
        Assert.Equal(films, r.Rows["TendancesFilms"]);
        var all = r.Rows.Values.SelectMany(x => x).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void HiddenSplitHalvesAreNotFilledAndListTitlesOutsideCandidatesAreDropped()
    {
        var (ctx, vec, ordered, sc) = Setup();
        var o = new RowOptions
        {
            Size = 5,
            Lists = new() { ["Populaires"] = new[] { "t00", "t40", "t41", "t42", "tmdb:movie:9" } },
            Kinds = new() { ["Populaires"] = KindFilter.Split },
            Shown = new HashSet<string> { "PourToi", "PopulairesSeries" },
        };
        var r = HomeRows.Allocate(ctx, vec, "u", ordered.Take(5).ToList(), ordered, sc, o);
        Assert.False(r.Rows.ContainsKey("PopulairesFilms"));
        Assert.DoesNotContain("t00", r.Rows["PopulairesSeries"]);           // déjà vu : pas candidat
        Assert.DoesNotContain("tmdb:movie:9", r.Rows["PopulairesSeries"]);
    }

    [Fact]
    public void TrendingRaisesPopularityOnlyWhenPresent()
    {
        var (ctx, vec, ordered, _) = Setup();
        var s = new EngineSettings { WPopularity = 1, GateTop = 500 };
        var cands = ordered.ToList();
        var baseSc = Recommender.Score(ctx, vec, "u", cands, s);
        var ds2 = new Dataset
        {
            Titles = ctx.Ds.Titles, Users = ctx.Ds.Users, Now = Now,
            Trending = new Dictionary<string, double>(StringComparer.Ordinal) { ["t40"] = 1.0 },
        };
        var ctx2 = new Context(ds2, Now, ds2.Users, s);
        var trendSc = Recommender.Score(ctx2, vec, "u", cands, s);
        Assert.True(trendSc["t40"] > baseSc["t40"], "titre en tendance remonté");
        foreach (var id in cands.Where(id => id != "t40"))
        {
            Assert.Equal(baseSc[id], trendSc[id], 9);                       // les autres : exactement comme avant
        }

        var off = new EngineSettings { WPopularity = 1, GateTop = 500, TrendingShare = 0 };
        var ctx3 = new Context(ds2, Now, ds2.Users, off);
        Assert.Equal(Recommender.Score(ctx, vec, "u", cands, off)["t40"], Recommender.Score(ctx3, vec, "u", cands, off)["t40"], 9);
    }
}
