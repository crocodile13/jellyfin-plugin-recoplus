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
    public void SnapshotSeesNothingFromTheFuture()
    {
        Title T(string id, TitleKind k) => new() { Id = id, Kind = k, Name = id, Genres = new[] { "g" }, Tags = Array.Empty<string>() };
        var titles = new[] { T("m", TitleKind.Movie), T("s", TitleKind.Series), T("r", TitleKind.Movie), T("f", TitleKind.Movie) }.ToDictionary(t => t.Id);
        var h = new System.Collections.Generic.Dictionary<string, Interaction>
        {
            // film commencé à 100, fini à 300 (favori, revu)
            ["m"] = new() { Title = "m", Start = 100, Last = 300, Played = true, Completion = 1, Rewatch = 1, Favorite = true },
            // demandé à 150, jamais regardé
            ["r"] = new() { Title = "r", Requested = 150 },
            // demandé à 400 et regardé à 450
            ["f"] = new() { Title = "f", Requested = 400, Start = 450, Last = 460, Played = true, Completion = 1 },
            ["s"] = new() { Title = "s", Start = 50, Last = 90, Episodes = 4 },
        };
        var ds = new Dataset { Titles = titles, Users = new() { ["u"] = h }, Now = 1000 };

        var at200 = Calibrator.Snapshot(ds, 200)["u"];
        Assert.False(at200["m"].Played);                    // en cours à 200
        Assert.Equal(200, at200["m"].Last);
        Assert.Equal(0, at200["m"].Rewatch);
        Assert.Equal(150, at200["r"].Requested);           // la demande existe déjà
        Assert.False(at200.ContainsKey("f"));              // ni demandé ni commencé à 200
        Assert.Same(h["s"], at200["s"]);                   // rien de postérieur : tel quel
        Assert.True(h["m"].Played);                         // l'historique d'origine n'est pas touché

        var at420 = Calibrator.Snapshot(ds, 420)["u"];
        Assert.Equal(400, at420["f"].Requested);           // demandé, pas encore regardé
        Assert.False(at420["f"].Played);
        Assert.Equal(0, at420["f"].Start);
        var at100 = Calibrator.Snapshot(ds, 120)["u"];
        Assert.False(at100.ContainsKey("r"));
        Assert.True(at100.ContainsKey("m"));
    }

    [Fact]
    public void TitlesWantedBeforeBeingWatchedAreNotRankingCases()
    {
        var ds = Golden();
        var cases = Calibrator.Cases(ds);
        Assert.NotEmpty(cases);
        var first = cases[0].Ev;
        ds.Users[first.User][first.Target].Requested = first.T0 - 10;
        var after = Calibrator.Cases(ds);
        Assert.DoesNotContain(after, c => c.Ev.User == first.User && c.Ev.Target == first.Target);
        Assert.DoesNotContain(Calibrator.Events(ds), e => e.User == first.User && e.Target == first.Target);
    }

    [Fact]
    public void CachedContextsGiveTheSameEvaluation()
    {
        var ds = Golden();
        var cases = Calibrator.Cases(ds);
        var cache = new Calibrator.ContextCache();
        var s0 = new EngineSettings();
        var sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, s0));
        // poids du score (contexte gardé), puis un réglage de signal (contexte refait), puis retour
        foreach (var s in new[] { s0, new EngineSettings { WMaxSim = 0.7, WFresh = 0.5, Mmr = 0.9 }, new EngineSettings { HalfLifeDays = 30, AbandonDays = 5 }, s0 })
        {
            var a = Calibrator.EvaluateRanks(ds, sim, cases, s);
            var b = Calibrator.EvaluateRanks(ds, sim, cases, s, contexts: cache);
            Assert.Equal(a.PerEventGain, b.PerEventGain);
        }
    }

    [Fact]
    public void ContextForOneAccountScoresLikeTheFullOneAndIsSmaller()
    {
        // calibrage : un contexte par cas, réduit à ce que lit le score du compte du cas (mêmes scores, bien moins de mémoire)
        var ds = Golden();
        var s = new EngineSettings { WCollab = 1.0 };
        var vec = Core.BuildVectors(ds.Titles, s);
        var hist = Calibrator.Snapshot(ds, ds.Now);
        var full = new Context(ds, ds.Now, hist, s);
        foreach (var u in ds.Users.Keys)
        {
            var mine = new Context(ds, ds.Now, hist, s, u);
            var cands = Calibrator.Candidates(ds, hist[u], ds.Now);
            Assert.Equal(Recommender.Score(full, vec, u, cands, s), Recommender.Score(mine, vec, u, cands, s));
            Assert.True(mine.Cooc.Values.Sum(r => r.Count) <= full.Cooc.Values.Sum(r => r.Count));
        }

        Assert.Contains(ds.Users.Keys, u => new Context(ds, ds.Now, hist, s, u).Cooc.Values.Sum(r => r.Count) < full.Cooc.Values.Sum(r => r.Count));
    }

    [Fact]
    public void CalibrationCachesStayWithinTheirBudgetWithTheSameResults()
    {
        var ds = Golden();
        var cases = Calibrator.Cases(ds);
        var s = new EngineSettings();
        var sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, s));
        var one = Calibrator.ContextCache.CostOf(new Context(ds, cases[0].Ev.T0 - 1, Calibrator.Snapshot(ds, cases[0].Ev.T0 - 1), s, cases[0].Ev.User), new());
        var small = new Calibrator.ContextCache { Budget = one * 3 };
        var a = Calibrator.EvaluateRanks(ds, sim, cases, s);
        var b = Calibrator.EvaluateRanks(ds, sim, cases, s, contexts: small);
        var c = Calibrator.EvaluateRanks(ds, sim, cases, s, contexts: small);   // cas gardés relus, les autres refaits
        Assert.Equal(a.PerEventGain, b.PerEventGain);
        Assert.Equal(a.PerEventGain, c.PerEventGain);
        Assert.InRange(small.Cost, 1, small.Budget);

        // modèle de choix : caractéristiques gardées dans le budget, mêmes poids appris qu'avec un cache sans limite
        var big = new ChoiceModel.FeatureCache(s);
        var tiny = new ChoiceModel.FeatureCache(s) { Budget = 5_000 };
        var x = ChoiceModel.Learn(ds, sim, cases, s, 1.0, cache: big);
        var y = ChoiceModel.Learn(ds, sim, cases, s, 1.0, cache: tiny);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(x), System.Text.Json.JsonSerializer.Serialize(y));
        Assert.True(big.Values > tiny.Budget, "le cache sans limite garde plus que le petit budget");
        Assert.InRange(tiny.Values, 0, tiny.Budget);
    }

    [Fact]
    public void FoldsSpreadAccountsAndCvPicksTheBestValue()
    {
        var ds = Golden();
        var cases = Calibrator.Cases(ds);
        int k = Calibrator.FoldCount(cases);
        var f = Calibrator.Folds(cases, k);
        Assert.All(f.Values, v => Assert.InRange(v, 0, k - 1));
        Assert.Equal(cases.Select(c => c.Ev.User).Distinct().Count(), f.Count);
        // valeur qui fait gagner partout : choisie ; sans différence : la dernière (la plus prudente)
        Assert.Equal(2, Calibrator.SelectByCv(cases, new[] { 1, 2, 3 }, (tr, te, v) => v == 2 ? 1.0 : 0.5));
        Assert.Equal(3, Calibrator.SelectByCv(cases, new[] { 1, 2, 3 }, (tr, te, v) => 0.5));
    }

    [Fact]
    public void CalibrationOptionsHaveAutomaticDefaultsAndAreHonoured()
    {
        var ds = Golden();
        var cases = Calibrator.Cases(ds);
        var auto = new CalibrationOptions();
        Assert.Equal(Calibrator.FoldCount(cases), auto.FoldsFor(cases));
        Assert.Equal(Calibrator.SlowFoldCount(auto.FoldsFor(cases)), auto.SlowFoldsFor(cases));
        // fonctions continues (arrondies), pas de paliers
        Assert.Equal(new[] { 2, 2, 2, 3, 4, 5, 7, 10, 10 }, new[] { 1, 3, 4, 9, 16, 25, 50, 100, 400 }.Select(Calibrator.FoldCount).ToArray());
        Assert.Equal(new[] { 2, 2, 3, 3, 4 }, new[] { 2, 3, 4, 5, 10 }.Select(Calibrator.SlowFoldCount).ToArray());
        Assert.Equal(2, auto.InnerFoldsEff);
        Assert.Equal(1, auto.SeMultiplier);
        Assert.Equal((2, 4), (auto.PassesEff, auto.RefineEff));
        Assert.Equal(Calibrator.MinPracticalGain, auto.MinGain);
        var accounts = cases.Select(c => c.Ev.User).Distinct().Count();
        Assert.Equal(Math.Min(10, accounts), new CalibrationOptions { Folds = 50 }.FoldsFor(cases));   // jamais plus que les comptes
        Assert.Equal(0, new CalibrationOptions { Prudence = CalibrationPrudence.Best }.SeMultiplier);

        var rep = Calibrator.Calibrate(ds, new EngineSettings(), method: CalibrationMethod.ChoiceModel, options: new CalibrationOptions { Folds = 2, MinGainPercent = 5 });
        if (rep.Methods.Count > 0)
        {
            Assert.Equal(2, rep.Folds);
            Assert.Equal(0.05, rep.MinGain, 9);
        }
    }

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
        Assert.Contains(cases, c => !c.Adopted);                                // commencés sans être adoptés : demi-poids
        foreach (var g in cases.GroupBy(c => (c.Ev.User, c.Adopted)))
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
    public void HonestSelectionDoesNotRewardTheLuckiestOfNoisyMethods()
    {
        var cases = Calibrator.Cases(Golden());
        int n = cases.Count;
        var rng = new Random(7);
        var baseG = Enumerable.Range(0, n).Select(_ => 0.3).ToArray();
        // trois méthodes de quatre étapes, chacune du pur bruit autour de l'actuel
        var paths = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 5)
            .Select(step => step == 0 ? (double[])baseG.Clone() : baseG.Select(b => b + (0.2 * (rng.NextDouble() - 0.5))).ToArray()).ToArray()).ToList();
        double naive = paths.SelectMany(p => p.Skip(1)).Max(o => o.Zip(baseG, (a, b) => a - b).Average());
        var h = Calibrator.HonestSelection(cases, baseG, paths, 3, 1.0);
        Assert.True(h.Delta <= naive + 1e-12, $"honnête {h.Delta} ≤ meilleur tirage {naive}");
        Assert.True(h.Ci.Low <= 0, "du bruit ne prouve rien");

        // une méthode qui gagne vraiment partout : retrouvée
        var real = new[] { (double[])baseG.Clone(), baseG.Select(b => b + 0.05).ToArray() };
        var g = Calibrator.HonestSelection(cases, baseG, paths.Append(real).ToList(), 3, 1.0);
        Assert.True(g.Delta > 0.03 && g.Ci.Low > 0, $"gain réel retrouvé : {g.Delta} [{g.Ci.Low} ; {g.Ci.High}]");
    }

    [Fact]
    public void BootstrapDeltaOfIdenticalIsZero()
    {
        var cases = Calibrator.Cases(Golden());
        var g = Enumerable.Repeat(0.3, cases.Count).ToArray();
        Assert.Equal((0.0, 0.0), Calibrator.BootstrapDelta(cases, g, g));
    }
}
