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

/// <summary>Contenu d'une rangée d'accueil.</summary>
public enum RowSource
{
    /// <summary>Titres de la bibliothèque seulement.</summary>
    Local,

    /// <summary>Titres hors bibliothèque (Seerr) seulement.</summary>
    Seerr,

    /// <summary>Les deux, mélangés.</summary>
    Mix
}

/// <summary>Façon de mélanger bibliothèque et hors bibliothèque dans une rangée.</summary>
public sealed class MixOptions
{
    /// <summary>Libre (classement commun du moteur) ; sinon à intervalle fixe.</summary>
    public bool Free { get; set; }

    /// <summary>Intervalle fixe : un titre hors bibliothèque toutes les N cartes.</summary>
    public int Every { get; set; } = 3;

    /// <summary>Libre : nombre minimal de titres hors bibliothèque montrés.</summary>
    public int MinFree { get; set; }

    /// <summary>Libre : nombre maximal de titres hors bibliothèque placés d'eux-mêmes (0 = aucune limite).</summary>
    public int MaxFree { get; set; }

    /// <summary>Libre : places « découverte » (les mieux notés qui n'ont pas trouvé de place).</summary>
    public int Discovery { get; set; }
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

    /// <summary>Résultat : titres par rangée, et titre aimé (nom, identifiant) de chaque rangée « Parce que … ».</summary>
    public sealed record Result(Dictionary<string, List<string>> Rows, Dictionary<string, string> Anchors)
    {
        /// <summary>Identifiant du titre aimé de chaque rangée « Parce que … ».</summary>
        public Dictionary<string, string> AnchorIds { get; init; } = new(StringComparer.Ordinal);
    }

    /// <param name="ctx">Contexte du calcul.</param>
    /// <param name="vec">Vecteurs de contenu.</param>
    /// <param name="user">Compte.</param>
    /// <param name="pourToi">« Pour toi » tel qu'il sera affiché (demandes en tête, puis la liste présentée).</param>
    /// <param name="ordered">Ordre de présentation des titres de la bibliothèque (liste présentée puis le reste par score).</param>
    /// <param name="sc">Score de chaque candidat.</param>
    /// <param name="o">Rangées demandées.</param>
    /// <param name="fit">
    /// Titres hors bibliothèque : appelé pour chaque rangée remplie (identifiant, titre aimé des rangées « Parce que … »,
    /// ses titres de la bibliothèque dans la limite de sa taille), renvoie combien de ces titres restent (les premiers) ;
    /// une valeur négative ou ≥ au nombre reçu ne change rien. Les titres qui sortent redeviennent libres pour les rangées
    /// suivantes ; quelques-uns restent en réserve à la fin de la rangée, sans y être réservés (secours de l'affichage).
    /// </param>
    public static Result Allocate(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user, IReadOnlyList<string> pourToi,
        IReadOnlyList<string> ordered, IReadOnlyDictionary<string, double> sc, RowOptions o, Func<string, string?, IReadOnlyList<string>, int>? fit = null)
    {
        var T = ctx.Ds.Titles;
        int size = Math.Clamp(o.Size, 5, 60);
        int keep = size + Spare;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchorIds = new Dictionary<string, string>(StringComparer.Ordinal);

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

        List<string> Row(string id, string? anchor, IEnumerable<string> src)
        {
            var l = Take(src, keep);
            if (fit is null || l.Count == 0)
            {
                return l;
            }

            var shown = l.Take(size).ToList();
            int k = fit(id, anchor, shown);
            if (k < 0 || k >= shown.Count)
            {
                return l;
            }

            foreach (var x in l.Skip(k))
            {
                used.Remove(x);
            }

            return l.Take(k).Concat(l.Skip(k).Take(Spare)).ToList();
        }

        // « Pour toi » : ce que HSS affichera, réserves comprises (aucune rangée ne les reprend)
        rows[PourToi] = Row(PourToi, null, pourToi);

        if (o.New)
        {
            double cut = ctx.Now - (Math.Clamp(o.NewDays, 1, 365) * Core.Day);
            var fresh = ordered.Where(id => T.TryGetValue(id, out var t) && (t.Added ?? 0) >= cut && !used.Contains(id)).ToList();
            rows[Nouveautes] = fresh.Count >= 4 ? Row(Nouveautes, null, fresh) : new List<string>();
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
                rows[ParceQue + k] = Row(ParceQue + k, a, near);
                anchors[ParceQue + k] = T[a].Name;
                anchorIds[ParceQue + k] = a;
            }

            for (int i = k + 1; i <= nb; i++)
            {
                rows[ParceQue + i] = new List<string>();
            }
        }

