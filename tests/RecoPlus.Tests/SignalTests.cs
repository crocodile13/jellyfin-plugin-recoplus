using System;
using System.Collections.Generic;
using System.Linq;
using RecoPlus.Engine;
using Xunit;

namespace RecoPlus.Tests;

/// <summary>Signaux explicites et implicites ajoutés en 1.0.4 : favoris partiels, revisionnage, notes, masqués, liste de suivi.</summary>
public class SignalTests
{
    private const double Now = 1_790_000_000;
    private static readonly EngineSettings S = new();

    private static Interaction Series(int eps, double idleDays = 1) => new()
    {
        Title = "s", Episodes = eps, Played = eps > 0, Completion = eps / 20.0,
        Last = eps > 0 ? Now - (idleDays * Core.Day) : 0, Start = eps > 0 ? Now - ((idleDays + 1) * Core.Day) : 0,
    };

    private static double Sig(Interaction x, TitleKind k = TitleKind.Series, int n = 20) => Core.Signal(x, k, n, Now, S);

    [Fact]
    public void EpisodeLikedIsNotSeriesLiked()
    {
        var plain = Sig(Series(8));
        var oneEpisode = Series(8); oneEpisode.FavEpisodes = 1;
        var threeEpisodes = Series(8); threeEpisodes.FavEpisodes = 3;
        var tenEpisodes = Series(8); tenEpisodes.FavEpisodes = 10;
        var season = Series(8); season.FavSeasons = 1;
        var series = Series(8); series.Favorite = true;
        var both = Series(8); both.FavSeasons = 2; both.FavEpisodes = 6;

        double d1 = Sig(oneEpisode) - plain, d3 = Sig(threeEpisodes) - plain, d10 = Sig(tenEpisodes) - plain;
        double ds = Sig(season) - plain, dSer = Sig(series) - plain, dBoth = Sig(both) - plain;
        Assert.True(d1 > 0 && d1 < d3, "un épisode aimé compte un peu, trois davantage");
        Assert.True(Math.Abs(d3 - d10) < 1e-12, "plafonné à 3 épisodes");
        Assert.True(d3 < ds && ds < dSer, "épisodes < saison < série");
        Assert.True(dBoth <= dSer + 1e-12, "saisons + épisodes ne dépassent jamais une série en favori");
        Assert.Equal(1.0, Core.FavoritePart(series, S));
        Assert.Equal(S.FavSeasonShare, Core.FavoritePart(season, S));
        Assert.Equal(S.FavEpisodeShare / 3, Core.FavoritePart(oneEpisode, S), 12);
    }

    [Fact]
    public void EpisodeFavoriteAloneMakesAnUnwatchedSeriesALike()
    {
        var x = new Interaction { Title = "s", FavEpisodes = 1, Last = Now - Core.Day };
        Assert.True(Sig(x) > 0);
    }

    [Fact]
    public void RewatchStrengthensALikeButNeverRescuesAnAbandon()
    {
        var on = new EngineSettings { RewatchBoost = 0.5 };
        var once = new Interaction { Title = "m", Played = true, Completion = 1, Last = Now - Core.Day, Start = Now - (2 * Core.Day), Plays = 1 };
        var twice = once.Copy(); twice.Rewatch = 1.0; twice.Plays = 2;
        Assert.Equal(Core.Signal(once, TitleKind.Movie, 0, Now, on) + (on.RewatchBoost * Math.Max(on.RecencyFloor, Math.Pow(0.5, 1 / on.HalfLifeDays))),
            Core.Signal(twice, TitleKind.Movie, 0, Now, on), 9);
        Assert.Equal(Sig(once, TitleKind.Movie), Sig(twice, TitleKind.Movie)); // coupé par défaut

        var abandoned = new Interaction { Title = "m", Completion = 0.4, Last = Now - (60 * Core.Day), Start = Now - (61 * Core.Day), Rewatch = 1 };
        Assert.Equal(-on.AbandonWeight, Core.Signal(abandoned, TitleKind.Movie, 0, Now, on));
    }

    [Fact]
    public void ExplicitRatingWinsInBothDirections()
    {
        var watched = new Interaction { Title = "m", Played = true, Completion = 1, Last = Now, Start = Now - Core.Day };
        var disliked = watched.Copy(); disliked.Rating = -1;
        Assert.Equal(-S.RatingWeight, Sig(disliked, TitleKind.Movie));

        var abandoned = new Interaction { Title = "m", Completion = 0.4, Last = Now - (60 * Core.Day), Start = Now - (61 * Core.Day), Rating = 1 };
        Assert.True(Sig(abandoned, TitleKind.Movie) > 0, "noté 5/5 malgré un arrêt : c'est la note qui compte");

        var neutral = watched.Copy(); neutral.Rating = 0;
        Assert.Equal(Sig(watched, TitleKind.Movie), Sig(neutral, TitleKind.Movie));
    }

