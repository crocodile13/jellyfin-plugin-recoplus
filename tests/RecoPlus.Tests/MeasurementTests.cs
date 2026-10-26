using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Mesure de l'effet des propositions : registre, taux par rangée et par place, part des lectures.</summary>
public class MeasurementTests
{
    private const double Now = 1_790_000_000;
    private const double D = Core.Day;

    private static Dictionary<string, List<string>> Rows(params (string Row, string[] Ids)[] r)
        => r.ToDictionary(x => x.Row, x => x.Ids.ToList(), StringComparer.Ordinal);

    [Fact]
    public void LedgerKeepsTheFirstOfferAndForgetsStaleOnes()
    {
        var l = new Dictionary<string, Measurement.Entry>(StringComparer.Ordinal);
        // même titre dans deux rangées : la meilleure place l'emporte ; titre hors bibliothèque ignoré
        Measurement.Record(l, Rows(("Films", new[] { "a", "b" }), ("PourToi", new[] { "x", "y", "b" }), ("ParceQue2", new[] { "c" }), ("Series", new[] { "tmdb:tv:1" })),
            id => !id.StartsWith("tmdb:", StringComparison.Ordinal), Now - (10 * D));
        Assert.Equal(("Films", 2), (l["b"].Row, l["b"].Position));
        Assert.Equal(("PourToi", 1), (l["x"].Row, l["x"].Position));
        Assert.Equal(("ParceQue", 1), (l["c"].Row, l["c"].Position));      // « ParceQue2 » → « ParceQue »
        Assert.False(l.ContainsKey("tmdb:tv:1"));

        // proposé de nouveau ailleurs : la première proposition reste, la dernière avance
        Measurement.Record(l, Rows(("Series", new[] { "b" })), _ => true, Now);
        Assert.Equal(("Films", 2, Now - (10 * D), Now), (l["b"].Row, l["b"].Position, l["b"].First, l["b"].Last));

        // plus proposé depuis plus de ForgetDays : oublié
        Measurement.Record(l, Rows(("Films", new[] { "b" })), _ => true, Now + ((Measurement.ForgetDays - 5) * D));
        Assert.Equal(new[] { "b" }, l.Keys.ToArray());

        // taille bornée : les plus anciennes dernières propositions partent d'abord
        var m = new Dictionary<string, Measurement.Entry>(StringComparer.Ordinal);
        Measurement.Record(m, Rows(("Films", new[] { "1", "2" })), _ => true, Now - D, max: 3);
        Measurement.Record(m, Rows(("Films", new[] { "3", "4" })), _ => true, Now, max: 3);
        Assert.Equal(new[] { "2", "3", "4" }, m.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void RatesCountOnlyTitlesOfferedLongEnoughAgoAndPlaysAfterTheOffer()
    {
        static Measurement.Entry E(string row, int pos, double daysAgo) => new() { Row = row, Position = pos, First = Now - (daysAgo * D), Last = Now - D };
        var ledger = new Dictionary<string, Dictionary<string, Measurement.Entry>>(StringComparer.Ordinal)
        {
            ["u"] = new(StringComparer.Ordinal)
            {
                ["m1"] = E("PourToi", 1, 40),   // commencé 2 j après, fini : compte partout
                ["m2"] = E("PourToi", 2, 40),   // jamais lu
                ["m3"] = E("Films", 8, 40),     // lu 10 j après : pas « sous 7 j », fini sous 30 j
                ["m4"] = E("Films", 25, 40),    // déjà lu AVANT la proposition : rien
                ["m5"] = E("PourToi", 3, 3),    // trop récent, pas lu : pas encore compté
                ["m6"] = E("PourToi", 4, 3),    // trop récent mais déjà commencé : compté (succès connu)
                ["s1"] = E("Series", 12, 40),   // série commencée, pas finie (10 %)
                ["k1"] = E("PourToi", 5, 40),   // seulement aimé (liste de suivi) : pas une lecture
            },
        };
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal)
        {
            ["s1"] = new() { Id = "s1", Kind = TitleKind.Series, Name = "S" },
        };
        Interaction I(string id, double startDaysAgo, double lastDaysAgo, bool played, double comp, int eps = 0)
            => new() { Title = id, Start = Now - (startDaysAgo * D), Last = Now - (lastDaysAgo * D), Played = played, Completion = comp, Episodes = eps };
        var hist = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal)
        {
            ["u"] = new(StringComparer.Ordinal)
            {
                ["m1"] = I("m1", 38, 38, true, 1),
                ["m3"] = I("m3", 30, 29, true, 1),
                ["m4"] = I("m4", 60, 60, true, 1),
                ["m6"] = I("m6", 1, 1, false, 0.3),
                ["s1"] = I("s1", 39, 20, true, 0.1, 2),
                ["k1"] = new() { Title = "k1", Start = Now - (39 * D), Watchlisted = Now - (39 * D), Rating = 1.0 },
                ["z"] = I("z", 5, 5, true, 1),           // lu récemment sans avoir été proposé
            },
        };

        var r = Measurement.Compute(ledger, hist, titles, Now);
        // sous 7 j : m1, m2, m3, m4, s1, k1 (≥ 7 j) + m6 (déjà commencé) ; commencés : m1, s1, m6
        Assert.Equal((7, 3), (r.All.Proposed7, r.All.Started7));
        // sous 30 j : m1, m2, m3, m4, s1, k1 ; finis : m1, m3 (la série à 10 % ne l'est pas)
        Assert.Equal((6, 2), (r.All.Proposed30, r.All.Finished30));
        Assert.Equal(Math.Round(3.0 / 7, 4), r.All.StartRate);
        Assert.Equal((4, 2), (r.ByRow["PourToi"].Proposed7, r.ByRow["PourToi"].Started7));
        Assert.Equal((2, 0, 2, 1), (r.ByRow["Films"].Proposed7, r.ByRow["Films"].Started7, r.ByRow["Films"].Proposed30, r.ByRow["Films"].Finished30));
        Assert.Equal(new[] { "1-5", "11-20", "21+", "6-10" }, r.ByPosition.Keys.ToArray());
        Assert.Equal(2, r.ByPosition["1-5"].Started7);              // m1, m6
        // lectures des 30 derniers jours : m3 (il y a 30 j), m6, z ; proposées peu avant : m3, m6
        Assert.Equal((3, 2), (r.RecentStarts, r.RecentFromReco));
        Assert.Equal(40, r.Days);
    }

