using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class ExplorationTests
{
    [Fact]
    public void DailyPagesKeepPageOneAndDrawTheRestPerSeed()
    {
        var a = Exploration.DailyPages("u1|2026-10-20|genre:18", 3, 10);
        Assert.Equal(3, a.Length);
        Assert.Equal(1, a[0]);
        Assert.Equal(a.Distinct().Count(), a.Length);
        Assert.All(a, p => Assert.InRange(p, 1, 10));
        Assert.Equal(a, Exploration.DailyPages("u1|2026-10-20|genre:18", 3, 10));          // même jour : mêmes pages

        // d'un jour (ou d'un compte) à l'autre, les pages tirées changent au moins une fois sur quelques essais
        var days = Enumerable.Range(1, 10).Select(d => string.Join(",", Exploration.DailyPages($"u1|2026-11-{d:00}|genre:18", 3, 10))).Distinct().Count();
        Assert.True(days > 3);
        Assert.Equal(new[] { 1, 2, 3 }, Exploration.DailyPages("x", 5, 3));                  // profondeur atteinte
        Assert.Equal(new[] { 1 }, Exploration.DailyPages("x", 1, 10));
    }

    [Fact]
    public void GaussianIsReproducibleAndRoughlyStandard()
    {
        Assert.Equal(Exploration.Gaussian("a"), Exploration.Gaussian("a"));
        var xs = Enumerable.Range(0, 4000).Select(i => Exploration.Gaussian("k" + i)).ToList();
        double mean = xs.Average(), sd = Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / xs.Count);
        Assert.InRange(mean, -0.1, 0.1);
        Assert.InRange(sd, 0.9, 1.1);
        Assert.All(xs, x => Assert.True(double.IsFinite(x)));
    }

    [Fact]
    public void NoiseSwapsCloseTitlesOnlyAndZeroLevelChangesNothing()
    {
        var sc = new Dictionary<string, double> { ["a"] = 1.0, ["b"] = 0.99, ["c"] = 0.0, ["d"] = -1.0 };
        Assert.Equal(sc, Exploration.Noisy(sc, 0, "s"));
        int swaps = 0;
        for (int day = 0; day < 50; day++)
        {
            var n = Exploration.Noisy(sc, 0.3, "u|" + day);
            Assert.True(n["a"] > n["d"] && n["b"] > n["d"], "un titre bien moins bon ne passe pas devant");
            swaps += n["b"] > n["a"] ? 1 : 0;
        }

        Assert.InRange(swaps, 5, 45);                                                       // titres proches : ordre variable
        Assert.Equal(Exploration.Noisy(sc, 0.3, "u|1"), Exploration.Noisy(sc, 0.3, "u|1"));
    }

    [Fact]
    public void MergeIsBalancedWeightedAndDeduplicated()
    {
        var m = Exploration.Merge(new[]
        {
            new Exploration.Source("rel", new[] { "a", "b", "c", "d", "e", "f" }, 2),
            new Exploration.Source("top", new[] { "b", "x", "y" }, 1),
            new Exploration.Source("saga", Array.Empty<string>(), 1),
        }, 6);
        Assert.Equal(new[] { "a", "b", "x", "c", "d", "y" }, m.Select(p => p.Key));
        Assert.Equal("rel", m.First(p => p.Key == "b").Source);                            // première source qui l'apporte
        Assert.Equal(9, Exploration.Merge(new[] { new Exploration.Source("s", Enumerable.Range(0, 9).Select(i => "k" + i).ToList(), 1) }, 50).Count);
        Assert.Empty(Exploration.Merge(Array.Empty<Exploration.Source>(), 10));
    }

    [Fact]
    public void TopKeepsRecurringItemsWeightedByTaste()
    {
        var liked = new (IEnumerable<int>, double)[]
        {
            (new[] { 1, 2, 3 }, 1.0),
            (new[] { 1, 2, 2 }, 0.5),          // doublon dans un titre : compté une fois
            (new[] { 1, 4 }, 2.0),
        };
        var top = Exploration.Top(liked, 2, 10);
        Assert.Equal(new[] { 1, 2 }, top.Select(t => t.Item));
        Assert.Equal(3.5, top[0].Weight, 9);
        Assert.Equal(new[] { 1, 4 }, Exploration.Top(liked, 1, 2).Select(t => t.Item));      // 3,5 puis 2,0 (1,5 et 1,0 écartés)
    }

    [Fact]
    public void PairsKeepTheStrongestCrossings()
    {
        var g = new (int, double)[] { (18, 3.0), (16, 2.0), (35, 0.5) };
        var k = new (string, double)[] { ("ja", 2.0), ("ko", 1.0), ("xx", 0.0) };
        var p = Exploration.Pairs(g, k, 3);
        Assert.Equal(new[] { (18, "ja"), (16, "ja"), (18, "ko") }, p.Select(x => (x.A, x.B)));
        Assert.Equal(6.0, p[0].Weight, 9);
        Assert.DoesNotContain(Exploration.Pairs(g, k, 100), x => x.B == "xx");               // poids nul : écarté
        Assert.Empty(Exploration.Pairs(g, Array.Empty<(string, double)>(), 5));
        Assert.Equal(2, Exploration.Pairs(new (int, double)[] { (1, 1), (2, 1) }, new (int, double)[] { (9, 1) }, 5).Count);
        Assert.Equal(1, Exploration.Pairs(new (int, double)[] { (1, 1), (2, 1) }, new (int, double)[] { (9, 1) }, 5)[0].A); // égalité : ordre des listes
    }

    [Fact]
    public void StatsCountEachStepPerSource()
    {
        var st = Exploration.Stats(
            new Dictionary<string, int> { ["related"] = 40, ["gems"] = 12 },
            new[] { "related", "related", "gems", "saga" },
            new[] { "related", "saga" },
            new[] { "saga" });
        Assert.Equal(40, st["related"].Found);
        Assert.Equal(2, st["related"].Detailed);
        Assert.Equal(1, st["related"].Proposed);
        Assert.Equal(0, st["related"].Placed);
        Assert.Equal(1, st["saga"].Placed);
        Assert.Equal(0, st["saga"].Found);
        Assert.Equal(0, st["gems"].Proposed);
        Assert.Null(Exploration.Stats(new Dictionary<string, int> { ["top"] = 3 }, Array.Empty<string>(), Array.Empty<string>(), null)["top"].Placed);
    }

    [Fact]
    public void ConfidentRatingDistrustsFewVotes()
    {
        Assert.InRange(Exploration.ConfidentRating(8.8, 4), 7.0, 7.05);                       // CHOPPER's : 4 votes
        Assert.InRange(Exploration.ConfidentRating(8.5, 20000), 8.45, 8.5);                   // classique reconnu
        Assert.True(Exploration.ConfidentRating(8.0, 3000) > Exploration.ConfidentRating(9.2, 10));
        Assert.Equal(7.0, Exploration.ConfidentRating(null, 100));
        Assert.Equal(7.0, Exploration.ConfidentRating(9.0, null));
        Assert.Equal(7.0, Exploration.ConfidentRating(9.0, 0));
    }

    [Fact]
    public void GenresMapBetweenMoviesAndSeries()
    {
        Assert.Equal(new[] { 16 }, Exploration.GenreFor(16, "tv"));
        Assert.Equal(new[] { 10765 }, Exploration.GenreFor(878, "tv"));
        Assert.Equal(new[] { 878, 14 }, Exploration.GenreFor(10765, "movie"));
        Assert.Empty(Exploration.GenreFor(27, "tv"));                                         // horreur : pas de genre série
        Assert.Equal(new[] { 27 }, Exploration.GenreFor(27, "movie"));
    }

    [Fact]
    public void ParentalFollowsJellyfinRules()
    {
        Assert.True(Parental.IsAllowed(null, null, false, 10, null));       // non classé : autorisé…
        Assert.False(Parental.IsAllowed(null, null, true, null, null));     // …sauf si le compte bloque les non classés
        Assert.True(Parental.IsAllowed(16, null, false, null, null));       // pas de limite
        Assert.True(Parental.IsAllowed(12, null, false, 12, null));
        Assert.False(Parental.IsAllowed(16, null, false, 12, null));
        Assert.False(Parental.IsAllowed(6, 1, false, 12, 0));               // deux plafonds, pas un ordre lexicographique
        Assert.True(Parental.IsAllowed(12, 0, false, 12, 0));
        Assert.True(Parental.TagsAllowed(new[] { "a" }, new[] { "Drame" }, Array.Empty<string>(), Array.Empty<string>()));
        Assert.False(Parental.TagsAllowed(new[] { "gore" }, new[] { "Drame" }, new[] { "GORE" }, Array.Empty<string>()));
        Assert.False(Parental.TagsAllowed(new[] { "x" }, new[] { "Horreur" }, new[] { "horreur" }, Array.Empty<string>()));
        Assert.False(Parental.TagsAllowed(new[] { "x" }, new[] { "Drame" }, Array.Empty<string>(), new[] { "kids" }));
        Assert.True(Parental.TagsAllowed(new[] { "Kids" }, new[] { "Drame" }, Array.Empty<string>(), new[] { "kids" }));
        Assert.False(Parental.TagsAllowed(new[] { "kids", "gore" }, Array.Empty<string>(), new[] { "gore" }, new[] { "kids" })); // bloqué l'emporte
    }
}
