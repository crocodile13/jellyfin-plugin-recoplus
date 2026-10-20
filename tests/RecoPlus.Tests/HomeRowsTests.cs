using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Rangées d'accueil sans doublons, et affichages enregistrés.</summary>
public class HomeRowsTests
{
    private const double Now = 1_790_000_000;

    private static (Context Ctx, Dictionary<string, SparseVector> Vec, List<string> Cands, Dictionary<string, double> Sc) Setup()
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        string[] genres = { "Horreur", "Comédie", "Animation" };
        for (int g = 0; g < genres.Length; g++)
        {
            for (int i = 0; i < 40; i++)
            {
                var id = $"{g}{i:00}";
                titles[id] = new Title
                {
                    Id = id,
                    Kind = i % 3 == 0 ? TitleKind.Series : TitleKind.Movie,
                    Name = $"T{id}",
                    Genres = new[] { genres[g] },
                    Tags = new[] { $"k{g}", $"t{g}{i % 4}" },
                    Added = Now - ((i < 5 ? 3 : 300) * Core.Day),
                };
            }
        }

        Interaction L(string id, double d) => new() { Title = id, Played = true, Completion = 1, Start = Now - ((d + 1) * Core.Day), Last = Now - (d * Core.Day) };
        var h = new[] { L("001", 1), L("002", 2), L("101", 3), L("102", 4), L("201", 5) }.ToDictionary(x => x.Title, x => x, StringComparer.Ordinal);
        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal) { ["u"] = h };
        var ds = new Dataset { Titles = titles, Users = users, Now = Now };
        ds.Collections["saga"] = new List<string> { "001", "002", "010" };
        ds.LinkCollections();
        var s = new EngineSettings();
        var ctx = new Context(ds, Now, users, s);
        var vec = Core.BuildVectors(titles, s);
        var cands = titles.Keys.Where(k => !h.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return (ctx, vec, cands, Recommender.Score(ctx, vec, "u", cands, s));
    }

    [Fact]
    public void RowsNeverRepeatATitleAndBecauseRowsUseDifferentAnchors()
    {
        var (ctx, vec, cands, sc) = Setup();
        var ordered = cands.OrderByDescending(c => sc[c]).ThenBy(c => c, StringComparer.Ordinal).ToList();
        var o = new RowOptions { Size = 10, Movies = true, Series = true, New = true, NewDays = 30, Because = 3 };
        var r = HomeRows.Allocate(ctx, vec, "u", ordered.Take(30).ToList(), ordered, sc, o);

        var all = r.Rows.Values.SelectMany(x => x).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());                       // aucun titre dans deux rangées
        Assert.Equal(ordered.Take(16), r.Rows[HomeRows.PourToi]);              // « Pour toi » intact, réserves comprises
        Assert.All(r.Rows.Values, l => Assert.True(l.Count <= 16));
        Assert.All(r.Rows[HomeRows.Films], id => Assert.Equal(TitleKind.Movie, ctx.Ds.Titles[id].Kind));
        Assert.All(r.Rows[HomeRows.Series], id => Assert.Equal(TitleKind.Series, ctx.Ds.Titles[id].Kind));
        Assert.All(r.Rows[HomeRows.Nouveautes], id => Assert.True(ctx.Ds.Titles[id].Added >= Now - (30 * Core.Day)));

        // trois titres aimés différents, jamais deux de la même saga (001 et 002 sont dans la même)
        Assert.Equal(3, r.Anchors.Count);
        Assert.Equal(r.Anchors.Count, r.Anchors.Values.Distinct().Count());
        Assert.False(r.Anchors.ContainsValue("T001") && r.Anchors.ContainsValue("T002"));
        foreach (var (row, name) in r.Anchors)
        {
            var anchor = ctx.Ds.Titles.Values.Single(t => t.Name == name).Id;
            Assert.All(r.Rows[row], id => Assert.True(vec[anchor].Dot(vec[id]) >= HomeRows.MinSimilarity));
        }
    }

    [Fact]
    public void OnlyRequestedRowsAndEmptyBecauseRowsWhenNothingFits()
    {
        var (ctx, vec, cands, sc) = Setup();
        var ordered = cands.OrderByDescending(c => sc[c]).ToList();
        var r = HomeRows.Allocate(ctx, vec, "u", ordered, ordered, sc, new RowOptions());
        Assert.Equal(new[] { HomeRows.PourToi }, r.Rows.Keys.ToArray());

        var none = HomeRows.Allocate(ctx, vec, "nobody", ordered, ordered, sc, new RowOptions { Because = 2 });
        Assert.Empty(none.Anchors);
        Assert.Empty(none.Rows[HomeRows.ParceQue + 1]);
        Assert.Empty(none.Rows[HomeRows.ParceQue + 2]);
    }

    [Fact]
    public void ImpressionsCountDistinctDaysAndOpeningResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recoplus-imp-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "affichages.json");
        try
        {
            const string A = "0123456789abcdef0123456789abcdef";
            using (var st = new ImpressionStore(() => file, NullLogger<ImpressionStore>.Instance))
            {
                Assert.Equal(2, st.Record("u", new[] { A, "tmdb:movie:42", "../etc/passwd", "<script>", null }, null, Now));
                st.Record("u", new[] { A }, null, Now + 3600);                     // même jour : pas un jour de plus
                st.Record("u", new[] { A.ToUpperInvariant() }, null, Now + 86400); // lendemain (casse ignorée)
                var e = st.For("u", Now + 86400);
                Assert.Equal(2, e[A].Days);
                Assert.Equal(Now + 86400, e[A].Last);
                Assert.Equal(1, e["tmdb:movie:42"].Days);
                Assert.Equal(2, e.Count);
                Assert.Empty(st.For("autre", Now));                               // par compte

                st.Record("u", new[] { "tmdb:movie:42" }, new[] { "tmdb:movie:42" }, Now + 86400 * 2); // ouvert le même jour
                st.Record("u", null, new[] { A }, Now + 86400 * 2);
                Assert.Empty(st.For("u", Now + 86400 * 2));
                st.Record("u", new[] { A }, null, Now + 86400 * 3);
            }

            using var again = new ImpressionStore(() => file, NullLogger<ImpressionStore>.Instance);
            Assert.Equal(1, again.For("u", Now + 86400 * 3)[A].Days);              // relu depuis le disque
            Assert.Empty(again.For("u", Now + (86400 * 400)));                     // oublié au-delà d'un an
            Assert.Equal(ImpressionStore.MaxPerCall, again.Record("v", Enumerable.Range(0, 500).Select(i => $"tmdb:tv:{i}"), null, Now));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void BrokenImpressionFileIsIgnored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recoplus-imp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "affichages.json");
        try
        {
            File.WriteAllText(file, "{ pas du json");
            using var st = new ImpressionStore(() => file, NullLogger<ImpressionStore>.Instance);
            Assert.Empty(st.For("u", Now));
            File.WriteAllText(file, "{\"u\":{\"../x\":{\"Days\":3,\"LastDay\":1,\"Last\":1}}}");
            using var st2 = new ImpressionStore(() => file, NullLogger<ImpressionStore>.Instance);
            Assert.Empty(st2.For("u", 1));                                         // identifiant refusé à la relecture
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
