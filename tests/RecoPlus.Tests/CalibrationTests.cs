using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class CalibrationTests
{
    private static Dataset Golden() => ParityTests.LoadDataset(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement.GetProperty("dataset"));

    [Fact]
    public void IndexedScoreEqualsDirectScore()
    {
        var ds = Golden();
        foreach (var s in new[] { new EngineSettings(), new EngineSettings { WNegative = 1.7, FeatLanguage = 0.6, Weighting = WeightingScheme.Bm25 } })
        {
            var vec = Core.BuildVectors(ds.Titles, s);
            var sim = new SimilarityIndex(vec);
            var ctx = new Context(ds, ds.Now, ds.Users, s);
            foreach (var u in ds.Users.Keys)
            {
                var cands = ds.Titles.Keys.Where(k => !ds.Users[u].ContainsKey(k)).ToList();
                var a = Recommender.Score(ctx, vec, u, cands, s);
                var b = Recommender.Score(ctx, vec, u, cands, s, sim: sim);
                Assert.True(cands.All(c => Math.Abs(a[c] - b[c]) < 1e-12), "écart pour " + u);
            }
        }
    }

    [Fact]
    public void IndexWithinBudgetFallsBackToDirect()
    {
        var ds = Golden();
        var s = new EngineSettings();
        var vec = Core.BuildVectors(ds.Titles, s);
        var tiny = new SimilarityIndex(vec, maxCells: vec.Count * 2L);   // deux lignes au plus
        var ctx = new Context(ds, ds.Now, ds.Users, s);
        var u = ds.Users.Keys.First(k => ds.Users[k].Count > 5);
        var cands = ds.Titles.Keys.Where(k => !ds.Users[u].ContainsKey(k)).ToList();
        var a = Recommender.Score(ctx, vec, u, cands, s);
        var b = Recommender.Score(ctx, vec, u, cands, s, sim: tiny);
        Assert.True(cands.All(c => Math.Abs(a[c] - b[c]) < 1e-12));
    }

    [Fact]
    public void CasesAreWeightedByHistoryDepthAndBalancedAcrossAccounts()
    {
        var cases = Calibrator.Cases(Golden());
        Assert.True(cases.Count > 0);
        Assert.True(cases.All(c => c.Prior >= Calibrator.MinPrior));
        Assert.Equal(1.0, cases.Average(c => c.Weight), 9);
        // à compte égal, un cas avec plus d'historique pèse plus
        foreach (var g in cases.GroupBy(c => c.Ev.User))
        {
            var o = g.OrderBy(c => c.Prior).ToList();
            for (int i = 1; i < o.Count; i++)
            {
                Assert.True(o[i].Weight >= o[i - 1].Weight - 1e-12);
            }
        }
    }

    [Fact]
    public void RankGainRewardsHigherPlacementsAndIsBounded()
    {
        var ds = Golden();
        var s = new EngineSettings();
        var sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, s));
        var cases = Calibrator.Cases(ds);
        var r = Calibrator.EvaluateRanks(ds, sim, cases, s);
        Assert.Equal(cases.Count, r.PerEventGain.Length);
        Assert.True(r.PerEventGain.All(g => g > 0 && g <= 1));
        for (int i = 0; i < cases.Count; i++)
        {
            Assert.True(!r.PerEvent10[i] || r.PerEventGain[i] >= 1.0 / Math.Log2(11) - 1e-12, "top 10 ⇒ gain ≥ 1/log2(11)");
            Assert.True(r.PerEvent10[i] || r.PerEventGain[i] < 1.0 / Math.Log2(11) + 1e-12);
        }
    }

    [Fact]
    public void BootstrapDeltaOfIdenticalIsZero()
    {
        var cases = Calibrator.Cases(Golden());
        var g = Enumerable.Repeat(0.3, cases.Count).ToArray();
        Assert.Equal((0.0, 0.0), Calibrator.BootstrapDelta(cases, g, g));
    }
}
