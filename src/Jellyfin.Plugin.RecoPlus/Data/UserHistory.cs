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
    public Dictionary<string, Interaction> Build(Guid userId, IReadOnlyDictionary<Guid, UserDataRow>? rows, LibrarySnapshot lib,
        IReadOnlyDictionary<(Guid User, Guid Item), double>? prFirst)
    {
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
        foreach (var d in rows.Values)
        {
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

            double last = Unix(d.LastPlayed);
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
                };
            }
            else if (d.Favorite)
            {
                inter[id] = new Interaction { Title = id, Favorite = true };
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
            if (lasts.Count == 0)
            {
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

            bool fav = inter.TryGetValue(id, out var prev) && prev.Favorite;
            inter[id] = new Interaction
            {
                Title = id,
                Start = first == 0 ? lasts.Min() : first,
                Last = lasts.Max(),
                Completion = Math.Min(1.0, played / (double)n),
                Played = played > 0,
                Episodes = played,
                Plays = played,
                Favorite = fav,
            };
        }

        // comme data.py : on ne garde que ce qui a une activité ou un favori
        return inter.Where(kv => kv.Value.Last != 0 || kv.Value.Favorite).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Titres que le compte a le droit de voir (bibliothèques autorisées, contrôle parental), appliqué par Jellyfin.</summary>
    public HashSet<string> Accessible(User user)
        => _lib.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
            })
            .Select(i => i.Id.ToString("N"))
            .ToHashSet(StringComparer.Ordinal);
}