    [Fact]
    public void HiddenIsAStrongNegativeWhateverElse()
    {
        var x = new Interaction { Title = "m", Played = true, Completion = 1, Favorite = true, Rating = 1, Last = Now, Hidden = true };
        Assert.Equal(-S.HiddenWeight, Sig(x, TitleKind.Movie));
    }

    [Fact]
    public void WatchlistIsAWeakerShortTermIntent()
    {
        var w = new Interaction { Title = "m", Watchlisted = Now - Core.Day };
        var r = new Interaction { Title = "m", Requested = Now - Core.Day };
        Assert.True(Sig(w, TitleKind.Movie) > 0 && Sig(w, TitleKind.Movie) < Sig(r, TitleKind.Movie));
        var old = new Interaction { Title = "m", Watchlisted = Now - ((S.RequestMaxDays + 1) * Core.Day) };
        Assert.Equal(0, Sig(old, TitleKind.Movie));
        var both = new Interaction { Title = "m", Watchlisted = Now - Core.Day, Requested = Now - (80 * Core.Day) };
        Assert.Equal(Math.Max(Sig(w, TitleKind.Movie), Sig(new Interaction { Title = "m", Requested = Now - (80 * Core.Day) }, TitleKind.Movie)), Sig(both, TitleKind.Movie));
        var watched = new Interaction { Title = "m", Watchlisted = Now - Core.Day, Played = true, Completion = 1, Last = Now };
        Assert.Equal(Sig(new Interaction { Title = "m", Played = true, Completion = 1, Last = Now }, TitleKind.Movie), Sig(watched, TitleKind.Movie));
    }

    [Fact]
    public void QualitySourceAndMissingValues()
    {
        Title T(double? c, double? r) => new() { Id = "x", Kind = TitleKind.Movie, Community = c, Critic = r };
        Assert.Equal(7.0, Recommender.Quality(T(7, 90), RatingSource.Community));
        Assert.Equal(9.0, Recommender.Quality(T(7, 90), RatingSource.Critic));
        Assert.Equal(7.0, Recommender.Quality(T(7, null), RatingSource.Critic), 12);
        Assert.Equal(9.0, Recommender.Quality(T(7, 90), RatingSource.Best));
        Assert.Equal(8.0, Recommender.Quality(T(7, 90), RatingSource.Mean));
        Assert.Equal(6.5, Recommender.Quality(T(null, null), RatingSource.Mean));
        Assert.Equal(6.5, Recommender.Quality(T(0, 0), RatingSource.Best));
    }

    [Fact]
    public void FewVotesMeanLittleConfidenceInTheRating()
    {
        Title T(double c, int? votes) => new() { Id = "x", Kind = TitleKind.Movie, Community = c, Votes = votes };
        Assert.Equal(9.0, Recommender.Quality(T(9, null), RatingSource.Community, 150));          // votes inconnus : note brute
        Assert.Equal(9.0, Recommender.Quality(T(9, 20000), RatingSource.Community, 0));           // 0 = note brute
        double few = Recommender.Quality(T(9, 12), RatingSource.Community, 150);
        double many = Recommender.Quality(T(9, 20000), RatingSource.Community, 150);
        Assert.True(few < 6.8 && few > 6.5, "9 sur 12 votes ≈ 6,7");
        Assert.True(many > 8.98, "9 sur 20 000 votes ≈ 9");
        Assert.Equal(6.5, Recommender.Quality(T(9, 0), RatingSource.Community, 150));
    }

    [Fact]
    public void HouseholdReviewsAreShrunkAndExcludeTheAccountItself()
    {
        var ds = new Dataset
        {
            Titles = new(), Users = new(),
            Reviews = new(StringComparer.Ordinal)
            {
                ["a"] = new(StringComparer.Ordinal) { ["u1"] = 5 },
                ["b"] = new(StringComparer.Ordinal) { ["u1"] = 5, ["u2"] = 5, ["u3"] = 5, ["u4"] = 5, ["u5"] = 5 },
                ["c"] = new(StringComparer.Ordinal) { ["me"] = 1 },
                ["d"] = new(StringComparer.Ordinal) { ["u1"] = 1, ["u2"] = 1 },
            },
        };
        double one = Recommender.Household(ds, "a", "me", 2), five = Recommender.Household(ds, "b", "me", 2);
        Assert.Equal(((5 + 6) / 3.0 - 3) / 2, one, 12);
        Assert.True(one > 0 && one < five && five < 1, "un avis pèse peu, cinq concordants beaucoup");
        Assert.Equal(0.0, Recommender.Household(ds, "c", "me", 2));                               // son propre avis ne compte pas ici
        Assert.True(Recommender.Household(ds, "d", "me", 2) < 0);
        Assert.Equal(0.0, Recommender.Household(ds, "zzz", "me", 2));
        Assert.Equal(1.0, Recommender.Household(ds, "a", "me", 0));                               // sans a priori : moyenne brute
    }

