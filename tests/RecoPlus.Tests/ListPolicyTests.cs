using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

public class ListPolicyTests
{
    // m00..m59 : films, s00..s59 : séries
    private static TitleKind? Kind(string id) => id[0] == 'm' ? TitleKind.Movie : id[0] == 's' ? TitleKind.Series : null;

    private static List<string> Ids(char k, int n, int from = 0) => Enumerable.Range(from, n).Select(i => $"{k}{i:00}").ToList();

    private static readonly DateTime Day = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DefaultsLeaveTheListUntouched()
    {
        var ranked = Ids('m', 30).Concat(Ids('s', 10)).ToList();
        var pool = ranked.Concat(Ids('m', 20, 30)).ToList();
        var o = new ListOptions().Sanitized();
        Assert.Equal(ranked, ListPolicy.Shape(ranked, pool, Kind, o, 1));
    }

    [Fact]
    public void RandomShareIsExactStableForADayAndChangesTheNextDay()
    {
        var ranked = Ids('m', 40);
        var pool = ranked.Concat(Ids('s', 50)).ToList();
        var o = new ListOptions { RandomPercent = 25 }.Sanitized();
        var a = ListPolicy.Shape(ranked, pool, Kind, o, ListPolicy.Seed("u1", Day));
        var b = ListPolicy.Shape(ranked, pool, Kind, o, ListPolicy.Seed("u1", Day.AddHours(10)));
        var c = ListPolicy.Shape(ranked, pool, Kind, o, ListPolicy.Seed("u1", Day.AddDays(1)));
        var d = ListPolicy.Shape(ranked, pool, Kind, o, ListPolicy.Seed("u2", Day));

        Assert.Equal(a, b);
        Assert.False(a.SequenceEqual(c), "un autre jour, un autre tirage");
        Assert.False(a.SequenceEqual(d), "un autre compte, un autre tirage");
        Assert.Equal(5, a.Take(20).Count(id => !ranked.Take(20).Contains(id))); // 25 % de 20
        Assert.Equal(a.Count, a.Distinct().Count());
        Assert.Equal(ranked.Count + a.Count(id => !ranked.Contains(id)), a.Count); // rien de retiré, seulement des ajouts
        // les titres déplacés restent juste après le haut de liste, dans l'ordre du goût
        var displaced = ranked.Take(20).Where(id => !a.Take(20).Contains(id)).ToList();
        Assert.Equal(displaced, a.Skip(20).Take(displaced.Count).ToList());
        // le reste du classement n'est pas perdu
        Assert.True(ranked.All(a.Contains));
    }

    [Fact]
    public void RandomDoesNotExceedThePool()
    {
        var ranked = Ids('m', 20);
        var pool = ranked.Concat(Ids('m', 2, 20)).ToList();
        var r = ListPolicy.Shape(ranked, pool, Kind, new ListOptions { RandomPercent = 50 }.Sanitized(), 7);
        Assert.Equal(22, r.Count);
        Assert.Equal(2, r.Take(20).Count(id => !ranked.Contains(id)));
    }

    [Fact]
    public void MinimumsPromoteTheBestRankedOfTheMissingKind()
    {
        var ranked = Ids('m', 25).Concat(new[] { "s00", "s01", "s02", "s03" }).ToList();
        var o = new ListOptions { MinSeries = 3 }.Sanitized();
        var r = ListPolicy.Shape(ranked, ranked, Kind, o, 1);
        var top = r.Take(20).ToList();
        Assert.Equal(3, top.Count(id => id[0] == 's'));
        Assert.Contains("s00", top);
        Assert.Contains("s01", top);
        Assert.Contains("s02", top);
        // les films évincés sont les moins bien classés du haut de liste, et restent juste après
        Assert.Equal(Ids('m', 17), top.Where(id => id[0] == 'm').ToList());
        Assert.Equal(new[] { "m17", "m18", "m19" }, r.Skip(20).Take(3).ToList());
        Assert.Equal(r.Count, r.Distinct().Count());
    }

    [Fact]
    public void MinimumsReachIntoThePoolWhenTheListHasTooFew()
    {
        var ranked = Ids('m', 30);
        var pool = ranked.Concat(Ids('s', 5)).ToList();
        var r = ListPolicy.Shape(ranked, pool, Kind, new ListOptions { MinSeries = 2 }.Sanitized(), 1);
        Assert.Equal(new[] { "s00", "s01" }, r.Take(20).Where(id => id[0] == 's').ToList());
    }

    [Fact]
    public void MinimumsHoldAfterRandom()
    {
        var ranked = Ids('m', 40).Concat(Ids('s', 3)).ToList();
        var pool = ranked.Concat(Ids('m', 40, 40)).ToList();
        for (ulong seed = 0; seed < 50; seed++)
        {
            var r = ListPolicy.Shape(ranked, pool, Kind, new ListOptions { RandomPercent = 50, MinSeries = 3 }.Sanitized(), seed);
            Assert.Equal(3, r.Take(20).Count(id => id[0] == 's'));
            Assert.Equal(r.Count, r.Distinct().Count());
        }
    }

    [Fact]
    public void ImpossibleMinimumsAreShared()
    {
        var o = new ListOptions { Window = 20, MinMovies = 15, MinSeries = 15 }.Sanitized();
        Assert.Equal(20, o.MinMovies + o.MinSeries);
        Assert.Equal(10, o.MinMovies);
        var p = new ListOptions { RandomPercent = 400, MinYear = -3 }.Sanitized();
        Assert.Equal(50.0, p.RandomPercent);
        Assert.Equal(0, p.MinYear);
    }

    [Fact]
    public void FiltersIgnoreCaseAndAccentsAndLetUnknownValuesThrough()
    {
        var o = new ListOptions { ExcludedGenres = new() { " Documentaire ", "science-fiction" }, MinCommunityRating = 6, MinYear = 1980 }.Sanitized();
        Title T(string g, double? c, int? y) => new() { Id = "x", Kind = TitleKind.Movie, Genres = new[] { g }, Community = c, Year = y };
        Assert.False(ListPolicy.Allows(T("DOCUMENTAIRE", 8, 2000), o));
        Assert.False(ListPolicy.Allows(T("Science Fiction", 8, 2000), o));
        Assert.False(ListPolicy.Allows(T("Drame", 5.9, 2000), o));
        Assert.False(ListPolicy.Allows(T("Drame", 8, 1979), o));
        Assert.True(ListPolicy.Allows(T("Drame", null, null), o));
        Assert.True(ListPolicy.Allows(T("Drame", 6, 1980), o));
        Assert.Equal("science fiction", ListOptions.Normalize("Science‑Fiction"));
        Assert.Equal("comedie", ListOptions.Normalize("Comédie"));
    }
}
