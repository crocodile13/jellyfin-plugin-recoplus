using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Accord TMDb pour la bibliothèque : décroissance selon la place, poids du goût, atténuation des titres cités partout.</summary>
public class TmdbSignalTests
{
    private static IReadOnlyList<string> L(params string[] ids) => ids;

    [Fact]
    public void EmptyOrWeightlessInputGivesNothing()
    {
        Assert.Empty(TmdbSignal.Agreement(Array.Empty<(double, IReadOnlyList<string>)>()));
        Assert.Empty(TmdbSignal.Agreement(new[] { (0.0, L("a")), (-1.0, L("b")), (double.NaN, L("c")) }));
        Assert.Empty(TmdbSignal.Damping(Array.Empty<IReadOnlyList<string>>()));
    }

    [Fact]
    public void FirstPlaceFromTheOnlyAnchorIsFullAgreementAndPlaceDecays()
    {
        var r = TmdbSignal.Agreement(new[] { (2.0, L("a", "b", "c", "a")) });
        Assert.Equal(1.0, r["a"], 12);                       // doublon ignoré : la première place compte
        Assert.Equal(1 / Math.Sqrt(2), r["b"], 12);
        Assert.Equal(1 / Math.Sqrt(3), r["c"], 12);
        Assert.True(r["a"] > r["b"] && r["b"] > r["c"]);
    }

    [Fact]
    public void AgreementIsWeightedByTasteAndNormalised()
    {
        // « x » cité en tête par le titre le plus aimé (3), « y » en tête par le moins aimé (1)
        var r = TmdbSignal.Agreement(new[] { (3.0, L("x")), (1.0, L("y")), (-5.0, L("z")) });
        Assert.Equal(0.75, r["x"], 12);
        Assert.Equal(0.25, r["y"], 12);
        Assert.False(r.ContainsKey("z"));                     // un titre rejeté n'est pas une ancre
        // cité par tous les titres aimés en tête : accord plein, jamais au-delà de 1
        var all = TmdbSignal.Agreement(new[] { (3.0, L("x")), (1.0, L("x")) });
        Assert.Equal(1.0, all["x"], 12);
    }

    [Fact]
    public void TitlesRecommendedEverywhereAreDampedLikeFrequentWords()
    {
        var lists = new[] { L("hit", "a"), L("hit", "b"), L("hit", "c"), L("hit", "a", "a") };
        var d = TmdbSignal.Damping(lists);
        Assert.Equal(Math.Log(1 + 1.0) / Math.Log(5), d["hit"], 12);    // dans les 4 listes
        Assert.Equal(Math.Log(1 + 2.0) / Math.Log(5), d["a"], 12);      // 2 listes (doublon compté une fois)
        Assert.Equal(1.0, d["b"], 12);                                   // une seule liste : pas d'atténuation
        Assert.True(d["hit"] < d["a"] && d["a"] < d["b"]);

        var anchors = lists.Select(l => (1.0, l)).ToList();
        var r = TmdbSignal.Agreement(anchors, d);
        Assert.Equal(d["hit"], r["hit"], 12);                            // 1ʳᵉ place partout, puis atténué
        Assert.True(r["hit"] < 1.0);
        Assert.Equal(1 / Math.Sqrt(2) / 4, r["b"], 12);                  // un titre absent de l'atténuation garde 1
        Assert.All(r.Values, v => Assert.InRange(v, 0.0, 1.0));
    }
}
