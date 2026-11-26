using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class RegionsTests
{
    [Fact]
    public void TimeZonesAndLanguagesPointToACountry()
    {
        Assert.Equal("FR", Regions.OfTimeZone("Europe/Paris"));
        Assert.Equal("CA", Regions.OfTimeZone("America/Montreal"));   // ancien nom (alias de tzdata)
        Assert.Equal("UA", Regions.OfTimeZone("Europe/Kiev"));
        Assert.Null(Regions.OfTimeZone("UTC"));
        Assert.Null(Regions.OfTimeZone("Etc/GMT+2"));
        Assert.Null(Regions.OfTimeZone(new string('a', 200)));

        Assert.Equal(("CA", true), Regions.OfLanguage("fr-CA"));
        Assert.Equal(("BR", true), Regions.OfLanguage("pt_BR"));
        Assert.Equal(("TW", true), Regions.OfLanguage("zh-Hant-TW"));
        Assert.Equal(("FR", false), Regions.OfLanguage("fr"));
        Assert.Equal(("FR", false), Regions.OfLanguage("fre"));        // ISO 639-2/B, comme Jellyfin
        Assert.Equal(("DE", false), Regions.OfLanguage("deu"));        // ISO 639-2/T
        Assert.Equal((null, false), Regions.OfLanguage("en"));         // l'anglais seul ne dit pas le pays
        Assert.Equal((null, false), Regions.OfLanguage(""));
        Assert.Contains("FR", Regions.Countries);
        Assert.True(Regions.Countries.Count > 200);
    }

    [Fact]
    public void TheStrongestCluesWinAndAChoiceComesFirst()
    {
        // fuseau belge, navigateur en français de France, audio français : le fuseau (3) pèse plus que la langue
        var clues = Regions.Clues("Europe/Brussels", new[] { "fr-FR" }, "fre", null, null, null, null);
        Assert.Equal("BE", Regions.Detect(null, clues, "FR").Country);
        Assert.True(Regions.Detect(null, clues, "FR").Detected);

        // sans fuseau : la langue avec région l'emporte sur la langue seule
        clues = Regions.Clues(null, new[] { "fr-CA", "en-US" }, "fre", "fre", null, null, null);
        Assert.Equal("CA", Regions.Detect(null, clues, "FR").Country);

        // choix du compte ou de l'administrateur : avant les indices, sans être « détecté »
        var g = Regions.Detect("de", clues, "FR");
        Assert.Equal(("DE", false), (g.Country, g.Detected));

        // rien : le pays de repli ; un repli mal formé redevient la France
        Assert.Equal(("IT", false), (Regions.Detect(null, Regions.Clues(null, new[] { "en" }, null, null, null, null, "UTC"), "it").Country, false));
        Assert.Equal("FR", Regions.Detect(null, Array.Empty<RegionClue>(), "???").Country);
        Assert.Equal("FR", Regions.Detect("UK", Array.Empty<RegionClue>(), "FR").Country);   // « UK » n'est pas un code : c'est GB
        Assert.Null(Regions.Clean("ZZ"));
        Assert.Equal("GB", Regions.Clean(" gb "));

        // le serveur seul compte peu, mais suffit quand il n'y a rien d'autre
        clues = Regions.Clues(null, null, null, null, "ES", null, null);
        Assert.Equal("ES", Regions.Detect(null, clues, "FR").Country);
        clues = Regions.Clues("Europe/Rome", null, null, null, "ES", "es", "Europe/Madrid");
        Assert.Equal("IT", Regions.Detect(null, clues, "FR").Country);
    }

    [Fact]
    public void AMovieIsNewWhereItWasReleasedLately()
    {
        double now = Releases.Parse("2026-11-26")!.Value;
        var local = new Dictionary<string, string> { ["FR"] = "2026-11-12", ["US"] = "2026-06-01" };
        Assert.True(Releases.IsNew(Releases.In("FR", local, "2026-05-20"), now, 60));
        Assert.False(Releases.IsNew(Releases.In("US", local, "2026-05-20"), now, 60));       // sorti il y a six mois là-bas
        Assert.False(Releases.IsNew(Releases.In("DE", local, "2026-05-20"), now, 60));       // sans date allemande : la date principale
        Assert.True(Releases.IsNew(Releases.In("DE", local, "2026-10-30"), now, 60));
        Assert.False(Releases.IsNew(Releases.In("FR", new Dictionary<string, string> { ["FR"] = "2027-01-10" }, "2026-11-01"), now, 60));   // pas encore sorti ici
        Assert.False(Releases.IsNew(null, now, 60));
        Assert.Null(Releases.Parse("demain"));
        Assert.Equal(Releases.Parse("2026-11-12"), Releases.Parse("2026-11-12T00:00:00.000Z"));
    }

    [Fact]
    public void UserPreferencesKeepOnlyWhatIsValidAndOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recoplus-prefs-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "preferences.json");
        try
        {
            var u = Guid.NewGuid().ToString("N");
            var store = new UserPrefsStore(() => path, NullLogger<UserPrefsStore>.Instance);
            Assert.True(store.Report(u, "Europe/Paris", new[] { "fr-FR", "<script>", "en", "de", "es", "it" }));
            Assert.False(store.Report(u, "Europe/Kyiv", null));   // moins d'une minute après : ignoré
            var p = store.For(u);
            Assert.Equal("Europe/Paris", p.BrowserZone);
            Assert.Equal(new[] { "fr-FR", "en", "de", "es" }, p.BrowserLanguages);

            // un fuseau inconnu n'est pas gardé (magasin sans intervalle entre deux signalements)
            var quick = new UserPrefsStore(() => path, NullLogger<UserPrefsStore>.Instance) { ReportInterval = TimeSpan.Zero };
            Assert.False(quick.Report(u, "Mars/Olympus", null));
            Assert.Equal("Europe/Paris", quick.For(u).BrowserZone);   // un navigateur qui ne dit rien d'utile n'efface pas l'indice connu
            Assert.True(quick.Report(u, "Europe/Brussels", null));
            Assert.Equal(("Europe/Brussels", "fr-FR"), (quick.For(u).BrowserZone, quick.For(u).BrowserLanguages![0]));   // langues gardées
            store = quick;

            // seulement ce que l'administrateur ouvre ; un pays mal formé est refusé
            Assert.True(store.Set(u, "be", false, canRegion: true, canExtended: false));
            Assert.Equal(("BE", (bool?)null), (store.For(u).Region, store.For(u).Extended));
            Assert.False(store.Set(u, "Belgique", null, canRegion: true, canExtended: true));
            Assert.True(store.Set(u, null, false, canRegion: false, canExtended: true));
            Assert.Equal(("BE", (bool?)false), (store.For(u).Region, store.For(u).Extended));

            // relu depuis le disque, et oublié quand le compte disparaît
            var again = new UserPrefsStore(() => path, NullLogger<UserPrefsStore>.Instance);
            Assert.Equal("BE", again.For(u).Region);
            Assert.Equal(1, again.Retain(new HashSet<string>()));
            Assert.Null(new UserPrefsStore(() => path, NullLogger<UserPrefsStore>.Instance).For(u).Region);

            // un fichier modifié à la main ne fait rien passer d'invalide
            File.WriteAllText(path, "{\"" + u + "\":{\"Region\":\"xyz\",\"BrowserZone\":\"../../etc\",\"BrowserLanguages\":[\"fr\",\"<b>\"]},\"pas-un-compte\":{\"Region\":\"FR\"}}");
            var read = new UserPrefsStore(() => path, NullLogger<UserPrefsStore>.Instance).For(u);
            Assert.Null(read.Region);
            Assert.Null(read.BrowserZone);
            Assert.Equal(new[] { "fr" }, read.BrowserLanguages);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
