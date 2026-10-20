using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Xunit;

namespace RecoPlus.Tests;

public class HistoryMathTests
{
    [Fact]
    public void RewatchAndExplicitRatings()
    {
        Assert.Equal(0.0, HistoryMath.MovieRewatch(0));
        Assert.Equal(0.0, HistoryMath.MovieRewatch(1));
        Assert.Equal(0.5, HistoryMath.MovieRewatch(2));
        Assert.Equal(1.0, HistoryMath.MovieRewatch(3));
        Assert.Equal(1.0, HistoryMath.MovieRewatch(40));

        Assert.Null(HistoryMath.ExplicitRating(null, null));
        Assert.Equal(1.0, HistoryMath.ExplicitRating(10, null));
        Assert.Equal(-0.6, HistoryMath.ExplicitRating(2, true)!.Value, 12);   // la note prime sur le pouce
        Assert.Equal(-1.0, HistoryMath.ExplicitRating(null, false));
        Assert.Equal(1.0, HistoryMath.ExplicitRating(0, true));               // note 0 = absente

        Assert.Equal(-1.0, HistoryMath.ReviewRating(1));
        Assert.Equal(0.0, HistoryMath.ReviewRating(3));
        Assert.Equal(0.75, HistoryMath.ReviewRating(4.5));
    }

    [Fact]
    public void NewEpisodesOnlyForACaughtUpSeries()
    {
        var added = new double[] { 10, 20, 30, 40, 100, 110 };
        Assert.Equal(2, HistoryMath.NewSinceCaughtUp(added, 50, 4));   // 4 épisodes avant le dernier visionnage, tous vus
        Assert.Equal(2, HistoryMath.NewSinceCaughtUp(added, 50, 3));   // un épisode d'écart toléré
        Assert.Equal(0, HistoryMath.NewSinceCaughtUp(added, 50, 2));   // pas rattrapée : c'est « en cours », pas une nouveauté
        Assert.Equal(0, HistoryMath.NewSinceCaughtUp(added, 200, 6));  // rien de nouveau
        Assert.Equal(0, HistoryMath.NewSinceCaughtUp(added, 0, 6));
        Assert.Equal(0, HistoryMath.NewSinceCaughtUp(null, 50, 4));
        Assert.Equal(0, HistoryMath.NewSinceCaughtUp(added, 5, 0));    // tout est arrivé après : jamais commencée
    }

    [Fact]
    public void OldPlaybackReportingFilesAreReattachedByName()
    {
        Guid U = Guid.NewGuid(), movie = Guid.NewGuid(), oldMovie = Guid.NewGuid(), series = Guid.NewGuid(), oldEp = Guid.NewGuid(), twin = Guid.NewGuid();
        var known = new HashSet<Guid> { movie, series };
        var movies = HistoryMath.NameIndex(new[] { (movie, "Le Voyage de Chihiro"), (twin, "Dune"), (Guid.NewGuid(), "Dune") });
        var seriesIdx = HistoryMath.NameIndex(new[] { (series, "The Office") });
        Assert.Null(movies["dune"]);                                                  // deux films « Dune » : ambigu
        var rows = new[]
        {
            new PlaybackStart(U, movie, "Movie", "Le Voyage de Chihiro", 2000),
            new PlaybackStart(U, oldMovie, "Movie", "le voyage de  CHIHIRO", 1000),   // ancien fichier, remplacé depuis
            new PlaybackStart(U, oldEp, "Episode", "The Office - s01e01 - Pilot", 500),
            new PlaybackStart(U, Guid.NewGuid(), "Movie", "Dune", 10),                // ambigu : ignoré
            new PlaybackStart(U, Guid.NewGuid(), "Audio", "Artiste - Titre (Album)", 10),
            new PlaybackStart(U, Guid.NewGuid(), "Movie", "Film inconnu", 10),
        };
        var map = HistoryMath.MapPlaybackStarts(rows, known.Contains, movies, seriesIdx, out var remapped);
        Assert.Equal(1000, map[(U, movie)]);                                          // date la plus ancienne, ancien fichier compris
        Assert.Equal(500, map[(U, series)]);
        Assert.Equal(2, remapped);
        Assert.Equal(2, map.Count);
        Assert.Equal("ecole des sorciers", HistoryMath.NormalizeName("  École   des Sorciers "));
    }

    [Fact]
    public void WatchedHoursUseRealDurations()
    {
        long Min(double m) => (long)(m * 60 * 10_000_000);
        Assert.Equal(150 * 0.25, HistoryMath.WatchedHours(Enumerable.Repeat((long?)Min(15), 150), null), 6);
        Assert.Equal(150 * 0.75, HistoryMath.WatchedHours(Enumerable.Repeat((long?)Min(45), 150), null), 6);
        Assert.Equal(0.5 + 0.4, HistoryMath.WatchedHours(new long?[] { Min(30), null }, 0.4), 6);   // durée inconnue : moyenne de la série
        Assert.Equal(0.75, HistoryMath.WatchedHours(new long?[] { null }, null), 6);
    }
}
