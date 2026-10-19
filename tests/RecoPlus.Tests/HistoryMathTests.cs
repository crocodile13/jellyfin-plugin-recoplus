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
}
