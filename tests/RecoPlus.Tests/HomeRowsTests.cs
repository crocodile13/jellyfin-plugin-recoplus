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
    public void ABecauseRowNeverContainsItsOwnAnchor()
    {
        var (ctx, vec, cands, sc) = Setup();
        // titres aimés encore proposables (noté sans lecture, option « proposer ce qui est marqué ») : ils ne doivent
        // jamais ouvrir la rangée « Parce que tu as aimé » dont ils sont le titre de départ
        var withAnchors = cands.Concat(new[] { "001", "101", "201" }).ToList();
        var full = new Dictionary<string, double>(sc) { ["001"] = 9, ["101"] = 9, ["201"] = 9 };
        var ordered = withAnchors.OrderByDescending(c => full[c]).ThenBy(c => c, StringComparer.Ordinal).ToList();
        var pourToi = ordered.Where(c => !c.EndsWith("01", StringComparison.Ordinal)).Take(5).ToList();
        var r = HomeRows.Allocate(ctx, vec, "u", pourToi, ordered, full, new RowOptions { Size = 10, Because = 3 });
        Assert.NotEmpty(r.Anchors);
        foreach (var (row, name) in r.Anchors)
        {
            var anchor = ctx.Ds.Titles.Values.Single(t => t.Name == name).Id;
            Assert.DoesNotContain(anchor, r.Rows[row]);
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

    [Fact]
    public void FreePlacesFollowJointScore()
    {
        var lib = new[] { 0.9, 0.8, 0.7, 0.6, 0.5 };
        Assert.Equal(new[] { 0, 2, 5, 5 }, HomeRows.FreePlaces(lib, new[] { 0.95, 0.75, 0.1, 0.0 }, 0));
        Assert.Equal(new[] { 0, 2, 5, 5 }, HomeRows.FreePlaces(lib, new[] { 0.95, 0.75, 0.1, 0.0 }, 2));   // minimum déjà atteint
        Assert.Equal(new[] { 0, 0 }, HomeRows.FreePlaces(Array.Empty<double>(), new[] { 0.3, 0.2 }, 3));    // rangée vide : rien à faire
    }

    [Fact]
    public void FreePlacesGuaranteeMinimumSpreadOverTheRow()
    {
        var lib = Enumerable.Range(0, 17).Select(i => 1.0 - (i * 0.01)).ToArray();
        var ext = Enumerable.Range(0, 20).Select(i => 0.5 - (i * 0.01)).ToArray();   // tous derrière la rangée
        var a = HomeRows.FreePlaces(lib, ext, 3);
        Assert.Equal(new[] { 4, 9, 13 }, a.Take(3));                                  // les 3 mieux classés, répartis
        Assert.All(a.Skip(3), x => Assert.Equal(17, x));                               // les autres restent derrière
        Assert.Equal(new[] { 9, 17 }, HomeRows.FreePlaces(lib, new[] { 0.5, 0.4 }, 1));

        // un titre déjà placé compte dans le minimum ; le complément vient des suivants, dans l'ordre du classement
        var mixed = HomeRows.FreePlaces(lib, new[] { 0.995, 0.5, 0.4, 0.3 }, 3);
        Assert.Equal(new[] { 1, 6, 11, 17 }, mixed);
        Assert.Equal(new[] { 8, 16 }, HomeRows.FreePlaces(lib.Take(16).ToArray(), new[] { 0.5, 0.4 }, 1));
        Assert.Equal(new[] { 0 }, HomeRows.FreePlaces(new[] { 1.0 }, new[] { 0.5 }, 5));   // jamais au-delà de la rangée
    }

    [Fact]
    public void ForcePlacesTakesUnplacedTitlesInTheGivenOrder()
    {
        var after = new[] { 3, 17, 17, 17, 17 };
        // découverte : les mieux notés d'abord (ordre 4, 2, …), seulement ceux sans place
        var a = HomeRows.ForcePlaces((int[])after.Clone(), 17, new[] { 0, 4, 2, 1 }, 2);
        Assert.Equal(new[] { 3, 17, 11, 17, 6 }, a);
        Assert.Equal(after, HomeRows.ForcePlaces((int[])after.Clone(), 17, new[] { 4 }, 0));
        Assert.Equal(after, HomeRows.ForcePlaces((int[])after.Clone(), 0, new[] { 4 }, 2));
    }
}
