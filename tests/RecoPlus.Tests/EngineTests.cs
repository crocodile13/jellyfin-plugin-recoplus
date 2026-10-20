using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class EngineTests
{
    private const double Now = 1_790_000_000;
    private static readonly EngineSettings S = new();

    private static Interaction Movie(double completion, double idleDays, bool played = false, bool fav = false)
        => new() { Title = "x", Completion = completion, Played = played, Favorite = fav, Last = Now - (idleDays * Core.Day), Start = Now - (idleDays * Core.Day) };

    [Fact]
    public void MovieAbandonHasMargin()
    {
        // vu à moitié il y a 5 jours : on peut le finir en deux fois → neutre
        Assert.Equal(0, Core.Signal(Movie(0.5, 5), TitleKind.Movie, 0, Now, S));
        // vu à moitié, jamais repris depuis 40 jours → abandon
        Assert.Equal(-S.AbandonWeight, Core.Signal(Movie(0.5, 40), TitleKind.Movie, 0, Now, S));
        // zapping (2 %) même ancien → neutre
        Assert.Equal(0, Core.Signal(Movie(0.02, 400), TitleKind.Movie, 0, Now, S));
        // 75 % = vu
        Assert.True(Core.Signal(Movie(0.75, 1), TitleKind.Movie, 0, Now, S) > 0.99);
        // favori annule l'abandon
        Assert.True(Core.Signal(Movie(0.5, 40, fav: true), TitleKind.Movie, 0, Now, S) > 0);
    }

    [Fact]
    public void SeriesAbandonNeedsLongSeriesAndIdle()
    {
        var x = new Interaction { Title = "s", Episodes = 1, Completion = 1 / 24.0, Last = Now - (60 * Core.Day) };
        Assert.Equal(-S.AbandonWeight, Core.Signal(x, TitleKind.Series, 24, Now, S));
        Assert.True(Core.Signal(x, TitleKind.Series, 4, Now, S) > 0);            // mini-série : pas un abandon
        x.Last = Now - (3 * Core.Day);
        Assert.True(Core.Signal(x, TitleKind.Series, 24, Now, S) > 0);           // encore en cours
    }

    [Fact]
    public void RequestCountsOnlyWhenNotStarted()
    {
        var x = new Interaction { Title = "r", Requested = Now - Core.Day };
        Assert.True(Math.Abs(Core.Signal(x, TitleKind.Movie, 0, Now, S) - (S.RequestWeight * Math.Pow(0.5, 1 / 21.0))) < 1e-12);
        x.Requested = Now - (21 * Core.Day);
        Assert.True(Math.Abs(Core.Signal(x, TitleKind.Movie, 0, Now, S) - (S.RequestWeight / 2)) < 1e-12); // court terme
        x.Requested = Now - (100 * Core.Day);
        Assert.Equal(0, Core.Signal(x, TitleKind.Movie, 0, Now, S));                                     // trop vieille : ne compte plus
        var watched = Movie(1, 2, played: true);
        watched.Requested = Now - Core.Day;
        Assert.True(Math.Abs(Core.Signal(watched, TitleKind.Movie, 0, Now, S) - Core.Signal(Movie(1, 2, played: true), TitleKind.Movie, 0, Now, S)) < 1e-12); // vu : la demande ne compte plus
        var abandoned = Movie(0.5, 40);
        abandoned.Requested = Now - (50 * Core.Day);
        Assert.Equal(-S.AbandonWeight, Core.Signal(abandoned, TitleKind.Movie, 0, Now, S)); // demandé puis abandonné ≠ voulu
    }

    [Fact]
    public void SanitizedEnforcesFreshCeilingAndGate()
    {
        var s = new EngineSettings { WFresh = 50, GateTop = 0, HalfLifeDays = double.NaN, Mmr = -3 }.Sanitized();
        Assert.Equal(EngineSettings.FreshCeiling, s.WFresh);
        Assert.Equal(5, s.GateTop);
        Assert.Equal(7, s.HalfLifeDays);
        Assert.Equal(0, s.Mmr);
    }

    private static Title T(string id, TitleKind k, string[] genres, int? year = 2010, double? added = null, string name = "")
        => new() { Id = id, Kind = k, Genres = genres, Year = year, Added = added, Name = name == string.Empty ? id : name };

    [Fact]
    public void GateBlocksFreshTitleWithoutTaste()
    {
        // 70 titres « Drame » proches des goûts, 1 titre « Ecchi » tout neuf sans aucun rapport
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        for (int i = 0; i < 70; i++)
        {
            titles[$"d{i}"] = T($"d{i}", TitleKind.Movie, new[] { "Drame", i % 2 == 0 ? "Romance" : "Comédie" });
        }

        titles["new"] = T("new", TitleKind.Series, new[] { "Ecchi" }, added: Now - 3600);
        var h = new Dictionary<string, Interaction>(StringComparer.Ordinal)
        {
            ["d0"] = new() { Title = "d0", Played = true, Completion = 1, Last = Now - Core.Day, Start = Now - Core.Day },
            ["d1"] = new() { Title = "d1", Played = true, Completion = 1, Last = Now - Core.Day, Start = Now - Core.Day },
        };
        var ds = new Dataset { Titles = titles, Users = new() { ["u"] = h }, Now = Now };
        ds.LinkCollections();
        var s = new EngineSettings { WFresh = EngineSettings.FreshCeiling, GateTop = 60 };
        var vec = Core.BuildVectors(titles, s);
        var ctx = new Context(ds, Now, ds.Users, s);
        var cands = titles.Keys.Where(k => !h.ContainsKey(k)).ToList();
        var sc = Recommender.Score(ctx, vec, "u", cands, s);
        var list = Recommender.Rank(ctx, vec, cands, sc, 20, s);
        Assert.DoesNotContain("new", list);
    }

    [Fact]
    public void SagaProposesFirstUnseenVolumeOnce()
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        for (int i = 1; i <= 5; i++)
        {
            titles[$"hp{i}"] = T($"hp{i}", TitleKind.Movie, new[] { "Fantastique", "Aventure" }, 2000 + i, name: $"HP {i}");
        }

        titles["other"] = T("other", TitleKind.Movie, new[] { "Fantastique" });
        titles["seen"] = T("seen", TitleKind.Movie, new[] { "Fantastique", "Aventure" });
        var h = new Dictionary<string, Interaction>(StringComparer.Ordinal)
        {
            ["hp1"] = new() { Title = "hp1", Played = true, Completion = 1, Last = Now - Core.Day, Start = Now - Core.Day },
            ["seen"] = new() { Title = "seen", Played = true, Completion = 1, Last = Now - Core.Day, Start = Now - Core.Day },
        };
        var ds = new Dataset
        {
            Titles = titles,
            Users = new() { ["u"] = h },
            Collections = new() { ["saga"] = new() { "hp1", "hp2", "hp3", "hp4", "hp5" } },
            Now = Now,
        };
        ds.LinkCollections();
        var vec = Core.BuildVectors(titles, S);
        var ctx = new Context(ds, Now, ds.Users, S);
        var cands = titles.Keys.Where(k => !h.ContainsKey(k)).ToList();
        var sc = Recommender.Score(ctx, vec, "u", cands, S);
        var list = Recommender.Rank(ctx, vec, cands, sc, 10, S);
        Assert.Equal(1, list.Count(x => x.StartsWith("hp", StringComparison.Ordinal)));
        Assert.Contains("hp2", list);                                           // HP 1 vu → HP 2, pas HP 4
    }

    [Fact]
    public void EvaluationMatchesPrototype()
    {
        var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement;
        var ds = ParityTests.LoadDataset(g.GetProperty("dataset"));
        var s = ParityTests.Settings(g.GetProperty("cases")[0]);
        var ev = Calibrator.Events(ds);
        var want = g.GetProperty("evaluation").EnumerateArray().ToList();
        Assert.Equal(want.Count, ev.Count);
        for (int i = 0; i < ev.Count; i++)
        {
            Assert.Equal(want[i].GetProperty("user").GetString(), ev[i].User);
            Assert.Equal(want[i].GetProperty("target").GetString(), ev[i].Target);
        }

        var r = Calibrator.Evaluate(ds, Core.BuildVectors(ds.Titles, s), ev, s);
        Assert.Equal(want.Select(e => e.GetProperty("hit10").GetBoolean()), r.PerEvent10);
        Assert.Equal(want.Select(e => e.GetProperty("hit20").GetBoolean()), r.PerEvent20);
    }

    [Fact]
    public void CalibrationRefusesWithTooFewEvents()
    {
        var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement;
        var ds = ParityTests.LoadDataset(g.GetProperty("dataset"));
        foreach (var u in ds.Users.Keys.OrderBy(k => k, StringComparer.Ordinal).Skip(2).ToList())
        {
            ds.Users.Remove(u);                                                 // deux comptes : trop peu de cas
        }

        Assert.InRange(Calibrator.Cases(ds).Count, 0, Calibrator.MinEvents - 1);
        var rep = Calibrator.Calibrate(ds, new EngineSettings());
        Assert.False(rep.Recommended);
        Assert.Contains("pas assez", rep.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapIdenticalIsZero()
    {
        var ev = Enumerable.Range(0, 20).Select(i => new EvalEvent($"u{i % 3}", "t", i)).ToList();
        var a = Enumerable.Range(0, 20).Select(i => i % 2 == 0).ToArray();
        Assert.Equal((0.0, 0.0), Calibrator.BootstrapCi(ev, a, a));
    }
}
