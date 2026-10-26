using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RecoPlus.Engine;

/// <summary>
/// Sagas du moteur, reconstruites à partir de trois indices, parce que les collections de Jellyfin seules ne suffisent pas :
/// <list type="bullet">
/// <item>la collection TMDb de chaque film (fiable, à jour même quand Jellyfin n'a pas rangé le nouveau volet) ;</item>
/// <item>les collections de Jellyfin, sauf celles qui mélangent plusieurs collections TMDb (filmographie d'un réalisateur,
/// « Marvel ») : celles-là restent des regroupements thématiques ;</item>
/// <item>le nom : un titre dont le nom commence par celui d'un autre titre de même langue d'origine, plus récent, avec un
/// studio ou un auteur en commun (« Overlord » → « Overlord : The Sacred Kingdom », la série puis le film).</item>
/// </list>
/// Fonctions pures : le plugin les applique à la bibliothèque avant chaque calcul, le moteur ne voit que le résultat.
/// </summary>
public static class Sagas
{
    /// <summary>Longueur minimale (caractères, après normalisation) d'un nom de départ pour la règle du nom.</summary>
    public const int MinStemLength = 4;

    /// <summary>Résultat : sagas (identifiant → titres dans l'ordre de sortie) et regroupements thématiques.</summary>
    public sealed record Result(Dictionary<string, List<string>> Collections, HashSet<string> Thematic);

    /// <summary>
    /// Reconstruit les collections du moteur.
    /// </summary>
    /// <param name="titles">Titres connus (bibliothèque et hors bibliothèque).</param>
    /// <param name="boxsets">Collections d'origine : celles de Jellyfin, et « tmdbc:N » pour les titres hors bibliothèque.</param>
    /// <param name="tmdbCollection">Collection TMDb de chaque titre qui en a une (titre → identifiant numérique).</param>
    /// <param name="sagaMax">Au-delà, une collection d'origine est un regroupement thématique (EngineSettings.SagaMax).</param>
    public static Result Refine(IReadOnlyDictionary<string, Title> titles, IReadOnlyDictionary<string, List<string>> boxsets,
        IReadOnlyDictionary<string, int> tmdbCollection, int sagaMax = 12)
    {
        // collection TMDb de chaque titre : donnée directe, ou déduite d'un « tmdbc:N » d'origine
        var tc = new Dictionary<string, int>(tmdbCollection, StringComparer.Ordinal);
        foreach (var (cid, items) in boxsets)
        {
            if (cid.StartsWith("tmdbc:", StringComparison.Ordinal) && int.TryParse(cid.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                foreach (var id in items)
                {
                    tc.TryAdd(id, n);
                }
            }
        }

        var uf = new UnionFind();
        var label = new Dictionary<string, string>(StringComparer.Ordinal); // titre → identifiant proposé pour sa saga

        void Label(string id, string cid)
        {
            if (!label.TryGetValue(id, out var cur) || Prefer(cid, cur))
            {
                label[id] = cid;
            }
        }

        // 1) collections TMDb
        foreach (var g in tc.Where(kv => titles.ContainsKey(kv.Key)).GroupBy(kv => kv.Value))
        {
            var ids = g.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var id in ids)
            {
                uf.Union(ids[0], id);
                Label(id, "tmdbc:" + g.Key.ToString(CultureInfo.InvariantCulture));
            }
        }

        // 2) règle du nom (avant les collections de Jellyfin : elle sert à juger leur cohérence)
        var stems = titles.Values.Select(t => (T: t, S: Normalize(t.Name))).Where(x => x.S.Length >= MinStemLength)
            .OrderBy(x => x.S, StringComparer.Ordinal).ThenBy(x => x.T.Id, StringComparer.Ordinal).ToList();
        var nameLinks = new List<(string Base, string Next)>();
        for (int i = 0; i < stems.Count; i++)
        {
            var (b, bs) = stems[i];
            // les noms qui commencent par « bs » suivent immédiatement dans l'ordre alphabétique
            for (int j = i + 1; j < stems.Count && stems[j].S.StartsWith(bs, StringComparison.Ordinal); j++)
            {
                var (c, cs) = stems[j];
                if (cs.Length > bs.Length && cs[bs.Length] == ' ' && Continues(b, c, tc))
                {
                    nameLinks.Add((b.Id, c.Id));
                }
            }
        }

        foreach (var (b, c) in nameLinks)
        {
            uf.Union(b, c);
            Label(b, "name:" + b);
            Label(c, "name:" + b);
        }

        // 3) collections d'origine : cohérentes → saga ; mélangées → regroupement thématique
        var thematic = new HashSet<string>(StringComparer.Ordinal);
        var kept = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (cid, items) in boxsets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ids = items.Where(titles.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count < 2 || cid.StartsWith("tmdbc:", StringComparison.Ordinal))
            {
                continue; // déjà pris en compte par la collection TMDb
            }

            // trop grande ou mélangée : regroupement thématique, jamais fusionnée avec une saga
            if (ids.Count <= sagaMax && Coherent(ids, tc))
            {
                foreach (var id in ids)
                {
                    uf.Union(ids[0], id);
                    Label(id, cid);
                }
            }
            else
            {
                thematic.Add(cid);
                kept[cid] = ids;
            }
        }

