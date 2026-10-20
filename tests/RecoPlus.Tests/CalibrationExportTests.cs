using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.RecoPlus.Data;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Export du jeu de calibrage : relu à l'identique par le banc d'essai, comptes pseudonymisés.</summary>
public class CalibrationExportTests
{
    [Fact]
    public void ExportRoundTripsAndHidesAccounts()
    {
        var ds = ParityTests.LoadDataset(JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement.GetProperty("dataset"));
        // comptes aux identifiants « réels », un titre hors bibliothèque demandé, un avis du foyer
        var real = ds.Users.Keys.OrderBy(k => k, StringComparer.Ordinal).Select((k, i) => (k, Guid.Parse($"0000000{i}-1111-2222-3333-444444444444").ToString("N"))).ToList();
        var users = real.ToDictionary(x => x.Item2, x => ds.Users[x.k], StringComparer.Ordinal);
        var titles = ds.Titles.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        titles["tmdb:movie:42"] = new Title { Id = "tmdb:movie:42", Kind = TitleKind.Movie, Name = "Ext", Genres = new[] { "g" }, External = true, Votes = 1234 };
        var u0 = real[0].Item2;
        users[u0]["tmdb:movie:42"] = new Interaction { Title = "tmdb:movie:42", Requested = ds.Now - 1000 };
        var t0 = ds.Titles.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        var reviews = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, double>>(StringComparer.Ordinal)
        {
            [t0] = new(StringComparer.Ordinal) { [u0] = 4.5 },
        };
        var src = new Dataset { Titles = titles, Users = users, Collections = ds.Collections, Now = ds.Now, Reviews = reviews, LocalHour = t => (int)(((t + 7200) % 86400) / 3600) };
        src.LinkCollections();

        using var ms = new MemoryStream();
        CalibrationExport.Write(src, ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        Assert.All(real, r => Assert.DoesNotContain(r.Item2, json));                 // aucun identifiant de compte

        var back = ParityTests.LoadDataset(JsonDocument.Parse(json).RootElement);
        Assert.Equal(src.Users.Count, back.Users.Count);
        Assert.Equal("u01", back.Users.Keys.OrderBy(k => k, StringComparer.Ordinal).First());
        Assert.Equal(4.5, back.Reviews[t0]["u01"]);
        Assert.True(back.Titles["tmdb:movie:42"].External);
        Assert.Equal(1234, back.Titles["tmdb:movie:42"].Votes);
        Assert.Equal(src.Users[u0]["tmdb:movie:42"].Requested, back.Users["u01"]["tmdb:movie:42"].Requested);
        Assert.Equal(src.HourOf(ds.Now), back.HourOf(ds.Now));

        // même évaluation avant et après
        var s = new EngineSettings();
        var a = Calibrator.EvaluateRanks(src, new SimilarityIndex(Core.BuildVectors(src.Titles, s)), Calibrator.Cases(src), s);
        var b = Calibrator.EvaluateRanks(back, new SimilarityIndex(Core.BuildVectors(back.Titles, s)), Calibrator.Cases(back), s);
        Assert.Equal(a.Events, b.Events);
        Assert.True(Math.Abs(a.Gain - b.Gain) < 1e-9);
    }
}
