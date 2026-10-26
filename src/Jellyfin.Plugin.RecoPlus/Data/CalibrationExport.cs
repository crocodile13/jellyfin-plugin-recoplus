using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Export du jeu de calibrage, pour l'évaluer hors du serveur (format du banc d'essai : <c>prototype/</c> et tests).
/// Pseudonymisé : les comptes deviennent u01, u02… (dans l'ordre de leur identifiant, sans nom, sans identifiant),
/// aussi dans les avis du foyer. Restent : la bibliothèque (titres, genres, mots-clés, studios, personnes, notes, dates)
/// et, par compte, ce qu'il a fait de chaque titre avec ses dates. Aucun secret, aucune adresse, aucun appareil.
/// </summary>
public static class CalibrationExport
{
    /// <summary>Écrit le jeu de données en JSON.</summary>
    public static void Write(Dataset ds, Stream output)
    {
        var alias = ds.Users.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => (k, "u" + (i + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture)))
            .ToDictionary(x => x.k, x => x.Item2, StringComparer.Ordinal);
        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteString("format", "reco-plus-calibration-1");
        w.WriteNumber("now", ds.Now);
        int utcHour = (int)(((ds.Now % 86400) + 86400) % 86400 / 3600);
        w.WriteNumber("utc_offset_hours", ((ds.HourOf(ds.Now) - utcHour + 36) % 24) - 12);   // fuseau des heures de visionnage

        w.WriteStartArray("titles");
        foreach (var t in ds.Titles.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("id", t.Id);
            w.WriteString("kind", t.Kind == TitleKind.Movie ? "Movie" : "Series");
            w.WriteString("name", t.Name);
            Num(w, "year", t.Year);
            Strings(w, "genres", t.Genres);
            Strings(w, "tags", t.Tags);
            Strings(w, "studios", t.Studios);
            w.WriteStartObject("people");
            foreach (var (role, names) in t.People)
            {
                Strings(w, role, names);
            }

            w.WriteEndObject();
            Num(w, "community", t.Community);
            Num(w, "added", t.Added);
            w.WriteNumber("episodes", t.EpisodeCount);
            Num(w, "released", t.Released);
            Num(w, "critic", t.Critic);
            Strings(w, "countries", t.Countries);
            if (t.Language is null)
            {
                w.WriteNull("language");
            }
            else
            {
                w.WriteString("language", t.Language);
            }

            Num(w, "votes", t.Votes);
            w.WriteBoolean("external", t.External);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartObject("users");
        foreach (var (u, h) in ds.Users.OrderBy(kv => alias[kv.Key], StringComparer.Ordinal))
        {
            w.WriteStartArray(alias[u]);
            foreach (var x in h.Values.OrderBy(x => x.Title, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("title", x.Title);
                w.WriteNumber("start", x.Start);
                w.WriteNumber("last", x.Last);
                w.WriteNumber("completion", x.Completion);
                w.WriteBoolean("played", x.Played);
                w.WriteNumber("episodes", x.Episodes);
                w.WriteNumber("plays", x.Plays);
                w.WriteBoolean("fav", x.Favorite);
                w.WriteNumber("requested", x.Requested);
                w.WriteNumber("watchlisted", x.Watchlisted);
                w.WriteNumber("rewatch", x.Rewatch);
                w.WriteNumber("fav_seasons", x.FavSeasons);
                w.WriteNumber("fav_episodes", x.FavEpisodes);
                Num(w, "rating", x.Rating);
                w.WriteBoolean("hidden", x.Hidden);
                w.WriteNumber("hours", x.Hours);
                w.WriteBoolean("backfill", x.Backfill);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();

        w.WriteStartObject("collections");
        foreach (var (id, items) in ds.Collections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Strings(w, id, items);
        }

        w.WriteEndObject();

        // collections qui ne sont pas des sagas (titres de plusieurs univers)
        w.WriteStartArray("thematic");
        foreach (var id in ds.Thematic.Order(StringComparer.Ordinal))
        {
            w.WriteStringValue(id);
        }

        w.WriteEndArray();

        w.WriteStartObject("reviews");
        foreach (var (title, by) in ds.Reviews.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WriteStartObject(title);
            foreach (var (u, stars) in by.Where(kv => alias.ContainsKey(kv.Key)).OrderBy(kv => alias[kv.Key], StringComparer.Ordinal))
            {
                w.WriteNumber(alias[u], stars);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Num(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d && double.IsFinite(d))
        {
            w.WriteNumber(name, d);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
