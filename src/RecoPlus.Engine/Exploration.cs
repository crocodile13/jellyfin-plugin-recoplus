using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Exploration du catalogue hors bibliothèque (mode étendu) : choix des pistes à explorer (genres, thèmes, auteurs,
/// studios, langues) à partir des titres aimés, pages tirées au hasard du jour, fusion équilibrée des sources et bruit
/// reproductible. Tout est déterministe pour un compte et un jour : la rangée change d'un jour à l'autre, pas à chaque
/// affichage.
/// </summary>
public static class Exploration
{
    /// <summary>Empreinte stable d'une chaîne (FNV-1a 64 bits ; string.GetHashCode change à chaque démarrage).</summary>
    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }

        return Mix(h);
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Jour (UTC) d'un instant, pour les graines.</summary>
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pages à lire d'une liste triée (par note) : la page 1, puis <paramref name="count"/> − 1 pages distinctes tirées
    /// parmi 2…<paramref name="depth"/>, propres à la graine (compte, jour, liste). Triées.
    /// </summary>
    public static int[] DailyPages(string seed, int count, int depth)
    {
        count = Math.Max(1, count);
        depth = Math.Max(1, depth);
        if (count >= depth)
        {
            return Enumerable.Range(1, depth).ToArray();
        }

        var pool = Enumerable.Range(2, depth - 1).ToList();
        var picks = new List<int> { 1 };
        ulong state = Hash(seed);
        for (int i = 0; i < count - 1 && pool.Count > 0; i++)
        {
            state = Mix(state);
            int j = (int)(state % (ulong)pool.Count);
            picks.Add(pool[j]);
            pool.RemoveAt(j);
        }

        picks.Sort();
        return picks.ToArray();
    }

    /// <summary>Tirage gaussien centré réduit, reproductible pour une graine (Box-Muller).</summary>
    public static double Gaussian(string seed)
    {
        ulong a = Hash(seed), b = Mix(a);
        double u1 = ((a >> 11) + 1.0) / 9007199254740993.0;   // ]0, 1]
        double u2 = (b >> 11) / 9007199254740992.0;           // [0, 1[
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Ajoute aux scores un bruit gaussien d'écart-type <paramref name="level"/> × (écart-type des scores), propre à
    /// chaque titre et à la graine (compte, jour). 0 = aucun bruit. Les titres proches échangent leur place, un titre
    /// très moins bon ne passe pas devant un très bon.
    /// </summary>
    public static Dictionary<string, double> Noisy(IReadOnlyDictionary<string, double> scores, double level, string seed)
    {
        var res = new Dictionary<string, double>(scores, StringComparer.Ordinal);
        if (!(level > 0) || scores.Count < 2)
        {
            return res;
        }

        double mean = scores.Values.Average();
        double sd = Math.Sqrt(scores.Values.Sum(v => (v - mean) * (v - mean)) / scores.Count);
        if (!(sd > 0))
        {
            return res;
        }

        foreach (var k in scores.Keys)
        {
            res[k] = scores[k] + (level * sd * Gaussian(seed + "|" + k));
        }

        return res;
    }

    /// <summary>Une source de candidats : ses titres du plus au moins prometteur, et son poids dans la fusion.</summary>
    public sealed record Source(string Name, IReadOnlyList<string> Keys, int Weight);

    /// <summary>
    /// Fusion équilibrée des sources : à chaque tour, chaque source donne autant de titres que son poids (dans son ordre),
    /// sans doublon, jusqu'à <paramref name="budget"/> titres. Aucune source ne peut ainsi prendre toutes les places.
    /// Renvoie aussi la source retenue pour chaque titre (la première qui l'a apporté).
    /// </summary>
    public static List<(string Key, string Source)> Merge(IReadOnlyList<Source> sources, int budget)
    {
        var res = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pos = new int[sources.Count];
        bool progress = true;
        while (res.Count < budget && progress)
        {
            progress = false;
            for (int s = 0; s < sources.Count && res.Count < budget; s++)
            {
                var src = sources[s];
                int taken = 0;
                while (taken < Math.Max(1, src.Weight) && pos[s] < src.Keys.Count && res.Count < budget)
                {
                    var k = src.Keys[pos[s]++];
                    progress = true;
                    if (seen.Add(k))
                    {
                        res.Add((k, src.Name));
                        taken++;
                    }
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Pistes les plus fortes : chaque élément reçoit le poids des titres aimés qui l'ont ; on garde ceux présents dans
    /// au moins <paramref name="minTitles"/> titres, les <paramref name="max"/> plus lourds (à égalité, le plus petit
    /// identifiant).
    /// </summary>
    public static List<(T Item, double Weight)> Top<T>(IEnumerable<(IEnumerable<T> Items, double Weight)> liked, int minTitles, int max)
        where T : notnull
    {
        var w = new Dictionary<T, (double W, int N)>();
        foreach (var (items, weight) in liked)
        {
            foreach (var it in items.Distinct())
            {
                var cur = w.TryGetValue(it, out var c) ? c : (0.0, 0);
                w[it] = (cur.Item1 + weight, cur.Item2 + 1);
            }
        }

        return w.Where(kv => kv.Value.N >= Math.Max(1, minTitles))
            .OrderByDescending(kv => kv.Value.W).ThenBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .Take(Math.Max(0, max)).Select(kv => (kv.Key, kv.Value.W)).ToList();
    }

    /// <summary>
    /// Note fiable pour comparer des titres à la note TMDb : moyenne bayésienne, la note ramenée vers
    /// <paramref name="prior"/> tant que le titre a peu de votes (poids <paramref name="m"/> votes fictifs). Un 8,8 sur
    /// 4 votes vaut ~7,0 ; un 8,5 sur 20 000 votes reste ~8,5. Sans nombre de votes connu : la moyenne a priori.
    /// </summary>
    public static double ConfidentRating(double? rating, int? votes, double prior = 7.0, double m = 300)
    {
        if (rating is not double r || !(r > 0) || votes is not int v || v <= 0)
        {
            return prior;
        }

        return ((v * r) + (m * prior)) / (v + m);
    }

    /// <summary>
    /// Croisements de deux pistes (genre × thème, genre × langue…) : les <paramref name="max"/> couples dont le produit
    /// des poids est le plus fort (à égalité, l'ordre des listes). Explore ce qui est au cœur des goûts, plus précis
    /// qu'une piste seule.
    /// </summary>
    public static List<(TA A, TB B, double Weight)> Pairs<TA, TB>(IReadOnlyList<(TA Item, double Weight)> a, IReadOnlyList<(TB Item, double Weight)> b, int max)
    {
        var res = new List<(TA, TB, double, int)>();
        for (int i = 0; i < a.Count; i++)
        {
            for (int j = 0; j < b.Count; j++)
            {
                res.Add((a[i].Item, b[j].Item, a[i].Weight * b[j].Weight, (i * b.Count) + j));
            }
        }

        return res.Where(p => p.Item3 > 0).OrderByDescending(p => p.Item3).ThenBy(p => p.Item4)
            .Take(Math.Max(0, max)).Select(p => (p.Item1, p.Item2, p.Item3)).ToList();
    }

    /// <summary>
    /// Bilan par source d'une exploration : titres trouvés (après exclusion des titres déjà là, demandés ou vus), détaillés
    /// et autorisés (dans le budget de fiches), proposés (classés parmi les titres hors bibliothèque du compte) et placés
    /// dans la rangée (mélange libre ; null sinon).
    /// </summary>
    public static Dictionary<string, SourceStat> Stats(
        IReadOnlyDictionary<string, int> found, IEnumerable<string> detailed, IEnumerable<string> proposed, IEnumerable<string>? placed)
    {
        var res = new Dictionary<string, SourceStat>(StringComparer.Ordinal);
        SourceStat Of(string s) => res.TryGetValue(s, out var x) ? x : res[s] = new SourceStat { Placed = placed is null ? null : 0 };
        foreach (var (s, n) in found)
        {
            Of(s).Found = n;
        }

        foreach (var s in detailed)
        {
            Of(s).Detailed++;
        }

        foreach (var s in proposed)
        {
            Of(s).Proposed++;
        }

        foreach (var s in placed ?? Enumerable.Empty<string>())
        {
            Of(s).Placed = (Of(s).Placed ?? 0) + 1;
        }

        return res;
    }

    // Genres TMDb : identifiants différents pour les films et les séries ; correspondances entre les deux
    private static readonly Dictionary<int, int[]> TvToMovie = new()
    {
        [10759] = new[] { 28, 12 },     // Action & Adventure
        [10765] = new[] { 878, 14 },    // Science-Fiction & Fantastique
        [10768] = new[] { 10752 },      // War & Politics
        [10762] = new[] { 10751 },      // Kids → Familial
    };

    private static readonly Dictionary<int, int> MovieToTv = new()
    {
        [28] = 10759, [12] = 10759, [878] = 10765, [14] = 10765, [10752] = 10768,
    };

    private static readonly HashSet<int> MovieGenres = new() { 28, 12, 16, 35, 80, 99, 18, 10751, 14, 36, 27, 10402, 9648, 10749, 878, 53, 10752, 37 };

    private static readonly HashSet<int> TvGenres = new() { 10759, 16, 35, 80, 99, 18, 10751, 10762, 9648, 10765, 10768, 37 };

    /// <summary>
    /// Identifiants de genre TMDb à utiliser pour une sorte (« movie » / « tv ») à partir d'un genre d'un titre aimé
    /// (film ou série). Vide si le genre n'a pas d'équivalent (horreur pour les séries, actualités pour les films…).
    /// </summary>
    public static IEnumerable<int> GenreFor(int genreId, string mediaType)
    {
        if (mediaType == "tv")
        {
            if (TvGenres.Contains(genreId))
            {
                return new[] { genreId };
            }

            return MovieToTv.TryGetValue(genreId, out var t) ? new[] { t } : Array.Empty<int>();
        }

        if (MovieGenres.Contains(genreId))
        {
            return new[] { genreId };
        }

        return TvToMovie.TryGetValue(genreId, out var m) ? m : Array.Empty<int>();
    }
}

/// <summary>Bilan d'une source d'exploration pour un compte (aperçu de la configuration).</summary>
public sealed class SourceStat
{
    /// <summary>Titres trouvés par la source (hors titres déjà là, demandés ou vus).</summary>
    public int Found { get; set; }

    /// <summary>Titres retenus dans le budget de fiches, détaillés et autorisés (contrôle parental).</summary>
    public int Detailed { get; set; }

    /// <summary>Titres classés parmi les titres hors bibliothèque proposés au compte.</summary>
    public int Proposed { get; set; }

    /// <summary>Titres placés dans la rangée (mélange libre) ; null : mélange à intervalle fixe.</summary>
    public int? Placed { get; set; }
}

/// <summary>
/// Contrôle parental d'un titre hors bibliothèque, aligné sur Jellyfin et Jellyfin Enhanced : classification du pays
/// choisi seulement (pas de repli sur un autre pays), titre sans classification autorisé sauf si le compte bloque les
/// titres non classés de cette sorte, et quand le compte a une limite de sous-niveau, les DEUX plafonds s'appliquent.
/// </summary>
public static class Parental
{
    /// <summary>Décision sur la classification.</summary>
    /// <param name="score">Niveau du titre (null : non classé ou classification inconnue).</param>
    /// <param name="subScore">Sous-niveau du titre (null = 0).</param>
    /// <param name="blockUnrated">Le compte bloque les titres non classés de cette sorte.</param>
    /// <param name="maxScore">Limite du compte (null : aucune).</param>
    /// <param name="maxSubScore">Limite de sous-niveau du compte (null : aucune).</param>
    public static bool IsAllowed(int? score, int? subScore, bool blockUnrated, int? maxScore, int? maxSubScore)
    {
        if (score is null)
        {
            return !blockUnrated;
        }

        if (maxScore is null)
        {
            return true;
        }

        return maxSubScore is not null
            ? (subScore ?? 0) <= maxSubScore.Value && score.Value <= maxScore.Value
            : score.Value <= maxScore.Value;
    }

    /// <summary>
    /// Étiquettes bloquées / autorisées du compte (comparées sans tenir compte de la casse) : une étiquette bloquée parmi
    /// les mots-clés ou les genres exclut le titre ; une liste d'autorisées non vide exige un mot-clé autorisé.
    /// </summary>
    public static bool TagsAllowed(IEnumerable<string> keywords, IEnumerable<string> genres, IReadOnlyCollection<string> blocked, IReadOnlyCollection<string> allowed)
    {
        if (blocked.Count == 0 && allowed.Count == 0)
        {
            return true;
        }

        var b = new HashSet<string>(blocked.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var a = new HashSet<string>(allowed.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var kw = keywords.ToList();
        if (b.Count > 0 && (kw.Any(b.Contains) || genres.Any(b.Contains)))
        {
            return false;
        }

        return a.Count == 0 || kw.Any(a.Contains);
    }
}
