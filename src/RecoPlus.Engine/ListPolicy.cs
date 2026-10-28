using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Réglages de présentation des listes, appliqués APRÈS le classement : ils ne changent pas le moteur
/// (ni son évaluation, ni le calibrage), seulement ce qui est montré.
/// </summary>
public sealed class ListOptions
{
    /// <summary>Haut de liste concerné par le hasard et les minimums ; 0 = automatique : la taille des rangées d'accueil.</summary>
    public int Window { get; set; }

    /// <summary>Part du haut de liste tirée au hasard parmi tous les titres proposables (0-50 %).</summary>
    public double RandomPercent { get; set; }

    /// <summary>Minimum de films dans le haut de liste.</summary>
    public int MinMovies { get; set; }

    /// <summary>Minimum de séries dans le haut de liste.</summary>
    public int MinSeries { get; set; }

    /// <summary>Genres jamais proposés (insensible à la casse et aux accents).</summary>
    public List<string> ExcludedGenres { get; set; } = new();

    /// <summary>Note minimale de la communauté (0-10) ; 0 = pas de filtre. Un titre sans note passe.</summary>
    public double MinCommunityRating { get; set; }

    /// <summary>Année de sortie minimale ; 0 = pas de filtre. Un titre sans année passe.</summary>
    public int MinYear { get; set; }

    /// <summary>Copie ramenée dans un domaine sûr.</summary>
    /// <param name="rowSize">Taille des rangées d'accueil : haut de liste quand il est automatique (0).</param>
    public ListOptions Sanitized(int rowSize = 20)
    {
        var w = Math.Clamp(Window > 0 ? Window : rowSize, 5, 100);
        var movies = Math.Clamp(MinMovies, 0, w);
        var series = Math.Clamp(MinSeries, 0, w);
        if (movies + series > w)
        {
            // ex. 15 + 15 sur 20 : partage au prorata, films d'abord pour l'arrondi
            movies = (int)Math.Round(w * movies / (double)(movies + series), MidpointRounding.AwayFromZero);
            series = w - movies;
        }

        return new ListOptions
        {
            Window = w,
            RandomPercent = double.IsFinite(RandomPercent) ? Math.Clamp(RandomPercent, 0, 50) : 0,
            MinMovies = movies,
            MinSeries = series,
            ExcludedGenres = (ExcludedGenres ?? new()).Select(Normalize).Where(g => g.Length > 0).Distinct().ToList(),
            MinCommunityRating = double.IsFinite(MinCommunityRating) ? Math.Clamp(MinCommunityRating, 0, 10) : 0,
            MinYear = Math.Clamp(MinYear, 0, 3000),
        };
    }

    /// <summary>« Science-Fiction », « science fiction », « Science‑fiction » → « science fiction ».</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var b = new System.Text.StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            b.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', b.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Filtres et mise en forme des listes (hasard reproductible, minimums de films et de séries).</summary>
public static class ListPolicy
{
    /// <summary>Le titre passe les filtres (genres exclus, note minimale, année minimale).</summary>
    public static bool Allows(Title t, ListOptions o)
    {
        if (o.ExcludedGenres.Count > 0 && t.Genres.Any(g => o.ExcludedGenres.Contains(ListOptions.Normalize(g))))
        {
            return false;
        }

        if (o.MinCommunityRating > 0 && t.Community is double c && c > 0 && c < o.MinCommunityRating)
        {
            return false;
        }

        return o.MinYear <= 0 || t.Year is not int y || y >= o.MinYear;
    }