        if (o.Movies)
        {
            rows[Films] = Row(Films, null, ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Movie));
        }

        if (o.Series)
        {
            rows[Series] = Row(Series, null, ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Series));
        }

        return new Result(rows, anchors) { AnchorIds = anchorIds };
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
    /// Rangée finale d'un mélange libre, qui garde sa taille (<paramref name="libCount"/> cartes) : chaque titre hors
    /// bibliothèque placé (place &lt; libCount) s'insère après ses <c>after</c> premiers titres de la bibliothèque, puis la
    /// rangée est coupée à sa taille. Les titres hors bibliothèque coupés repassent à « non placé » (libCount) ; renvoie
    /// aussi le nombre de titres de la bibliothèque qui restent (les premiers, dans l'ordre).
    /// </summary>
    public static (int[] After, int KeptLibrary) FitRow(int[] after, int libCount) => FitRow(after, libCount, libCount);

    /// <summary>Comme <see cref="FitRow(int[], int)"/>, pour une rangée de <paramref name="size"/> cartes.</summary>
    public static (int[] After, int KeptLibrary) FitRow(int[] after, int libCount, int size)
    {
        var res = (int[])after.Clone();
        var order = Enumerable.Range(0, after.Length).Where(i => after[i] >= 0 && after[i] < libCount)
            .OrderBy(i => after[i]).ThenBy(i => i).ToList();
        var kept = new HashSet<int>();
        int total = 0, lib = 0, k = 0;
        for (int l = 0; l <= libCount && total < size; l++)
        {
            while (k < order.Count && after[order[k]] == l && total < size)
            {
                kept.Add(order[k++]);
                total++;
            }

            if (l < libCount && total < size)
            {
                lib++;
                total++;
            }
        }

        for (int i = 0; i < res.Length; i++)
        {
            if (res[i] >= libCount || (res[i] >= 0 && !kept.Contains(i)))
            {
                res[i] = libCount;
            }
        }

        return (res, lib);
    }

    /// <summary>
    /// Place les titres hors bibliothèque d'une rangée. Renvoie, pour chacun, le nombre de titres de la bibliothèque qui le
    /// précèdent (≥ <c>lib.Count</c> = pas dans la rangée), et le nombre de titres de la bibliothèque qui restent (les
    /// premiers) ; la rangée garde sa taille (<paramref name="size"/> cartes au plus).
    /// Seerr : les titres hors bibliothèque seuls, dans l'ordre. Mélange à intervalle fixe : un toutes les N cartes. Mélange
    /// libre : chacun après les titres de la bibliothèque mieux classés que lui (<see cref="FreePlaces"/>), minimum,
    /// maximum et places découverte compris.
    /// </summary>
    /// <param name="lib">Clé de classement de chaque titre de la bibliothèque de la rangée, dans l'ordre.</param>
    /// <param name="ext">Clé de classement de chaque titre hors bibliothèque, du mieux au moins bien classé.</param>
    /// <param name="source">Contenu de la rangée (Local : rien n'est placé).</param>
    /// <param name="o">Façon de mélanger.</param>
    /// <param name="discovery">Mélange libre : ordre de préférence pour les places découverte (indices de <paramref name="ext"/>).</param>
    /// <param name="size">Cartes de la rangée.</param>
    public static (int[] After, int KeptLibrary) PlaceRow(IReadOnlyList<double> lib, IReadOnlyList<double> ext, RowSource source,
        MixOptions o, IEnumerable<int>? discovery, int size)
    {
        int n = lib.Count;
        size = Math.Max(0, size);
        if (source == RowSource.Local || ext.Count == 0 || size == 0)
        {
            return (Enumerable.Repeat(n, ext.Count).ToArray(), Math.Min(n, size));
        }

        if (source == RowSource.Seerr)
        {
            // les titres hors bibliothèque seuls (aucun titre de la bibliothèque devant eux)
            return (Enumerable.Range(0, ext.Count).Select(i => i < size ? 0 : n).ToArray(), 0);
        }

        int[] after;
        if (o.Free)
        {
            after = FreePlaces(lib, ext, Math.Clamp(o.MinFree, 0, 60));
            if (o.MaxFree > 0)
            {
                after = CapPlaces(after, n, Math.Min(o.MaxFree, 60));
            }

            after = ForcePlaces(after, n, discovery ?? Enumerable.Empty<int>(), Math.Clamp(o.Discovery, 0, 10));
        }
        else
        {
            // intervalle fixe : la carte p (1, 2, …) est hors bibliothèque si p est un multiple de N
            int every = Math.Clamp(o.Every, 2, 20), libSeen = 0, e = 0;
            after = Enumerable.Repeat(n, ext.Count).ToArray();
            for (int p = 1; p <= size && libSeen < n; p++)
            {
                if (p % every == 0 && e < ext.Count)
                {
                    after[e++] = libSeen;
                }
                else
                {
                    libSeen++;
                }
            }
        }

        return FitRow(after, n, size);
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
