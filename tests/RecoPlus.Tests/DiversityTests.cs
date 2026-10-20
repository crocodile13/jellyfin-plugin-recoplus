using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>1.0.5 : goût du moment, affichages sans ouverture, diversité personnalisée et calibrée, méthodes de calibrage.</summary>
public class DiversityTests
{
    private const double Now = 1_790_000_000;

    // bibliothèque : 4 « mondes » (genres + mots-clés propres), 30 titres chacun
    private static Dataset World(Dictionary<string, Dictionary<string, Interaction>> users)
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        string[] genres = { "Horreur", "Comédie", "Animation", "Documentaire" };
        for (int g = 0; g < genres.Length; g++)
        {
            for (int i = 0; i < 30; i++)
            {
                var id = $"{g}{i:00}";
                titles[id] = new Title
                {
                    Id = id,
                    Kind = TitleKind.Movie,
                    Name = id,
                    Genres = new[] { genres[g] },
                    Tags = new[] { $"k{g}a", $"k{g}b", $"t{g}{i % 3}" },
                    Added = Now - (400 * Core.Day),
                };
            }
        }

        return new Dataset { Titles = titles, Users = users, Now = Now };
    }

    private static Interaction Liked(string id, double daysAgo) => new() { Title = id, Played = true, Completion = 1, Start = Now - ((daysAgo + 1) * Core.Day), Last = Now - (daysAgo * Core.Day) };

    private static (Dataset Ds, Context Ctx, Dictionary<string, SparseVector> Vec, List<string> Cands) Setup(EngineSettings s, params Interaction[] h)
    {
        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal)
        {
            ["u"] = h.ToDictionary(x => x.Title, x => x, StringComparer.Ordinal),
        };
        var ds = World(users);
        var ctx = new Context(ds, Now, users, s);
        var vec = Core.BuildVectors(ds.Titles, s);
        var cands = ds.Titles.Keys.Where(k => !users["u"].ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return (ds, ctx, vec, cands);
    }

    [Fact]
    public void ShortTermFavoursTheCurrentStreak()
    {
        // aimé autrefois : beaucoup d'horreur ; ces derniers jours : de l'animation
        var h = Enumerable.Range(0, 6).Select(i => Liked($"0{i:00}", 200 + i))
            .Concat(Enumerable.Range(0, 2).Select(i => Liked($"2{i:00}", 1 + i))).ToArray();
        var off = new EngineSettings { RecencyFloor = 1 };     // même poids pour l'ancien et le récent : seul le goût du moment les distingue
        var on = new EngineSettings { RecencyFloor = 1, WShortTerm = 2, ShortTermTitles = 2 };
        var (_, ctxOff, vec, cands) = Setup(off, h);
        var (_, ctxOn, _, _) = Setup(on, h);
        double Gap(Context c, EngineSettings s)
        {
            var sc = Recommender.Score(c, vec, "u", cands, s);
            return sc["210"] - sc["010"];
        }

        Assert.True(Gap(ctxOn, on) > Gap(ctxOff, off) + 0.1, "l'animation du moment remonte face à l'horreur d'autrefois");
        var comp = Recommender.Compute(ctxOn, vec, "u", cands, on);
        Assert.True(comp.St[cands.IndexOf("210")] > 0.5 && comp.St[cands.IndexOf("010")] < 0.05);
        Assert.All(Recommender.Compute(ctxOff, vec, "u", cands, off).St, v => Assert.Equal(0, v)); // coupé : rien calculé
    }

    [Fact]
    public void ShownButNeverOpenedSinksThenRecovers()
    {
        var s = new EngineSettings();
        Assert.Equal(0, Recommender.ImpressionPenalty(new Exposure(0, Now), Now, s));
        double one = Recommender.ImpressionPenalty(new Exposure(1, Now), Now, s);
        double week = Recommender.ImpressionPenalty(new Exposure(7, Now), Now, s);
        double month = Recommender.ImpressionPenalty(new Exposure(30, Now), Now, s);
        Assert.True(one > 0 && one < week && week < month && month <= s.WImpression);
        Assert.Equal(s.WImpression * (1 - Math.Exp(-1)), week, 12);
        Assert.Equal(week / 2, Recommender.ImpressionPenalty(new Exposure(7, Now - (s.ImpressionRecoveryDays * Core.Day)), Now, s), 12);
        Assert.Equal(0, Recommender.ImpressionPenalty(new Exposure(30, Now), Now, new EngineSettings { WImpression = 0 }));
        Assert.Equal(0.9, new EngineSettings { WImpression = 5 }.Sanitized().WImpression);  // jamais tout le score

        var (_, ctx, vec, cands) = Setup(s, Liked("000", 3), Liked("001", 4), Liked("002", 5));
        var plain = Recommender.Score(ctx, vec, "u", cands, s);
        var top = cands.OrderByDescending(c => plain[c]).First();
        var shown = Recommender.Score(ctx, vec, "u", cands, s, exposure: new Dictionary<string, Exposure> { [top] = new(10, Now) });
        Assert.True(shown[top] < plain[top]);
        Assert.Equal(plain[top] * (1 - Recommender.ImpressionPenalty(new Exposure(10, Now), Now, s)), shown[top], 12);
        Assert.All(cands.Where(c => c != top), c => Assert.Equal(plain[c], shown[c]));
    }

    [Fact]
    public void PersonalizedDiversityFollowsTheAccountsVariety()
    {
        var s = new EngineSettings { Diversity = DiversityMode.Personalized, Mmr = 0.3 };
        // spécialiste : 8 films d'horreur ; éclectique : 2 de chaque monde
        var (_, cf, vec, cands) = Setup(s, Enumerable.Range(0, 8).Select(i => Liked($"0{i:00}", i + 1)).ToArray());
        var eclectic = Enumerable.Range(0, 4).SelectMany(g => new[] { Liked($"{g}00", g + 1), Liked($"{g}01", g + 5) }).ToArray();
        var (_, ce, vecE, candsE) = Setup(s, eclectic);
        double focused = Recommender.HistorySimilarity(cf, vec, "u")!.Value;
        double varied = Recommender.HistorySimilarity(ce, vecE, "u")!.Value;
        Assert.True(focused > varied);

        var lf = Recommender.Rank(cf, vec, cands, Recommender.Score(cf, vec, "u", cands, s), 10, s, "u");
        var le = Recommender.Rank(ce, vecE, candsE, Recommender.Score(ce, vecE, "u", candsE, s), 10, s, "u");
        Assert.True(Recommender.ListSimilarity(vec, lf) > Recommender.ListSimilarity(vecE, le), "liste resserrée pour le spécialiste, variée pour l'éclectique");

        // la liste retenue est celle dont la ressemblance interne est la plus proche de celle de l'historique
        var sc = Recommender.Score(ce, vecE, "u", candsE, s);
        double gap = Math.Abs(Recommender.ListSimilarity(vecE, le) - varied);
        foreach (var f in new[] { 0.0, 0.5, 1.0, 2.0, 4.0 })
        {
            var fixedL = Recommender.Rank(ce, vecE, candsE, sc, 10, new EngineSettings { Mmr = 0.3 * f, Diversity = DiversityMode.Fixed }, "u");
            Assert.True(gap <= Math.Abs(Recommender.ListSimilarity(vecE, fixedL) - varied) + 1e-12);
        }

        // moins de 3 titres aimés : diversité fixe
        var (_, c2, v2, k2) = Setup(s, Liked("000", 1), Liked("100", 2));
        Assert.Null(Recommender.HistorySimilarity(c2, v2, "u"));
        var sc2 = Recommender.Score(c2, v2, "u", k2, s);
        Assert.Equal(Recommender.Rank(c2, v2, k2, sc2, 10, new EngineSettings { Diversity = DiversityMode.Fixed }, "u"), Recommender.Rank(c2, v2, k2, sc2, 10, s, "u"));
    }

    [Fact]
    public void CalibratedListMirrorsTheGenresOfTheHistory()
    {
        // 6 horreurs, 2 comédies : un tri par score ne propose presque que de l'horreur
        var h = Enumerable.Range(0, 6).Select(i => Liked($"0{i:00}", i + 1)).Concat(new[] { Liked("100", 2), Liked("101", 3) }).ToArray();
        var plainS = new EngineSettings { Diversity = DiversityMode.Fixed, Mmr = 0 };
        var calS = new EngineSettings { Diversity = DiversityMode.Calibrated, CalibrationStrength = 0.6 };
        var (ds, ctx, vec, cands) = Setup(calS, h);
        var p = Recommender.GenreDistribution(ctx, "u");
        Assert.Equal(1.0, p.Values.Sum(), 9);
        Assert.True(p["Horreur"] > 0.6 && p["Comédie"] > 0.15);
        var sc = Recommender.Score(ctx, vec, "u", cands, calS);
        double Share(List<string> l, string g) => l.Count(id => ds.Titles[id].Genres.Contains(g)) / (double)l.Count;
        var plain = Recommender.Rank(ctx, vec, cands, sc, 10, plainS, "u");
        var cal = Recommender.Rank(ctx, vec, cands, sc, 10, calS, "u");
        double errPlain = Math.Abs(Share(plain, "Comédie") - p["Comédie"]), errCal = Math.Abs(Share(cal, "Comédie") - p["Comédie"]);
        Assert.True(errCal < errPlain || errCal <= 0.1, $"part de comédie : {Share(cal, "Comédie")} (visé {p["Comédie"]:0.00}), sans calibrage {Share(plain, "Comédie")}");
        Assert.True(Share(cal, "Horreur") >= 0.5, "le genre dominant reste dominant");
        Assert.True(Share(cal, "Documentaire") <= 0.2, "un genre jamais regardé ne s'impose pas");
    }

    [Fact]
    public void SettingsAreSanitized()
    {
        var s = new EngineSettings { ShortTermTitles = 99, CalibrationStrength = 3, ImpressionDays = -1, Diversity = (DiversityMode)42 }.Sanitized();
        Assert.Equal(30, s.ShortTermTitles);
        Assert.Equal(1, s.CalibrationStrength);
        Assert.Equal(1, s.ImpressionDays);
        Assert.Equal(DiversityMode.Fixed, s.Diversity);
        Assert.Equal(DiversityMode.Personalized, new EngineSettings().Diversity);
        Assert.Equal(0, new EngineSettings().WShortTerm);
    }

    [Fact]
    public void DataSufficiencyIsExplainedPerMethod()
    {
        Assert.Equal("insuffisant", Calibrator.Sufficiency(CalibrationMethod.ChoiceModel, 30).Level);
        Assert.Equal("limite", Calibrator.Sufficiency(CalibrationMethod.ChoiceModel, 70).Level);
        Assert.Equal("suffisant", Calibrator.Sufficiency(CalibrationMethod.ChoiceModel, 150).Level);
        Assert.Equal("limite", Calibrator.Sufficiency(CalibrationMethod.Coordinate, 300).Level);
        Assert.Equal("insuffisant", Calibrator.Sufficiency(CalibrationMethod.Coordinate, 150).Level);
        Assert.Equal("suffisant", Calibrator.Sufficiency(CalibrationMethod.Coordinate, 5000).Level);
        Assert.Contains("150 cas", Calibrator.Sufficiency(CalibrationMethod.Auto, 150).Message);
    }

    [Fact]
    public void ChoiceModelStaysNearTheCurrentSettingsWithoutEvidence()
    {
        var g = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement;
        var ds = ParityTests.LoadDataset(g.GetProperty("dataset"));
        var start = new EngineSettings();
        var sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, start));
        var cases = Calibrator.Cases(ds);
        Assert.NotEmpty(cases);
        var strong = ChoiceModel.Learn(ds, sim, cases, start, 1e6);
        Assert.All(ChoiceModel.Weights, w => Assert.Equal(w.Get(start), w.Get(strong)));   // sans preuve : rien ne bouge, pas même du bruit
        var free = ChoiceModel.Learn(ds, sim, cases, start, 1e-4);
        Assert.All(ChoiceModel.Weights, w => Assert.InRange(w.Get(free), 0, w.Hi));
        Assert.Equal(free.WFresh, ChoiceModel.Learn(ds, sim, cases, start, 1e-4).WFresh); // déterministe

        Assert.True(NeuralNet.IsZero(null) && NeuralNet.IsZero(new double[NeuralNet.Size]));
        Assert.Null(NeuralNet.With(start, new double[NeuralNet.Size]).Neural);              // correction nulle : aucune
        var rep = Calibrator.Calibrate(ds, start, method: CalibrationMethod.ChoiceModel);
        Assert.False(rep.Recommended);   // trop peu de cas synthétiques
        Assert.Equal(CalibrationMethod.ChoiceModel, rep.Method);
    }
}
