using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Accord TMDb pour les titres de la bibliothèque : les « recommandations » (et « similaires ») que TMDb publie pour
/// chaque titre, tirées de l'activité de tous ses utilisateurs, servent de « vus ensemble » venu de l'extérieur — ce
/// que les quelques comptes d'un serveur ne peuvent pas apprendre seuls, et qui existe dès l'arrivée d'un titre.
/// <para>
/// Pour un compte, un candidat B reçoit, de chaque titre aimé A dont la liste TMDb contient B, le poids du goût pour A
/// multiplié par une décroissance selon la place de B dans la liste (1/√(1 + place)) ; la somme est rapportée à la somme
/// des poids (0..1). Un titre recommandé partout (succès planétaire) est atténué comme un mot trop fréquent (IDF) :
/// log(1 + N / df) / log(1 + N), N = listes connues, df = listes qui le contiennent.
/// </para>
/// </summary>
public static class TmdbSignal
{
    /// <summary>Atténuation de chaque titre selon le nombre de listes qui le citent (1 = jamais cité ailleurs).</summary>
    public static Dictionary<string, double> Damping(IEnumerable<IReadOnlyList<string>> lists)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        int n = 0;
        foreach (var l in lists)
        {
            n++;
            foreach (var id in l.Distinct(StringComparer.Ordinal))
            {
                df[id] = df.GetValueOrDefault(id) + 1;
            }
        }

        double den = Math.Log(1.0 + n);
        return df.ToDictionary(kv => kv.Key, kv => den > 0 ? Math.Log(1.0 + ((double)n / kv.Value)) / den : 1.0, StringComparer.Ordinal);
    }

    /// <summary>
    /// Accord de chaque titre cité : somme pondérée par le goût et la place, rapportée à la somme des poids, atténuée.
    /// </summary>
    /// <param name="anchors">Titres aimés du compte : poids (signal positif) et liste TMDb ordonnée (titres de la bibliothèque).</param>
    /// <param name="damping">Atténuation par titre (<see cref="Damping"/>) ; absente = 1.</param>
    public static Dictionary<string, double> Agreement(IReadOnlyList<(double Weight, IReadOnlyList<string> Related)> anchors,
        IReadOnlyDictionary<string, double>? damping = null)
    {
        var res = new Dictionary<string, double>(StringComparer.Ordinal);
        double tot = anchors.Where(a => a.Weight > 0 && double.IsFinite(a.Weight)).Sum(a => a.Weight);
        if (tot <= 0)
        {
            return res;
        }

        foreach (var (w, rel) in anchors)
        {
            if (!(w > 0) || !double.IsFinite(w))
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rel.Count; i++)
            {
                if (seen.Add(rel[i]))
                {
                    res[rel[i]] = res.GetValueOrDefault(rel[i]) + (w / Math.Sqrt(1.0 + i));
                }
            }
        }

        foreach (var id in res.Keys.ToList())
        {
            double d = damping is not null && damping.TryGetValue(id, out var dv) ? dv : 1.0;
            res[id] = Math.Clamp(res[id] / tot * d, 0.0, 1.0);
        }

        return res;
    }
}