        // composantes : une saga par groupe d'au moins deux titres, dans l'ordre de sortie
        var colls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var g in label.Keys.GroupBy(uf.Find, StringComparer.Ordinal))
        {
            var ids = g.ToList();
            if (ids.Count < 2)
            {
                continue;
            }

            string cid = ids.Select(id => label[id]).Aggregate((a, b) => Prefer(b, a) ? b : a);
            ids.Sort((a, b) => Order(titles[a], titles[b]));
            colls[cid] = ids;
        }

        foreach (var (cid, ids) in kept)
        {
            if (!colls.ContainsKey(cid))
            {
                colls[cid] = ids;
            }
        }

        return new Result(colls, thematic);
    }

    /// <summary>
    /// Suite dont le début n'est pas dans la bibliothèque : mot-clé TMDb « sequel » et aucune saga connue. À montrer après
    /// le reste (le compte risque de ne pas connaître le début).
    /// </summary>
    public static bool IsOrphanSequel(Dataset ds, string id)
        => ds.Titles.TryGetValue(id, out var t) && t.Tags.Any(x => string.Equals(x, "sequel", StringComparison.OrdinalIgnoreCase))
           && !(Recommender.Franchise(ds, id) is { } f && !ds.Thematic.Contains(f.Id));

    /// <summary>Saga (et non regroupement thématique) : taille au plus <paramref name="sagaMax"/>, collection non mélangée.</summary>
    public static bool IsSaga(Dataset ds, (int Size, string Id) f, int sagaMax) => f.Size <= sagaMax && !ds.Thematic.Contains(f.Id);

    /// <summary>Ordre de sortie : date de sortie, sinon année, puis nom et identifiant (stable).</summary>
    public static int Order(Title a, Title b)
    {
        int c = Key(a).CompareTo(Key(b));
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static double Key(Title t) => t.Released ?? (t.Year is int y ? new DateTimeOffset(y, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() : double.MaxValue);

    /// <summary>Minuscules sans accents, ponctuation remplacée par des espaces simples (« HAIKYÛ !! » → « haikyu »).</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = true;
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// « c » continue « b » : même langue d'origine, pas plus ancien, un studio ou un auteur en commun, et TMDb ne dit pas
    /// le contraire (deux films dont l'un au moins a une collection TMDb ne sont reliés que s'ils ont la même : « Dune »
    /// de 1984 ne précède pas « Dune : Deuxième partie »). Une série n'a pas de collection TMDb : série → film reste permis.
    /// </summary>
    internal static bool Continues(Title b, Title c, IReadOnlyDictionary<string, int> tc)
    {
        if (b.Id == c.Id || (b.Language is { } lb && c.Language is { } lc && !string.Equals(lb, lc, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (b.Kind == TitleKind.Movie && c.Kind == TitleKind.Movie)
        {
            bool hb = tc.TryGetValue(b.Id, out var cb), hc = tc.TryGetValue(c.Id, out var cc);
            if ((hb || hc) && !(hb && hc && cb == cc))
            {
                return false;
            }
        }

        if (b.Year is int yb && c.Year is int yc && yc < yb)
        {
            return false;
        }

        static IEnumerable<string> Authors(Title t) => new[] { "Director", "Creator", "Writer" }
            .SelectMany(r => t.People.TryGetValue(r, out var l) ? l : Array.Empty<string>());
        var sb = new HashSet<string>(b.Studios, StringComparer.OrdinalIgnoreCase);
        var ab = new HashSet<string>(Authors(b), StringComparer.OrdinalIgnoreCase);
        return c.Studios.Any(sb.Contains) || Authors(c).Any(ab.Contains);
    }

    // une collection d'origine n'est écartée des sagas que sur preuve d'un mélange : ses titres appartiennent à plusieurs
    // collections TMDb (« Marvel », filmographie d'un réalisateur). Sans cette preuve elle reste une saga : la prendre à
    // tort pour une saga coûte peu (un seul de ses titres à la fois), l'inverse ramène les suites avant leur début.
    private static bool Coherent(List<string> ids, Dictionary<string, int> tc)
        => ids.Where(tc.ContainsKey).Select(id => tc[id]).Distinct().Count() <= 1;

    // identifiant de saga préféré : collection TMDb, puis collection Jellyfin, puis nom ; à égalité, l'ordre alphabétique
    private static bool Prefer(string a, string b)
    {
        static int Rank(string x) => x.StartsWith("tmdbc:", StringComparison.Ordinal) ? 0 : x.StartsWith("name:", StringComparison.Ordinal) ? 2 : 1;
        int ra = Rank(a), rb = Rank(b);
        return ra != rb ? ra < rb : string.CompareOrdinal(a, b) < 0;
    }

    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _p = new(StringComparer.Ordinal);

        public string Find(string x)
        {
            if (!_p.TryGetValue(x, out var p))
            {
                _p[x] = x;
                return x;
            }

            if (p == x)
            {
                return x;
            }

            var r = Find(p);
            _p[x] = r;
            return r;
        }

        public void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                if (string.CompareOrdinal(ra, rb) < 0)
                {
                    _p[rb] = ra;
                }
                else
                {
                    _p[ra] = rb;
                }
            }
        }
    }
}
