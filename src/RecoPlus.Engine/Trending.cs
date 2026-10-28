using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Sortes de titres d'une rangée d'accueil.</summary>
public enum KindFilter
{
    /// <summary>Films et séries.</summary>
    Both,

    /// <summary>Films seulement.</summary>
    Movies,

    /// <summary>Séries seulement.</summary>
    Series,

    /// <summary>Films et séries, en deux rangées séparées.</summary>
    Split
}

/// <summary>
/// Tendances TMDb du moment (listes « trending » de TMDb, lues via Seerr) : intensité de chaque titre, et ordre de la
/// rangée « Tendances » d'un compte. Fonctions pures.
/// </summary>
public static class Trending
{
    /// <summary>Le titre convient à la sorte demandée.</summary>
    public static bool Allows(KindFilter k, TitleKind kind) => k switch
    {
        KindFilter.Movies => kind == TitleKind.Movie,
        KindFilter.Series => kind == TitleKind.Series,
        _ => true,
    };

    /// <summary>
    /// Intensité de chaque titre des listes de tendances (chaque liste dans l'ordre de TMDb) : 1 / √(1 + place), la place
    /// comptée dans sa liste (films et séries ont chacun leur premier). Un titre présent dans deux listes garde la plus
    /// forte. Décroissance continue : le 1er vaut 1, le 4e 0,5, le 25e 0,2.
    /// </summary>
    public static Dictionary<string, double> Scores(params IReadOnlyList<string>[] lists)
    {
        var res = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var l in lists)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int place = 0;
            foreach (var id in l)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                double v = 1.0 / Math.Sqrt(1.0 + place++);
                if (!res.TryGetValue(id, out var cur) || v > cur)
                {
                    res[id] = v;
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Ordre de la rangée « Tendances » pour un compte : mélange de la tendance et du goût du compte, tous deux ramenés à
    /// 0..1 parmi les titres de la rangée. <paramref name="tasteShare"/> = 0 : l'ordre de TMDb ; 1 : le goût seul (parmi les
    /// tendances). À égalité, la tendance la plus forte, puis l'identifiant (ordre stable).
    /// </summary>
    /// <param name="ids">Titres en tendance proposables au compte.</param>
    /// <param name="trend">Intensité de chaque titre (<see cref="Scores"/>).</param>
    /// <param name="taste">Score du compte pour chaque titre (absent = le plus bas).</param>
    /// <param name="tasteShare">Part du goût, 0..1.</param>
    public static List<string> Order(IEnumerable<string> ids, IReadOnlyDictionary<string, double> trend,
        IReadOnlyDictionary<string, double> taste, double tasteShare)
    {
        var l = ids.Distinct(StringComparer.Ordinal).ToList();
        double a = double.IsFinite(tasteShare) ? Math.Clamp(tasteShare, 0.0, 1.0) : 0.0;
        var tv = l.Select(id => taste.TryGetValue(id, out var v) && double.IsFinite(v) ? v : double.NaN).ToList();
        var known = tv.Where(double.IsFinite).ToList();
        double lo = known.Count > 0 ? known.Min() : 0, span = known.Count > 0 ? known.Max() - lo : 0;
        double Norm(double v) => double.IsNaN(v) ? 0.0 : span > 0 ? (v - lo) / span : 1.0;
        return l.Select((id, i) => (Id: id, Tr: trend.GetValueOrDefault(id), Key: ((1.0 - a) * trend.GetValueOrDefault(id)) + (a * Norm(tv[i]))))
            .OrderByDescending(x => x.Key).ThenByDescending(x => x.Tr).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id).ToList();
    }
}

/// <summary>
/// Accueil d'un compte (liste ordonnée des sections de Home Screen Sections) quand une rangée est séparée en deux
/// (films, séries) ou réunie : la nouvelle forme prend la place de l'ancienne. Fonction pure.
/// </summary>
public static class SplitLayout
{
    /// <summary>
    /// Met <paramref name="sections"/> en accord avec le réglage de <paramref name="baseRow"/> :
    /// <list type="bullet">
    /// <item>le compte n'affiche aucune forme de la rangée : rien ne change (il ne l'a pas voulue) ;</item>
    /// <item>il n'affiche que des formes à jour (par exemple une seule moitié) : rien ne change (choix du compte) ;</item>
    /// <item>il affiche une forme périmée seule : remplacée, à sa place, par la forme actuelle (une rangée ou ses deux
    /// moitiés) ;</item>
    /// <item>il affiche les deux : les formes périmées sont retirées.</item>
    /// </list>
    /// </summary>
    /// <returns>La liste a changé.</returns>
    public static bool Apply(List<string> sections, string prefix, string baseRow, KindFilter kind)
    {
        var all = HomeRows.Forms(baseRow).Select(x => prefix + x).ToHashSet(StringComparer.Ordinal);
        var want = HomeRows.Expand(baseRow, kind).Select(x => prefix + x).ToList();
        bool Stale(string s) => all.Contains(s) && !want.Contains(s, StringComparer.Ordinal);
        if (!sections.Any(Stale))
        {
            return false;
        }

        if (sections.Any(s => want.Contains(s, StringComparer.Ordinal)))
        {
            sections.RemoveAll(Stale);
            return true;
        }

        int at = sections.FindIndex(Stale);
        sections.RemoveAll(Stale);
        sections.InsertRange(at, want);
        return true;
    }
}
