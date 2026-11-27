using System;
using System.Collections.Generic;
using System.Globalization;

namespace RecoPlus.Engine;

/// <summary>
/// Nouvelles sorties : un film est nouveau dans un pays quand il y est sorti récemment, en salle, en vidéo à la demande ou
/// à la télévision (types TMDb 2 limitée, 3 salles, 4 numérique, 6 télévision ; pas l'avant-première ni le support
/// physique, qui suit). Sans date pour ce pays, la date de sortie principale de TMDb. Une série est nouvelle quand sa
/// première diffusion est récente (TMDb ne donne pas de date par pays pour les séries).
/// </summary>
public static class Releases
{
    /// <summary>Types de sortie TMDb retenus : limitée, salles, numérique, télévision.</summary>
    public static readonly int[] Types = { 2, 3, 4, 6 };

    /// <summary>Date « aaaa-mm-jj » (ou plus longue, ISO) en secondes Unix UTC ; null si illisible.</summary>
    public static double? Parse(string? date)
        => date is { Length: >= 10 } && DateTime.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeSeconds()
            : null;

    /// <summary>
    /// Date de sortie d'un titre pour un compte : en mode international, la date principale (première sortie dans le
    /// monde) ; sinon la plus ancienne de ses pays (la sortie la plus tôt chez l'un d'eux), et la date principale pour
    /// un pays sans date connue.
    /// </summary>
    public static double? For(RegionGuess region, IReadOnlyDictionary<string, string>? local, string? primary)
    {
        if (region.International || region.Countries.Count == 0)
        {
            return Parse(primary);
        }

        double? best = null;
        foreach (var c in region.Countries)
        {
            if (In(c, local, primary) is { } d && (best is null || d < best))
            {
                best = d;
            }
        }

        return best;
    }

    /// <summary>Date de sortie d'un titre dans un pays : sa première sortie retenue là-bas, sinon la date principale.</summary>
    public static double? In(string country, IReadOnlyDictionary<string, string>? local, string? primary)
        => local is not null && local.TryGetValue(country, out var d) && Parse(d) is { } t ? t : Parse(primary);

    /// <summary>Âge d'un titre en jours à la date <paramref name="now"/> (négatif : pas encore sorti) ; null sans date lisible.</summary>
    public static double? AgeDays(string? date, double now) => Parse(date) is { } t ? (now - t) / Core.Day : null;

    /// <summary>
    /// Assez de votes TMDb pour l'âge du titre : les votes s'accumulent avec le temps (relevé TMDb : un film de moins d'un
    /// mois a 9 votes en médiane, une série récente 2 à 7). Le minimum complet vaut à partir de <paramref name="rampDays"/>
    /// jours, proportionnellement moins avant ; un titre pas encore sorti n'en a pas besoin. Sans date : le minimum complet.
    /// </summary>
    public static bool EnoughVotes(int votes, double? ageDays, int minVotes, double rampDays = 30)
        => minVotes <= 0 || votes >= minVotes * (ageDays is double a ? Math.Clamp(a / rampDays, 0, 1) : 1);

    /// <summary>
    /// Note TMDb fiable d'une sortie récente : ramenée vers la note médiane des sorties récentes (6,8 au relevé) tant
    /// qu'elle repose sur peu de votes (poids de 10 votes fictifs) : 9,0 sur 8 votes ≈ 7,8 ; 5,5 sur 25 votes ≈ 5,9.
    /// </summary>
    public static double ConfidentRating(double rating, int votes) => Exploration.ConfidentRating(rating, votes, 6.8, 10);

    /// <summary>Sorti depuis au plus <paramref name="days"/> jours (et pas dans le futur) à la date <paramref name="now"/>.</summary>
    public static bool IsNew(double? released, double now, int days)
        => released is { } r && r <= now && r >= now - (Math.Clamp(days, 1, 365) * Core.Day);
}
