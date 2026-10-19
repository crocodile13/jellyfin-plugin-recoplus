using System;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Conversions de l'historique Jellyfin en signaux (sans dépendance à Jellyfin : testables seules).</summary>
public static class HistoryMath
{
    /// <summary>Film revu : 2 lectures = 0,5 ; 3 et plus = 1.</summary>
    public static double MovieRewatch(int plays) => plays >= 2 ? Math.Min(1.0, (plays - 1) / 2.0) : 0.0;

    /// <summary>Note explicite Jellyfin ramenée à -1..+1 : note 0-10 (5 = neutre), sinon pouce. Null si rien.</summary>
    public static double? ExplicitRating(double? rating, bool? likes)
        => rating is double r && r > 0 ? Math.Clamp((r - 5.0) / 5.0, -1.0, 1.0) : likes is bool l ? (l ? 1.0 : -1.0) : null;

    /// <summary>Avis Jellyfin Enhanced (1 à 5 étoiles, 3 = neutre) ramené à -1..+1.</summary>
    public static double ReviewRating(double stars) => Math.Clamp((stars - 3.0) / 2.0, -1.0, 1.0);

    /// <summary>
    /// Épisodes arrivés après le dernier visionnage, si la série était rattrapée à ce moment-là (au plus un épisode
    /// d'écart) ; sinon 0. <paramref name="added"/> : dates d'ajout triées.
    /// </summary>
    public static int NewSinceCaughtUp(double[]? added, double last, int played)
    {
        if (added is null || added.Length == 0 || last <= 0)
        {
            return 0;
        }

        int before = 0;
        while (before < added.Length && added[before] <= last)
        {
            before++;
        }

        int after = added.Length - before;
        return after > 0 && before > 0 && played >= before - 1 ? after : 0;
    }
}
