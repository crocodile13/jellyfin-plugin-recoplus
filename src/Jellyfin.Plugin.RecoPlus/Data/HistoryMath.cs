using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Une ligne de Playback Reporting : premier lancement d'un élément par un compte (<paramref name="At"/>) et dernier
/// (<paramref name="Last"/>, 0 si inconnu), avec son type et son nom.
/// </summary>
public readonly record struct PlaybackStart(Guid User, Guid Item, string Type, string Name, double At, double Last = 0);

/// <summary>Conversions de l'historique Jellyfin en signaux (sans dépendance à Jellyfin : testables seules).</summary>
public static partial class HistoryMath
{
    [GeneratedRegex(@"^(?<series>.+?) - s\d+e\d+(?: - .*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeName();

    /// <summary>Nom comparable : minuscules, sans accents ni espaces superflus.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var d = name.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Premiers lancements, y compris ceux d'éléments qui ne sont plus dans la bibliothèque (fichier remplacé par
    /// Radarr/Sonarr : nouvel identifiant Jellyfin) : rattachés au titre actuel par leur nom (film : son nom ;
    /// épisode « Série - s01e02 - … » : la série), seulement si un seul titre porte ce nom. Clé : (compte, élément ou titre).
    /// </summary>
    /// <param name="known">L'élément existe encore (film, série ou épisode de la bibliothèque).</param>
    /// <param name="moviesByName">Nom normalisé → film (null si plusieurs films portent ce nom).</param>
    /// <param name="seriesByName">Nom normalisé → série (null si ambigu).</param>
    public static Dictionary<(Guid User, Guid Item), double> MapPlaybackStarts(IEnumerable<PlaybackStart> rows, Func<Guid, bool> known,
        IReadOnlyDictionary<string, Guid?> moviesByName, IReadOnlyDictionary<string, Guid?> seriesByName, out int remapped)
        => Map(rows, known, moviesByName, seriesByName, r => r.At, first: true, out remapped);

    /// <summary>
    /// Dernières lectures réelles (même rattachement que <see cref="MapPlaybackStarts"/>) : la date d'un visionnage,
    /// que « marquer comme vu » ne réécrit pas (contrairement à la date de dernière lecture de Jellyfin).
    /// </summary>
    public static Dictionary<(Guid User, Guid Item), double> MapPlaybackLasts(IEnumerable<PlaybackStart> rows, Func<Guid, bool> known,
        IReadOnlyDictionary<string, Guid?> moviesByName, IReadOnlyDictionary<string, Guid?> seriesByName)
        => Map(rows, known, moviesByName, seriesByName, r => Math.Max(r.At, r.Last), first: false, out _);

    private static Dictionary<(Guid User, Guid Item), double> Map(IEnumerable<PlaybackStart> rows, Func<Guid, bool> known,
        IReadOnlyDictionary<string, Guid?> moviesByName, IReadOnlyDictionary<string, Guid?> seriesByName, Func<PlaybackStart, double> date, bool first, out int remapped)
    {
        var res = new Dictionary<(Guid, Guid), double>();
        remapped = 0;
        void Put(Guid u, Guid i, double at)
        {
            if (!res.TryGetValue((u, i), out var cur) || (first ? at < cur : at > cur))
            {
                res[(u, i)] = at;
            }
        }

        foreach (var r in rows)
        {
            if (r.At <= 0)
            {
                continue;
            }

            if (known(r.Item))
            {
                Put(r.User, r.Item, date(r));
                continue;
            }

            Guid? target = null;
            if (string.Equals(r.Type, "Movie", StringComparison.OrdinalIgnoreCase))
            {
                target = moviesByName.GetValueOrDefault(NormalizeName(r.Name));
            }
            else if (string.Equals(r.Type, "Episode", StringComparison.OrdinalIgnoreCase) && EpisodeName().Match(r.Name ?? string.Empty) is { Success: true } m)
            {
                target = seriesByName.GetValueOrDefault(NormalizeName(m.Groups["series"].Value));
            }

            if (target is Guid t)
            {
                Put(r.User, t, date(r));
                remapped++;
            }
        }

        return res;
    }

    /// <summary>Index nom normalisé → identifiant ; un nom porté par plusieurs titres est ambigu (null).</summary>
    public static Dictionary<string, Guid?> NameIndex(IEnumerable<(Guid Id, string Name)> titles)
    {
        var idx = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var (id, name) in titles)
        {
            var k = NormalizeName(name);
            if (k.Length == 0)
            {
                continue;
            }

            idx[k] = idx.ContainsKey(k) ? null : id;
        }

        return idx;
    }

    /// <summary>Heures regardées d'une série : durée de chaque épisode vu, sinon la durée moyenne connue de la série, sinon 45 min.</summary>
    public static double WatchedHours(IEnumerable<long?> playedRuntimeTicks, double? seriesMeanHours)
    {
        double h = 0;
        foreach (var t in playedRuntimeTicks)
        {
            h += t is long v && v > 0 ? v / 36_000_000_000.0 : seriesMeanHours ?? global::RecoPlus.Engine.Core.DefaultEpisodeHours;
        }

        return h;
    }

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
