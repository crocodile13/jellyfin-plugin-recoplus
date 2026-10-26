using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Réglages des titres placés en tête de liste.</summary>
/// <param name="Max">Nombre maximal de titres placés en tête (0 = aucun).</param>
/// <param name="MaxDays">Fenêtre (jours) : arrivés dans la bibliothèque depuis, et pour un titre aimé sans être vu ici, sortis depuis.</param>
/// <param name="SagaMax">Au-delà de ce nombre de titres, une collection n'est pas une saga mais un regroupement.</param>
public sealed record PinOptions(int Max, double MaxDays, int SagaMax);

/// <summary>
/// Titres placés en tête : les nouveautés de ce que le compte suit déjà, seulement. Une nouveauté qu'il ne connaît pas
/// encore mais qui prolonge ce qu'il a regardé ou aimé : nouveaux épisodes ou nouvelle saison d'une série, volet suivant
/// d'une saga. Jamais un titre qu'il a lui-même demandé ou mis de côté (il sait déjà qu'il le veut).
/// <list type="bullet">
/// <item>Titre vu sur ce serveur (même sans l'avoir aimé, pourvu que son poids soit positif : ni abandon ni masqué) :
/// la nouveauté est arrivée après sa dernière lecture ; pour une série, le compte avait vu presque tout ce qui existait
/// alors (une série laissée en plan n'est pas « suivie »).</item>
/// <item>Titre aimé sans être vu ici (vu ailleurs) : on ignore jusqu'où il l'a vu, donc la nouveauté doit être sortie
/// récemment (diffusion de l'épisode, sortie du film), pas seulement ajoutée.</item>
/// </list>
/// Les plus récemment arrivées d'abord.
/// </summary>
public static class Pins
{
    /// <summary>Écart toléré entre la dernière lecture et l'ajout d'un épisode (même import, horloges).</summary>
    private const double Slack = 3600;

    /// <summary>Titres placés en tête.</summary>
    /// <param name="ds">Jeu de données (titres, collections).</param>
    /// <param name="h">Historique du compte.</param>
    /// <param name="sig">Signaux du compte.</param>
    /// <param name="allowed">Le compte a le droit de voir ce titre (bibliothèques, contrôle parental).</param>
    /// <param name="episodes">Série → ses épisodes (date d'ajout, date de diffusion ou 0), ou null.</param>
    /// <param name="now">Instant du calcul.</param>
    /// <param name="o">Réglages.</param>
    public static List<string> Select(Dataset ds, IReadOnlyDictionary<string, Interaction> h, IReadOnlyDictionary<string, double> sig,
        Func<string, bool> allowed, Func<string, IReadOnlyList<(double Added, double Aired)>?> episodes, double now, PinOptions o)
    {
        if (o.Max <= 0)
        {
            return new List<string>();
        }

        double cut = now - (Math.Max(1.0, o.MaxDays) * Core.Day);
        var found = new Dictionary<string, double>(StringComparer.Ordinal);   // titre → date d'arrivée de la nouveauté

        static bool SeenHere(Interaction x) => x.Played || x.Episodes > 0 || x.Completion > 0;

        // nouveaux épisodes, nouvelle saison
        foreach (var (id, x) in h)
        {
            if (x.Hidden || sig.GetValueOrDefault(id) <= 0 || !ds.Titles.TryGetValue(id, out var t) || t.External || t.Kind != TitleKind.Series || !allowed(id)
                || episodes(id) is not { Count: > 0 } eps)
            {
                continue;
            }

            List<(double Added, double Aired)> fresh;
            if (x.Episodes > 0)
            {
                if (x.Last <= 0)
                {
                    continue; // date inconnue : impossible de savoir ce qui est nouveau pour lui
                }

                int before = eps.Count(e => e.Added <= x.Last + Slack);
                // presque à jour à sa dernière lecture (tolérance d'environ 10 %, au moins un épisode)
                if (before == 0 || x.Episodes < before - Math.Max(1, (int)Math.Round(0.1 * before)))
                {
                    continue;
                }

                fresh = eps.Where(e => e.Added > x.Last + Slack && e.Added >= cut).ToList();
            }
            else
            {
                fresh = eps.Where(e => e.Added >= cut && e.Aired >= cut).ToList();
            }

            if (fresh.Count > 0)
            {
                found[id] = fresh.Max(e => e.Added);
            }
        }

        // volet suivant d'une saga
        foreach (var m in ds.Titles.Values)
        {
            if (m.External || m.Kind != TitleKind.Movie || (m.Added ?? 0) < cut || !allowed(m.Id)
                || (h.TryGetValue(m.Id, out var mx) && (SeenHere(mx) || mx.Hidden || mx.Requested > 0 || mx.Watchlisted > 0 || mx.Favorite || mx.Rating is not null))
                || Recommender.Franchise(ds, m.Id) is not { } fr || !Sagas.IsSaga(ds, fr, o.SagaMax) || !ds.Collections.TryGetValue(fr.Id, out var parts))
            {
                continue;
            }

            double Order(Title t) => t.Released ?? (t.Year is int y ? new DateTimeOffset(y, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() : double.MaxValue);
            var others = parts.Where(p => p != m.Id && ds.Titles.TryGetValue(p, out var pt) && !pt.External).Select(p => ds.Titles[p]).ToList();
            // le volet suivant seulement : chaque volet sorti avant lui a été vu ici ou aimé (vu ailleurs)
            // (films comme séries : un film qui prolonge une série suppose la série vue)
            if (others.Any(p => Order(p) < Order(m)
                && !(h.TryGetValue(p.Id, out var px) && !px.Hidden && (SeenHere(px) || sig.GetValueOrDefault(p.Id) > 0))))
            {
                continue;
            }

            bool follows = others.Any(p => h.TryGetValue(p.Id, out var px) && !px.Hidden && sig.GetValueOrDefault(p.Id) > 0
                && (SeenHere(px) ? px.Last <= 0 || px.Last < (m.Added ?? 0) : (m.Released ?? 0) >= cut));
            if (follows)
            {
                found[m.Id] = m.Added ?? 0;
            }
        }

        return found.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(o.Max).Select(kv => kv.Key).ToList();
    }
}
