using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Pourquoi un titre de la bibliothèque est proposé à un compte : une phrase courte, affichée discrètement (au survol
/// d'une carte, sur la fiche du titre). Messages emballés (<see cref="Texts.Pack"/>), traduits à l'affichage. Une raison
/// n'est donnée que si elle est vraie et nette : sans lien clair avec un titre aimé, aucune.
/// </summary>
public static class Reasons
{
    /// <summary>
    /// Ressemblance minimale avec un titre aimé pour dire « parce que tu as aimé ». Mesurée sur une bibliothèque réelle
    /// (métadonnées TMDb, 136 titres) : titres liés 0,15 à 0,33 (Ghibli entre eux ~0,18, Iron Man et Avengers 0,16), sans
    /// rapport ~0 ; 95 % des paires sous 0,05, 99 % sous 0,15. 0,12 garde les vrais liens, jamais le bruit.
    /// </summary>
    public const double ExplainSimilarity = 0.12;

    /// <summary>Titres aimés examinés comme explication (les plus forts).</summary>
    public const int Anchors = 30;

    /// <summary>
    /// Raison de chaque titre des rangées (et des titres en tête), par ordre de priorité : titre en tête (nouveaux
    /// épisodes, suite d'une saga), raison de la rangée (« Parce que … », listes du moment, nouveautés), puis le titre aimé
    /// le plus proche s'il ressemble nettement.
    /// </summary>
    /// <param name="ctx">Contexte du calcul (titres, signaux, historique).</param>
    /// <param name="vec">Vecteurs de contenu.</param>
    /// <param name="user">Compte.</param>
    /// <param name="rows">Rangées (identifiant → titres), dans l'ordre de la répartition.</param>
    /// <param name="anchorIds">Titre aimé de chaque rangée « Parce que … ».</param>
    /// <param name="pinned">Titres placés en tête.</param>
    /// <param name="listReason">Raison d'une rangée de liste TMDb (« Tendances »…) ; null : aucune.</param>
    public static Dictionary<string, string> For(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user,
        IReadOnlyDictionary<string, List<string>> rows, IReadOnlyDictionary<string, string> anchorIds, IReadOnlyCollection<string> pinned,
        Func<string, string?> listReason)
    {
        var T = ctx.Ds.Titles;
        var hist = ctx.Hist.TryGetValue(user, out var h) ? h : new Dictionary<string, Interaction>();
        var sig = ctx.Sig.TryGetValue(user, out var s) ? s : new Dictionary<string, double>();
        var res = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in pinned)
        {
            if (!T.TryGetValue(id, out var t))
            {
                continue;
            }

            if (t.Kind == TitleKind.Series)
            {
                res[id] = Texts.Pack("Nouveaux épisodes");
                continue;
            }

            // suite d'une saga : le volet précédent que le compte a vu ou aimé
            var prev = Recommender.Franchise(ctx.Ds, id) is { } f && ctx.Ds.Collections.TryGetValue(f.Id, out var parts)
                ? parts.Where(p => p != id && T.ContainsKey(p) && Sagas.Order(T[p], t) < 0 && sig.GetValueOrDefault(p) > 0).OrderBy(p => T[p], Comparer<Title>.Create(Sagas.Order)).LastOrDefault()
                : null;
            res[id] = prev is null ? Texts.Pack("Volet suivant d'une saga que tu suis") : Texts.Pack("Suite de {0}", T[prev].Name);
        }

        // titres qui peuvent servir d'explication : regardés ou aimés (un titre seulement demandé explique mal un autre)
        static bool Known(Interaction? x) => x is not null && (x.Played || x.Episodes > 0 || x.Completion > 0 || x.Favorite || x.Rating is > 0);
        var liked = sig.Where(e => e.Value > 0 && vec.ContainsKey(e.Key) && T.TryGetValue(e.Key, out var lt) && !lt.External && Known(hist.GetValueOrDefault(e.Key)))
            .OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Take(Anchors).Select(e => e.Key).ToList();

        foreach (var (row, ids) in rows)
        {
            var kind = HomeRows.KindOfRow(row);
            foreach (var id in ids)
            {
                if (res.ContainsKey(id) || !T.TryGetValue(id, out var t))
                {
                    continue;
                }

                string? why = null;
                if (kind == HomeRows.ParceQue && anchorIds.TryGetValue(row, out var a) && T.TryGetValue(a, out var at))
                {
                    why = Texts.Pack(Exploration.BecauseTemplate(hist.GetValueOrDefault(a)), at.Name);
                }
                else if (Array.IndexOf(HomeRows.ListRows, kind) >= 0)
                {
                    why = listReason(kind);
                }
                else if (kind == HomeRows.Nouveautes)
                {
                    why = Texts.Pack("Arrivé récemment");
                }

                if (why is null && vec.TryGetValue(id, out var v))
                {
                    // le titre aimé le plus proche, s'il ressemble nettement
                    string? best = null;
                    double bs = ExplainSimilarity;
                    foreach (var l in liked)
                    {
                        double d = v.Dot(vec[l]);
                        if (d > bs)
                        {
                            (best, bs) = (l, d);
                        }
                    }

                    if (best is not null)
                    {
                        why = Texts.Pack(Exploration.BecauseTemplate(hist.GetValueOrDefault(best)), T[best].Name);
                    }
                }

                if (why is not null)
                {
                    res[id] = why;
                }
            }
        }

        return res;
    }
}
