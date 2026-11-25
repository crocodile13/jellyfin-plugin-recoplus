using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RecoPlus.Engine;

namespace RecoPlus.Tests;

/// <summary>
/// Lecture d'un jeu de données au format de golden.json et de l'export de calibrage (comptes u01, u02…). Partagé par
/// les tests et l'outil de calibrage du banc MovieLens (tests/movielens/Calibrate).
/// </summary>
public static class DatasetJson
{
    public static Dataset Load(JsonElement d)
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        foreach (var t in d.GetProperty("titles").EnumerateArray())
        {
            var people = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var p in t.GetProperty("people").EnumerateObject())
            {
                people[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
            }

            titles[t.GetProperty("id").GetString()!] = new Title
            {
                Id = t.GetProperty("id").GetString()!,
                Kind = t.GetProperty("kind").GetString() == "Movie" ? TitleKind.Movie : TitleKind.Series,
                Name = t.GetProperty("name").GetString()!,
                Year = t.GetProperty("year").ValueKind == JsonValueKind.Null ? null : t.GetProperty("year").GetInt32(),
                Genres = Strings(t, "genres"),
                Tags = Strings(t, "tags"),
                Studios = Strings(t, "studios"),
                People = people,
                Community = t.GetProperty("community").ValueKind == JsonValueKind.Null ? null : t.GetProperty("community").GetDouble(),
                Added = t.GetProperty("added").ValueKind == JsonValueKind.Null ? null : t.GetProperty("added").GetDouble(),
                EpisodeCount = t.GetProperty("episodes").GetInt32(),
                Released = NumOrNull(t, "released"),
                Critic = NumOrNull(t, "critic"),
                Countries = t.TryGetProperty("countries", out _) ? Strings(t, "countries") : Array.Empty<string>(),
                Language = t.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String ? lg.GetString() : null,
                Votes = NumOrNull(t, "votes") is double vo ? (int)vo : null,
                External = t.TryGetProperty("external", out var ex) && ex.ValueKind == JsonValueKind.True,
            };
        }

        var users = new Dictionary<string, Dictionary<string, Interaction>>(StringComparer.Ordinal);
        foreach (var u in d.GetProperty("users").EnumerateObject())
        {
            var h = new Dictionary<string, Interaction>(StringComparer.Ordinal);
            foreach (var x in u.Value.EnumerateArray())
            {
                var id = x.GetProperty("title").GetString()!;
                h[id] = new Interaction
                {
                    Title = id,
                    Start = x.GetProperty("start").GetDouble(),
                    Last = x.GetProperty("last").GetDouble(),
                    Completion = x.GetProperty("completion").GetDouble(),
                    Played = x.GetProperty("played").GetBoolean(),
                    Episodes = x.GetProperty("episodes").GetInt32(),
                    Plays = x.GetProperty("plays").GetInt32(),
                    Favorite = x.GetProperty("fav").GetBoolean(),
                    Requested = NumOrNull(x, "requested") ?? 0,
                    Watchlisted = NumOrNull(x, "watchlisted") ?? 0,
                    Rewatch = NumOrNull(x, "rewatch") ?? 0,
                    FavSeasons = (int)(NumOrNull(x, "fav_seasons") ?? 0),
                    FavEpisodes = (int)(NumOrNull(x, "fav_episodes") ?? 0),
                    Rating = NumOrNull(x, "rating"),
                    Hidden = x.TryGetProperty("hidden", out var hd) && hd.GetBoolean(),
                    Hours = NumOrNull(x, "hours") ?? 0,
                    Backfill = x.TryGetProperty("backfill", out var bf) && bf.GetBoolean(),
                };
            }

            users[u.Name] = h;
        }

        var colls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in d.GetProperty("collections").EnumerateObject())
        {
            colls[c.Name] = c.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
        }

        var thematic = new HashSet<string>(StringComparer.Ordinal);
        if (d.TryGetProperty("thematic", out var th) && th.ValueKind == JsonValueKind.Array)
        {
            thematic.UnionWith(th.EnumerateArray().Select(x => x.GetString()!));
        }

        var reviews = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        if (d.TryGetProperty("reviews", out var rv))
        {
            foreach (var t in rv.EnumerateObject())
            {
                reviews[t.Name] = t.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble(), StringComparer.Ordinal);
            }
        }

        int shift = d.TryGetProperty("utc_offset_hours", out var off) && off.ValueKind == JsonValueKind.Number ? off.GetInt32() : 0;
        var ds = new Dataset
        {
            Titles = titles, Users = users, Collections = colls, Thematic = thematic, Now = d.GetProperty("now").GetDouble(), Reviews = reviews,
            LocalHour = shift == 0 ? null : t => (int)((((t + (shift * 3600)) % 86400) + 86400) % 86400 / 3600),
        };
        ds.LinkCollections();
        return ds;
    }

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    public static double? NumOrNull(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
