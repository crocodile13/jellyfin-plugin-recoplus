using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Rangées d'accueil demandées (Home Screen Sections).</summary>
public sealed class RowOptions
{
    /// <summary>Titres par rangée.</summary>
    public int Size { get; set; } = 20;

    /// <summary>« Films pour toi ».</summary>
    public bool Movies { get; set; }

    /// <summary>« Séries pour toi ».</summary>
    public bool Series { get; set; }

    /// <summary>« Nouveautés pour toi ».</summary>
    public bool New { get; set; }

    /// <summary>« Nouveautés » : arrivés depuis ce nombre de jours.</summary>
    public int NewDays { get; set; } = 30;

    /// <summary>Rangées « Parce que tu as aimé … » (0 à 3).</summary>
    public int Because { get; set; }
}

/// <summary>
/// Répartit les recommandations d'un compte entre ses rangées d'accueil sans qu'un titre apparaisse deux fois :
/// « Pour toi » d'abord, puis Nouveautés, « Parce que tu as aimé … » (des titres aimés différents, de sagas différentes),
/// Films, Séries. Les autres rangées de l'accueil (celles de Jellyfin ou d'autres plugins) ne sont pas concernées.
/// </summary>
public static class HomeRows
{
    /// <summary>Identifiants des rangées (suffixes des sections Home Screen Sections).</summary>
    public const string PourToi = "PourToi", Films = "Films", Series = "Series", Nouveautes = "Nouveautes", ParceQue = "ParceQue";

    /// <summary>Titres de réserve par rangée (droits d'accès revérifiés à l'affichage).</summary>
    public const int Spare = 6;

    /// <summary>Ressemblance minimale avec le titre aimé pour « Parce que tu as aimé … ».</summary>
    public const double MinSimilarity = 0.1;

    /// <summary>Une rangée « Parce que tu as aimé … » n'est montrée qu'avec au moins ce nombre de titres.</summary>
    public const int MinBecause = 6;

    /// <summary>Résultat : titres par rangée, et titre aimé de chaque rangée « Parce que … ».</summary>
    public sealed record Result(Dictionary<string, List<string>> Rows, Dictionary<string, string> Anchors);

    /// <param name="ctx">Contexte du calcul.</param>
    /// <param name="vec">Vecteurs de contenu.</param>
    /// <param name="user">Compte.</param>
    /// <param name="pourToi">« Pour toi » tel qu'il sera affiché (demandes en tête, puis la liste présentée).</param>
    /// <param name="ordered">Ordre de présentation des titres de la bibliothèque (liste présentée puis le reste par score).</param>
    /// <param name="sc">Score de chaque candidat.</param>
    /// <param name="o">Rangées demandées.</param>
    public static Result Allocate(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user, IReadOnlyList<string> pourToi,
        IReadOnlyList<string> ordered, IReadOnlyDictionary<string, double> sc, RowOptions o)
    {
        var T = ctx.Ds.Titles;
        int size = Math.Clamp(o.Size, 5, 60);
        int keep = size + Spare;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);

        List<string> Take(IEnumerable<string> src, int n)
        {
            var l = new List<string>();
            foreach (var id in src)
            {
                if (l.Count >= n)
                {
                    break;
                }

                if (used.Add(id))
                {
                    l.Add(id);
                }
            }

            return l;
        }

        // « Pour toi » : ce que HSS affichera, réserves comprises (aucune rangée ne les reprend)
        rows[PourToi] = Take(pourToi, keep);

        if (o.New)
        {
            double cut = ctx.Now - (Math.Clamp(o.NewDays, 1, 365) * Core.Day);
            var fresh = ordered.Where(id => T.TryGetValue(id, out var t) && (t.Added ?? 0) >= cut && !used.Contains(id)).ToList();
            rows[Nouveautes] = fresh.Count >= 4 ? Take(fresh, keep) : new List<string>();
        }

        int nb = Math.Clamp(o.Because, 0, 3);
        if (nb > 0)
        {
            double min = sc.Count > 0 ? sc.Values.Min() : 0, span = sc.Count > 0 ? sc.Values.Max() - min : 0;
            var sig = ctx.Sig.TryGetValue(user, out var su) ? su : new Dictionary<string, double>();
            var sagas = new HashSet<string>(StringComparer.Ordinal);
            int k = 0;
            foreach (var (a, _) in sig.Where(e => e.Value > 0 && T.TryGetValue(e.Key, out var t) && !t.External && vec.ContainsKey(e.Key))
                         .OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Take(15))
            {
                if (k >= nb)
                {
                    break;
                }

                var fr = Recommender.Franchise(ctx.Ds, a);
                if (fr is { } f && !sagas.Add(f.Id))
                {
                    continue; // un seul titre de départ par saga
                }

                var av = vec[a];
                var near = ordered.Where(id => !used.Contains(id))
                    .Select(id => (Id: id, Sim: av.Dot(vec[id])))
                    .Where(x => x.Sim >= MinSimilarity)
                    .OrderByDescending(x => (0.7 * x.Sim) + (0.3 * (span > 0 ? (sc[x.Id] - min) / span : 0)))
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => x.Id).ToList();
                if (near.Count < MinBecause)
                {
                    continue;
                }

                k++;
                rows[ParceQue + k] = Take(near, keep);
                anchors[ParceQue + k] = T[a].Name;
            }

            for (int i = k + 1; i <= nb; i++)
            {
                rows[ParceQue + i] = new List<string>();
            }
        }

        if (o.Movies)
        {
            rows[Films] = Take(ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Movie), keep);
        }

        if (o.Series)
        {
            rows[Series] = Take(ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Series), keep);
        }

        return new Result(rows, anchors);
    }
}