    /// <summary>Graine stable pour un compte et un jour : la liste ne change pas à chaque affichage, mais d'un jour à l'autre.</summary>
    public static ulong Seed(string user, DateTime utc)
    {
        // FNV-1a 64 bits : string.GetHashCode change à chaque démarrage
        ulong h = 14695981039346656037UL;
        foreach (var ch in user + "|" + utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
        {
            h ^= ch;
            h *= 1099511628211UL;
        }

        return h;
    }

    /// <summary>
    /// Met en forme une liste classée : <paramref name="ranked"/> garde sa longueur et son ordre au-delà du haut de liste.
    /// 1) hasard : une part du haut de liste est remplacée par des titres tirés dans <paramref name="pool"/> (tout ce qui
    ///    est proposable), à des places tirées elles aussi ; 2) minimums : s'il manque des films ou des séries dans le
    ///    haut de liste, les mieux classés de la sorte manquante remontent à la place des moins bien classés de l'autre.
    /// </summary>
    public static List<string> Shape(IReadOnlyList<string> ranked, IReadOnlyList<string> pool, Func<string, TitleKind?> kind, ListOptions o, ulong seed)
    {
        var list = ranked.Distinct(StringComparer.Ordinal).ToList();
        int w = Math.Min(o.Window, list.Count);
        if (w == 0)
        {
            return list;
        }

        var rng = new SplitMix(seed);

        // 1) hasard
        int nRandom = (int)Math.Round(w * o.RandomPercent / 100.0, MidpointRounding.AwayFromZero);
        if (nRandom > 0)
        {
            var top = new HashSet<string>(list.Take(w), StringComparer.Ordinal);
            var candidates = pool.Where(id => !top.Contains(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            nRandom = Math.Min(nRandom, candidates.Count);
            var picks = Sample(candidates, nRandom, rng);
            var slots = Sample(Enumerable.Range(0, w).ToList(), nRandom, rng).OrderBy(i => i).ToList();

            // les titres déplacés glissent juste après le haut de liste, dans leur ordre
            var displaced = slots.Select(i => list[i]).ToList();
            for (int k = 0; k < slots.Count; k++)
            {
                list[slots[k]] = picks[k];
            }

            var picked = new HashSet<string>(picks, StringComparer.Ordinal);
            var rest = list.Skip(w).Where(id => !picked.Contains(id)).ToList();
            list = list.Take(w).Concat(displaced).Concat(rest).ToList();
        }

        // 2) minimums
        Ensure(list, w, TitleKind.Movie, o.MinMovies, kind, pool);
        Ensure(list, w, TitleKind.Series, o.MinSeries, kind, pool);
        return list;
    }

    private static void Ensure(List<string> list, int w, TitleKind want, int min, Func<string, TitleKind?> kind, IReadOnlyList<string> pool)
    {
        int have = list.Take(w).Count(id => kind(id) == want);
        if (have >= min)
        {
            return;
        }

        var inList = new HashSet<string>(list, StringComparer.Ordinal);
        var donors = list.Skip(w).Where(id => kind(id) == want)
            .Concat(pool.Where(id => !inList.Contains(id) && kind(id) == want))
            .Distinct(StringComparer.Ordinal).Take(min - have).ToList();
        foreach (var d in donors)
        {
            // remplace le dernier titre de l'autre sorte dans le haut de liste ; il glisse juste après
            int victim = -1;
            for (int i = w - 1; i >= 0; i--)
            {
                if (kind(list[i]) != want)
                {
                    victim = i;
                    break;
                }
            }

            if (victim < 0)
            {
                return;
            }

            var out_ = list[victim];
            list.Remove(d);
            list.RemoveAt(victim);
            list.Insert(Math.Min(w - 1, list.Count), d);
            list.Insert(Math.Min(w, list.Count), out_);
        }
    }

    private static List<T> Sample<T>(List<T> from, int n, SplitMix rng)
    {
        var a = from.ToList();
        for (int i = 0; i < n && i < a.Count; i++)
        {
            int j = i + (int)(rng.Next() % (ulong)(a.Count - i));
            (a[i], a[j]) = (a[j], a[i]);
        }

        return a.Take(n).ToList();
    }

    /// <summary>Générateur reproductible (SplitMix64), indépendant de l'implémentation de System.Random.</summary>
    private sealed class SplitMix
    {
        private ulong _s;

        public SplitMix(ulong seed) => _s = seed;

        public ulong Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
