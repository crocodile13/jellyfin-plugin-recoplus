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
                var near = ordered.Where(id => id != a && !used.Contains(id))     // jamais le titre aimé dans sa propre rangée
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

    /// <summary>
    /// Mélange libre : place de chaque titre hors bibliothèque dans la rangée « Pour toi » = nombre de titres de la
    /// bibliothèque mieux notés que lui (une place égale au nombre de titres montrés = derrière toute la rangée, non montré).
    /// Si moins de <paramref name="minShown"/> titres hors bibliothèque trouvent une place, les mieux classés de ceux qui
    /// n'en ont pas sont ajoutés quand même, répartis régulièrement dans la rangée (jamais plus loin que leur propre place).
    /// </summary>
    /// <param name="lib">Score commun de chaque titre de la bibliothèque montré, dans l'ordre de la rangée.</param>
    /// <param name="ext">Score commun de chaque titre hors bibliothèque, du mieux au moins bien classé.</param>
    /// <param name="minShown">Nombre minimal de titres hors bibliothèque montrés (0 = classement seul).</param>
    public static int[] FreePlaces(IReadOnlyList<double> lib, IReadOnlyList<double> ext, int minShown)
    {
        int n = lib.Count;
        var after = ext.Select(e => lib.Count(x => x > e)).ToArray();
        if (n == 0)
        {
            return after;
        }

        int missing = Math.Min(Math.Max(0, minShown), ext.Count) - after.Count(a => a < n);
        if (missing <= 0)
        {
            return after;
        }

        return ForcePlaces(after, n, Enumerable.Range(0, ext.Count), missing);
    }

    /// <summary>
    /// Au plus <paramref name="max"/> titres hors bibliothèque dans la rangée : les mieux classés (ordre des indices)
    /// gardent leur place, les autres passent derrière la rangée. Garde la bibliothèque présente même quand
    /// l'exploration trouve beaucoup de titres mieux classés.
    /// </summary>
    public static int[] CapPlaces(int[] after, int libCount, int max)
    {
        int kept = 0;
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] >= 0 && after[i] < libCount && kept++ >= Math.Max(0, max))
            {
                after[i] = libCount;
            }
        }

        return after;
    }

    /// <summary>
    /// Place de force jusqu'à <paramref name="count"/> titres hors bibliothèque qui n'ont pas de place (place ≥
    /// <paramref name="libCount"/>), pris dans l'ordre <paramref name="order"/>, répartis régulièrement dans la rangée.
    /// </summary>
    public static int[] ForcePlaces(int[] after, int libCount, IEnumerable<int> order, int count)
    {
        if (libCount <= 0 || count <= 0)
        {
            return after;
        }

        var forced = order.Where(i => i >= 0 && i < after.Length && after[i] >= libCount).Distinct().Take(count).ToList();
        for (int j = 0; j < forced.Count; j++)
        {
            after[forced[j]] = Math.Clamp((int)Math.Round((j + 1.0) * libCount / (forced.Count + 1), MidpointRounding.AwayFromZero), 0, libCount - 1);
        }

        return after;
    }
}
