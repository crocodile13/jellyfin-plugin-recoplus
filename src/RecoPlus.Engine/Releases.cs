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

    /// <summary>Date de sortie d'un titre dans un pays : sa première sortie retenue là-bas, sinon la date principale.</summary>
    public static double? In(string country, IReadOnlyDictionary<string, string>? local, string? primary)
        => local is not null && local.TryGetValue(country, out var d) && Parse(d) is { } t ? t : Parse(primary);

    /// <summary>Sorti depuis au plus <paramref name="days"/> jours (et pas dans le futur) à la date <paramref name="now"/>.</summary>
    public static bool IsNew(double? released, double now, int days)
        => released is { } r && r <= now && r >= now - (Math.Clamp(days, 1, 365) * Core.Day);
}