    [Fact]
    public void LanguageAndCountryTokensOnlyWhenWeighted()
    {
        var t = new Title { Id = "x", Kind = TitleKind.Series, Language = " JA ", Countries = new[] { "Japan", "France", "Korea", "Taiwan" } };
        var off = Core.Tokens(t, S);
        Assert.False(off.Keys.Any(k => k.StartsWith("l:", StringComparison.Ordinal) || k.StartsWith("n:", StringComparison.Ordinal)));
        var on = Core.Tokens(t, new EngineSettings { FeatLanguage = 0.5, FeatCountry = 0.3 });
        Assert.Equal(0.5, on["l:ja"]);
        Assert.Equal(new[] { "n:France", "n:Japan", "n:Korea" }, on.Keys.Where(k => k.StartsWith("n:", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SeriesWeightFollowsWatchTime()
    {
        var anime = Series(150); anime.Hours = 150 * 0.25;
        var drama = Series(150); drama.Hours = 150 * 0.75;
        var short3 = Series(10); short3.Hours = 10 * 0.25;
        var big = new EngineSettings { SeriesMaxWeight = 2 };
        Assert.True(Core.Signal(anime, TitleKind.Series, 200, Now, big) < Core.Signal(drama, TitleKind.Series, 200, Now, big), "150 × 15 min < 150 × 45 min");
        Assert.True(Sig(short3) < Sig(Series(10)), "10 épisodes de 15 min < 10 de 45 min (durée inconnue = 45 min)");
        Assert.Equal(Sig(anime), Sig(drama));                                            // plafonnées à 1 par défaut
        var byEp = new EngineSettings { SeriesByTime = false };
        Assert.Equal(Core.Signal(anime, TitleKind.Series, 200, Now, byEp), Core.Signal(drama, TitleKind.Series, 200, Now, byEp));
    }

    [Fact]
    public void UnknownDatesCountAsOldTastesAndBulkMarksAreDetected()
    {
        var h = new List<Interaction>
        {
            new() { Title = "nodate", Played = true, Completion = 1 },
            new() { Title = "real", Played = true, Completion = 1, Start = Now - (10 * Core.Day), Last = Now - (10 * Core.Day) },
        };
        for (int i = 0; i < 5; i++)
        {
            h.Add(new Interaction { Title = "bulk" + i, Played = true, Completion = 1, Start = Now - (100 * Core.Day) + (20 * i), Last = Now - (100 * Core.Day) + (20 * i) });
        }

        h.Add(new Interaction { Title = "three", Played = true, Completion = 1, Start = Now - (50 * Core.Day), Last = Now - (50 * Core.Day) });
        h.Add(new Interaction { Title = "three2", Played = true, Completion = 1, Start = Now - (50 * Core.Day) + 60, Last = Now - (50 * Core.Day) + 60 });
        Assert.Equal(6, Core.MarkBackfill(h));
        Assert.True(h.Single(x => x.Title == "nodate").Backfill);
        Assert.All(h.Where(x => x.Title.StartsWith("bulk", StringComparison.Ordinal)), x => Assert.True(x.Backfill));
        Assert.False(h.Single(x => x.Title == "real").Backfill);
        Assert.False(h.Single(x => x.Title == "three").Backfill);                       // 2 titres rapprochés : pas un marquage en masse
        var nd = h.Single(x => x.Title == "nodate");
        Assert.Equal(S.RecencyFloor, Sig(nd, TitleKind.Movie), 12);                       // goût ancien, pas tout frais
    }

    [Fact]
    public void BackfillMatchesThePrototype()
    {
        var g = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "golden.json"))).RootElement;
        var ds = ParityTests.LoadDataset(g.GetProperty("dataset"));
        foreach (var h in ds.Users.Values)
        {
            var want = h.Values.Select(x => x.Backfill).ToArray();
            Assert.Contains(true, want.Concat(new[] { true }));
            foreach (var x in h.Values)
            {
                x.Backfill = false;
            }

            Core.MarkBackfill(h.Values);
            Assert.Equal(want, h.Values.Select(x => x.Backfill).ToArray());
        }
    }
}