    [Fact]
    public void EmptyInputsGiveAnEmptyReportWithoutRates()
    {
        var r = Measurement.Compute(new Dictionary<string, Dictionary<string, Measurement.Entry>>(), new Dictionary<string, Dictionary<string, Interaction>>(), new Dictionary<string, Title>(), Now);
        Assert.Null(r.All.StartRate);
        Assert.Null(r.All.FinishRate);
        Assert.Null(r.RecentShare);
        Assert.Equal(0, r.Days);
        Assert.Equal("1-5", Measurement.Bucket(1));
        Assert.Equal("6-10", Measurement.Bucket(10));
        Assert.Equal("21+", Measurement.Bucket(99));
    }

    [Fact]
    public void LedgerIsPersistedPerAccountAndBrokenEntriesAreDropped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recoplus-led-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "propositions.json");
        try
        {
            using (var st = new RecoLedger(() => file, NullLogger<RecoLedger>.Instance))
            {
                st.Record("u", Rows(("PourToi", new[] { "a", "b" })), _ => true, Now);
                st.Record("v", Rows(("Films", new[] { "c" })), _ => true, Now);
                st.Forget("v");
                var snap = st.Snapshot();
                snap["u"]["a"].Position = 99;                                    // copie : le registre ne bouge pas
                Assert.Equal(1, st.Snapshot()["u"]["a"].Position);
            }

            using (var again = new RecoLedger(() => file, NullLogger<RecoLedger>.Instance))
            {
                var s = again.Snapshot();
                Assert.Equal(new[] { "u" }, s.Keys.ToArray());
                Assert.Equal(("PourToi", 2, Now), (s["u"]["b"].Row, s["u"]["b"].Position, s["u"]["b"].First));
            }

            File.WriteAllText(file, "{\"u\":{\"a\":{\"Row\":\"\",\"Position\":1,\"First\":1,\"Last\":2},\"b\":{\"Row\":\"Films\",\"Position\":0,\"First\":1,\"Last\":2},\"c\":{\"Row\":\"Films\",\"Position\":1,\"First\":3,\"Last\":2},\"d\":{\"Row\":\"Films\",\"Position\":4,\"First\":1,\"Last\":2}}}");
            using (var st = new RecoLedger(() => file, NullLogger<RecoLedger>.Instance))
            {
                Assert.Equal(new[] { "d" }, st.Snapshot()["u"].Keys.ToArray());
            }

            File.WriteAllText(file, "{ pas du json");
            using var broken = new RecoLedger(() => file, NullLogger<RecoLedger>.Instance);
            Assert.Empty(broken.Snapshot());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
