using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class OriginTests
{
    private static Title T(string id, string? lang, params string[] countries)
        => new() { Id = id, Kind = TitleKind.Movie, Name = id, Language = lang, Countries = countries };

    private static Dataset Ds(Dictionary<string, Title> titles, IReadOnlyList<string> home)
        => new() { Titles = titles, Users = new(StringComparer.Ordinal), HomeOrigins = new(StringComparer.Ordinal) { ["u"] = home } };

    [Fact]
    public void AFrenchComedyComesBeforeABelarusianDramaForAFrenchAccountWithoutHistory()
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal)
        {
            ["comedie"] = T("comedie", "fr", "France"), ["drame"] = T("drame", "be", "Belarus"), ["film"] = T("film", "en", "United States of America"),
            ["inconnu"] = T("inconnu", null),
        };
        var cands = new[] { "comedie", "drame", "film", "inconnu" };
        var d = Origin.Deviation(Ds(titles, new[] { "l:fr", "c:france" }), "u", Array.Empty<KeyValuePair<string, double>>(), cands, 5);
        Assert.True(d[0] > 0 && d[1] < 0, string.Join(" ", d));
        Assert.Equal(d[1], d[2], 9);       // sans historique : rien ne distingue l'anglais du biélorusse
        Assert.Equal(0, d[3]);             // origine inconnue : neutre
        Assert.Equal(0, d.Take(3).Sum(), 9);   // centré sur les candidats d'origine connue

        // mode international (aucune langue du pays) et sans historique : rien de favorisé
        Assert.All(Origin.Deviation(Ds(titles, Array.Empty<string>()), "u", Array.Empty<KeyValuePair<string, double>>(), cands, 5), x => Assert.Equal(0, x));
    }

    [Fact]
    public void WhatTheAccountWatchesOutweighsTheStartingPreference()
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal) { ["fr"] = T("fr", "fr", "France"), ["ko"] = T("ko", "ko", "South Korea") };
        for (int i = 0; i < 20; i++)
        {
            titles["k" + i] = T("k" + i, "ko", "South Korea");
        }

        var liked = Enumerable.Range(0, 20).Select(i => new KeyValuePair<string, double>("k" + i, 1.0)).ToList();
        var d = Origin.Deviation(Ds(titles, new[] { "l:fr", "c:france" }), "u", liked, new[] { "fr", "ko" }, 5);
        Assert.True(d[1] > d[0], "un compte qui aime les séries coréennes les garde devant : " + string.Join(" ", d));
    }

    [Fact]
    public void OriginKeysAreNormalizedLikeThePrototype()
    {
        Assert.Equal(new[] { "l:en", "c:france", "c:japan" }, Origin.Keys(T("x", " EN-us ", " France ", "Japan", "france ")));
        Assert.Empty(Origin.Keys(T("x", "e")));
        var be = Regions.LanguagesOf(new RegionGuess(new[] { "BE" }, false, false, Array.Empty<RegionClue>()));
        Assert.Contains("fr", be);
        Assert.Contains("nl", be);
        Assert.Empty(Regions.LanguagesOf(new RegionGuess(Array.Empty<string>(), true, false, Array.Empty<RegionClue>())));
        // noms de pays de TMDb et de Jellyfin (« United States of America ») : la préférence de départ les reconnaît
        var us = Regions.HomeOrigins(new RegionGuess(new[] { "US" }, false, false, Array.Empty<RegionClue>()));
        Assert.Contains("c:united states of america", us);
        Assert.Contains("l:en", us);
        Assert.Contains("c:south korea", Regions.HomeOrigins(new RegionGuess(new[] { "KR" }, false, false, Array.Empty<RegionClue>())));
        // titre sans langue (fiches de Jellyfin) : son pays suffit
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal) { ["a"] = T("a", null, "France"), ["b"] = T("b", null, "Japan") };
        var d = Origin.Deviation(Ds(titles, Regions.HomeOrigins(new RegionGuess(new[] { "FR" }, false, false, Array.Empty<RegionClue>()))), "u",
            Array.Empty<KeyValuePair<string, double>>(), new[] { "a", "b" }, 5);
        Assert.True(d[0] > d[1]);
    }
}
