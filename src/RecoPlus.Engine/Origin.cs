using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Origine d'un titre (langue originale, pays de production) et familiarité d'un compte avec elle : la part de ses titres
/// aimés qui ont cette origine, plus une préférence de départ pour la langue de son pays (<see cref="Dataset.HomeOrigins"/>
/// : <see cref="EngineSettings.OriginPrior"/> titres fictifs, aucun en mode international). Un titre prend la familiarité de
/// son origine la plus familière ; l'ingrédient est cet écart à la moyenne des candidats : une comédie française remonte
/// un peu pour un compte français, un drame d'un pays dont il ne regarde jamais rien descend un peu, et un compte qui
/// regarde beaucoup de séries coréennes garde les séries coréennes. Même calcul dans le prototype (parité).
/// </summary>
public static class Origin
{
    /// <summary>Origines d'un titre : « l:fr » (langue originale, deux lettres) et « c:france » (pays de production).</summary>
    public static List<string> Keys(Title t)
    {
        var res = new List<string>();
        var lang = (t.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (lang.Length >= 2)
        {
            res.Add("l:" + lang[..2]);
        }

        foreach (var c in t.Countries)
        {
            var k = "c:" + (c ?? string.Empty).Trim().ToLowerInvariant();
            if (k.Length > 2 && !res.Contains(k))
            {
                res.Add(k);
            }
        }

        return res;
    }

    /// <summary>
    /// Écart de familiarité de chaque candidat à la moyenne des candidats d'origine connue (0 pour un candidat sans origine
    /// connue, ou sans aucune familiarité possible).
    /// </summary>
    /// <param name="liked">Titres aimés du compte et leur poids (signal positif).</param>
    public static double[] Deviation(Dataset ds, string user, IEnumerable<KeyValuePair<string, double>> liked, IReadOnlyList<string> cands, double prior)
    {
        var h = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0;
        foreach (var (t, s) in liked)
        {
            if (!ds.Titles.TryGetValue(t, out var title))
            {
                continue;
            }

            var keys = Keys(title);
            if (keys.Count == 0)
            {
                continue;
            }

            total += s;
            foreach (var k in keys)
            {
                h[k] = h.GetValueOrDefault(k) + s;
            }
        }

        var homes = (ds.HomeOrigins.GetValueOrDefault(user) ?? Array.Empty<string>())
            .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant()).Where(x => x.Length > 2).Distinct(StringComparer.Ordinal).ToList();
        double p = Math.Max(0.0, prior);
        double denom = total + (homes.Count > 0 ? p : 0.0);
        var res = new double[cands.Count];
        if (!(denom > 0))
        {
            return res;
        }

        // chaque origine du pays (ses langues, son nom) reçoit la préférence entière : la France a aussi le breton ou
        // l'occitan, la Suisse quatre langues ; partagée, la préférence du français s'y diluerait
        double Fam(string k) => (h.GetValueOrDefault(k) + (homes.Contains(k) ? p : 0.0)) / denom;
        var f = new double?[cands.Count];
        double sum = 0;
        int n = 0;
        for (int i = 0; i < cands.Count; i++)
        {
            if (ds.Titles.TryGetValue(cands[i], out var t) && Keys(t) is { Count: > 0 } keys)
            {
                double best = 0;
                foreach (var k in keys)
                {
                    best = Math.Max(best, Fam(k));
                }

                f[i] = best;
                sum += best;
                n++;
            }
        }

        double mean = n > 0 ? sum / n : 0.0;
        for (int i = 0; i < cands.Count; i++)
        {
            res[i] = f[i] is double v ? v - mean : 0.0;
        }

        return res;
    }
}
