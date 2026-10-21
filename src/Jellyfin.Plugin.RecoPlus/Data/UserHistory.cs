using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Historique d'un compte traduit pour le moteur (épisodes agrégés par série), comme le prototype (data.py).</summary>
public sealed class UserHistory
{
    private readonly ILibraryManager _lib;
    private readonly HistoryStore _store;

    public UserHistory(ILibraryManager lib, HistoryStore store)
    {
        _lib = lib;
        _store = store;
    }

    private static double Unix(DateTime? d) => d is { } v && v != default
        ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0
        : 0;

    /// <summary>Interactions du compte avec les titres de l'instantané, à partir de ses lignes de données utilisateur.</summary>
    /// <param name="rows">Lignes de ce compte (UserDataReader).</param>
    /// <param name="prFirst">Premiers lancements issus de Playback Reporting (facultatif).</param>
    /// <param name="prLast">
    /// Dernières lectures réelles issues de Playback Reporting (facultatif) : elles priment sur la date de dernière
    /// lecture de Jellyfin, que « marquer comme vu » réécrit sans visionnage.
    /// </param>
    public Dictionary<string, Interaction> Build(Guid userId, IReadOnlyDictionary<Guid, UserDataRow>? rows, LibrarySnapshot lib,
        IReadOnlyDictionary<(Guid User, Guid Item), double>? prFirst, IReadOnlyDictionary<(Guid User, Guid Item), double>? prLast = null)
    {
        double PrLast(Guid item) => prLast is not null && prLast.TryGetValue((userId, item), out var v) ? v : 0;
        var inter = new Dictionary<string, Interaction>(StringComparer.Ordinal);
        if (rows is null || rows.Count == 0)
        {
            return inter;
        }

        double FirstStart(Guid item, double fallback)
        {
            double a = _store.First(userId, item);
            double b = prFirst is not null && prFirst.TryGetValue((userId, item), out var v) ? v : 0;
            double m = a == 0 ? b : (b == 0 ? a : Math.Min(a, b));
            return m == 0 ? fallback : m;
        }

        var perSeries = new Dictionary<Guid, List<UserDataRow>>();
        var favSeasons = new Dictionary<Guid, int>();
        foreach (var d in rows.Values)
        {
            // saison en favori : un goût pour la série, moindre que la série entière
            if (lib.SeasonSeries.TryGetValue(d.ItemId, out var ssid))
            {
                if (d.Favorite)
                {
                    favSeasons[ssid] = favSeasons.GetValueOrDefault(ssid) + 1;
                }

                continue;
            }

            if (lib.EpisodeSeries.TryGetValue(d.ItemId, out var sid))
            {
                if (!perSeries.TryGetValue(sid, out var l))
                {
                    perSeries[sid] = l = new List<UserDataRow>();
                }

                l.Add(d);
                continue;
            }

            var id = d.ItemId.ToString("N");
            if (!lib.Titles.TryGetValue(id, out var t))
            {
                continue; // saisons, musique, éléments supprimés…
            }

            double last = PrLast(d.ItemId) is > 0 and var real ? real : Unix(d.LastPlayed);
            if (t.Kind == TitleKind.Movie)
            {
                double comp = d.Played ? 1.0 : (lib.Runtime.TryGetValue(d.ItemId, out var rt) && rt > 0 ? Math.Min(1.0, d.Position / (double)rt) : 0);
                inter[id] = new Interaction
                {
                    Title = id,
                    Start = FirstStart(d.ItemId, last),
                    Last = last,
                    Completion = comp,
                    Played = d.Played,
                    Plays = d.PlayCount,
                    Favorite = d.Favorite,
                    Rewatch = HistoryMath.MovieRewatch(d.PlayCount),
                    Rating = HistoryMath.ExplicitRating(d.Rating, d.Likes),
                };
            }
            else if (d.Favorite || HistoryMath.ExplicitRating(d.Rating, d.Likes) is not null)
            {
                inter[id] = new Interaction { Title = id, Favorite = d.Favorite, Rating = HistoryMath.ExplicitRating(d.Rating, d.Likes) };
            }
        }

        foreach (var (sid, eps) in perSeries)
        {
            var id = sid.ToString("N");
            if (!lib.Titles.TryGetValue(id, out var st))
            {
                continue;
            }

            var lasts = eps.Select(e => Unix(e.LastPlayed)).Where(v => v != 0).ToList();
            int favEps = eps.Count(e => e.Favorite);
            if (lasts.Count == 0 && !eps.Any(e => e.Played))
            {
                if (favEps > 0)
                {
                    // épisodes aimés sans visionnage enregistré (lecture ailleurs, base importée…)
                    var only = inter.TryGetValue(id, out var p0) ? p0 : inter[id] = new Interaction { Title = id };
                    only.FavEpisodes = favEps;
                }

                continue;
            }

            int played = eps.Count(e => e.Played);
            int n = Math.Max(st.EpisodeCount, 1);
            double first = FirstStart(sid, 0);
            foreach (var e in eps)
            {
                double f = FirstStart(e.ItemId, 0);
                if (f != 0 && (first == 0 || f < first))
                {
                    first = f;
                }
            }

            inter.TryGetValue(id, out var prev);
            // dernière lecture réelle (Playback Reporting) si connue, sinon la date de Jellyfin ; 0 : marqués vus sans date
            double realLast = Math.Max(PrLast(sid), eps.Select(e => PrLast(e.ItemId)).DefaultIfEmpty(0).Max());
            double seriesLast = realLast > 0 ? realLast : (lasts.Count > 0 ? lasts.Max() : 0);
            inter[id] = new Interaction
            {
                Title = id,
                Start = first == 0 ? (lasts.Count > 0 ? lasts.Min() : 0) : first,
                Last = seriesLast,
                Completion = Math.Min(1.0, played / (double)n),
                Played = played > 0,
                Episodes = played,
                Plays = played,
                Favorite = prev?.Favorite ?? false,
                Rating = prev?.Rating,
                FavEpisodes = favEps,
                Rewatch = Math.Min(1.0, eps.Count(e => e.PlayCount >= 2) / 3.0),
                Hours = HistoryMath.WatchedHours(eps.Where(e => e.Played).Select(e => lib.Runtime.TryGetValue(e.ItemId, out var rt) ? (long?)rt : null),
                    lib.SeriesMeanHours.TryGetValue(sid, out var mh) ? mh : (double?)null),
                NewEpisodes = HistoryMath.NewSinceCaughtUp(lib.EpisodeAdded.GetValueOrDefault(sid), seriesLast, played),
            };
        }

        foreach (var (sid, count) in favSeasons)
        {
            var id = sid.ToString("N");
            if (lib.Titles.ContainsKey(id))
            {
                var x = inter.TryGetValue(id, out var p1) ? p1 : inter[id] = new Interaction { Title = id };
                x.FavSeasons = count;
            }
        }

        // comme data.py : on garde ce qui a une activité, un visionnage (même sans date), un favori (série, saison,
        // épisode) ou une note ; puis les dates inconnues (vu sans date, marquage en masse) sont repérées
        var kept = inter.Where(kv => kv.Value.Last != 0 || kv.Value.Played || kv.Value.Episodes > 0 || kv.Value.Favorite || kv.Value.FavSeasons > 0
                                     || kv.Value.FavEpisodes > 0 || kv.Value.Rating is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Core.MarkBackfill(kept.Values);
        return kept;
    }

    /// <summary>Titres que le compte a le droit de voir (bibliothèques autorisées, contrôle parental), appliqué par Jellyfin.</summary>
    public HashSet<string> Accessible(User user)
        => _lib.GetItemIds(new InternalItemsQuery(user)                      // identifiants seulement : pas d'objets complets
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
            })
            .Select(i => i.ToString("N"))
            .ToHashSet(StringComparer.Ordinal);
}
