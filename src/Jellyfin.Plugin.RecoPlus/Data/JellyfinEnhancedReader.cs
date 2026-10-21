using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Ce que le compte a dit explicitement dans Jellyfin Enhanced.</summary>
public sealed class ExplicitFeedback
{
    /// <summary>Titres de la bibliothèque masqués partout (« pas intéressé »), identifiants Jellyfin en « N ».</summary>
    public HashSet<string> HiddenItems { get; } = new(StringComparer.Ordinal);

    /// <summary>Titres masqués repérés par TMDb (« movie:123 », « tv:456 ») : bibliothèque ou catalogue Seerr.</summary>
    public HashSet<string> HiddenTmdb { get; } = new(StringComparer.Ordinal);

    /// <summary>Notes d'avis (1 à 5) par TMDb (« movie:123 », « tv:456 »).</summary>
    public Dictionary<string, double> Ratings { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Lit, en lecture seule, les masquages et les avis notés de Jellyfin Enhanced (fichiers JSON de sa configuration).
/// Format absent ou inattendu : rien n'est lu, l'état le dit, le reste du plugin n'en dépend pas.
/// </summary>
public sealed class JellyfinEnhancedReader
{
    private const long MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _base;
    private readonly ILogger<JellyfinEnhancedReader> _log;

    /// <param name="basePath">Dossier de configuration de Jellyfin Enhanced (plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced).</param>
    public JellyfinEnhancedReader(string basePath, ILogger<JellyfinEnhancedReader> log)
    {
        _base = basePath;
        _log = log;
    }

    /// <summary>État lisible (page de configuration ; emballé : <see cref="Texts.Pack"/>).</summary>
    public string State { get; private set; } = "non lu";

    /// <summary>Retours explicites de chaque compte (clé : identifiant en « N »).</summary>
    public Dictionary<string, ExplicitFeedback> ReadAll(IEnumerable<Guid> users)
    {
        var res = new Dictionary<string, ExplicitFeedback>(StringComparer.Ordinal);
        if (!Directory.Exists(_base))
        {
            State = "Jellyfin Enhanced absent : masquages et avis non pris en compte";
            return res;
        }

        int hidden = 0, ratings = 0, errors = 0;
        foreach (var u in users)
        {
            var key = u.ToString("N");
            var fb = new ExplicitFeedback();
            res[key] = fb;
            var file = Path.Combine(_base, key, "hidden-content.json");
            try
            {
                if (Load<HiddenFile>(file) is { Items: not null } hf)
                {
                    foreach (var it in hf.Items.Values)
                    {
                        // seul « masqué partout » dit « pas intéressé » ; les autres portées ne retirent qu'une rangée d'accueil
                        if (it is null || !string.Equals(it.HideScope ?? "global", "global", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var type = (it.Type ?? string.Empty).ToLowerInvariant();
                        if (type is not ("movie" or "series" or "tv"))
                        {
                            continue; // un épisode ou une saison masqué n'est pas un rejet de la série
                        }

                        if (Guid.TryParse(it.ItemId, out var g))
                        {
                            fb.HiddenItems.Add(g.ToString("N"));
                        }

                        if (TmdbOf(it.TmdbId) is int tmdb && tmdb > 0)
                        {
                            fb.HiddenTmdb.Add((type == "movie" ? "movie:" : "tv:") + tmdb);
                        }

                        hidden++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                errors++;
                _log.LogWarning("Reco+: unreadable Jellyfin Enhanced hidden titles for an account ({Msg})", ex.Message);
            }
        }

        try
        {
            if (Load<ReviewsFile>(Path.Combine(_base, "reviews.json")) is { Reviews: not null } rf)
            {
                foreach (var r in rf.Reviews.Values)
                {
                    if (r?.Rating is not double v || v <= 0 || !Guid.TryParse(r.UserId, out var g) || TmdbOf(r.TmdbId) is not int tmdb
                        || !res.TryGetValue(g.ToString("N"), out var fb))
                    {
                        continue;
                    }

                    var mt = string.Equals(r.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv:" : "movie:";
                    fb.Ratings[mt + tmdb] = Math.Clamp(v, 1, 5);
                    ratings++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            errors++;
            _log.LogWarning("Reco+: unreadable Jellyfin Enhanced reviews ({Msg})", ex.Message);
        }

        State = errors > 0
            ? Texts.Pack("lu : {0} titres masqués, {1} avis notés, {2} fichier(s) illisible(s)", hidden, ratings, errors)
            : Texts.Pack("lu : {0} titres masqués, {1} avis notés", hidden, ratings);
        return res;
    }

    /// <summary>Identifiant TMDb écrit en texte ou en nombre selon les versions.</summary>
    private static int? TmdbOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) && n > 0 => n,
        JsonValueKind.String when int.TryParse(e.GetString(), out var n) && n > 0 => n,
        _ => null,
    };

    private static T? Load<T>(string path)
        where T : class
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length == 0)
        {
            return null;
        }

        if (fi.Length > MaxBytes)
        {
            throw new InvalidDataException("file too large");   // i18n:skip (interne, jamais affiché)
        }

        using var s = fi.OpenRead();
        return JsonSerializer.Deserialize<T>(s, Json);
    }

    private sealed class HiddenFile
    {
        public Dictionary<string, HiddenItem?>? Items { get; set; }
    }

    private sealed class HiddenItem
    {
        public string? ItemId { get; set; }

        public string? Type { get; set; }

        public JsonElement TmdbId { get; set; }

        public string? HideScope { get; set; }
    }

    private sealed class ReviewsFile
    {
        public Dictionary<string, Review?>? Reviews { get; set; }
    }

    private sealed class Review
    {
        public string? UserId { get; set; }

        public JsonElement TmdbId { get; set; }

        public string? MediaType { get; set; }

        public double? Rating { get; set; }
    }
}
