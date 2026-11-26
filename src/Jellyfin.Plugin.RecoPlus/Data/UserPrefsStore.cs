using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Réglages de chaque compte (sa page Reco+, Plugin Pages) et indices de son pays signalés par son navigateur (fuseau
/// horaire, langues). Seul le compte connecté écrit les siens : l'identité vient du jeton de session. Chaque valeur est
/// vérifiée (forme, longueur, nombre) ; les comptes disparus sont oubliés. Écrit sur disque à chaque changement
/// (atomique) : les changements sont rares, un signalement identique au précédent n'écrit rien.
/// </summary>
public sealed partial class UserPrefsStore
{
    /// <summary>Langues du navigateur gardées (les premières, par ordre de préférence).</summary>
    public const int MaxLanguages = 4;

    private readonly Func<string?> _path;
    private readonly ILogger<UserPrefsStore> _log;
    private readonly object _lock = new();
    private readonly object _saveLock = new();   // deux écritures à la fois se disputeraient le fichier temporaire
    private readonly Dictionary<string, Prefs> _byUser = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <param name="path">Chemin du fichier (null tant que le plugin n'est pas prêt).</param>
    public UserPrefsStore(Func<string?> path, ILogger<UserPrefsStore> log)
    {
        _path = path;
        _log = log;
    }

    /// <summary>Réglages et indices d'un compte.</summary>
    public sealed class Prefs
    {
        /// <summary>Pays choisi par le compte (ISO 3166-1) ; null = détecté.</summary>
        public string? Region { get; set; }

        /// <summary>Titres hors bibliothèque pour ce compte ; null = comme le serveur.</summary>
        public bool? Extended { get; set; }

        /// <summary>Fuseau horaire IANA du navigateur (seulement un fuseau connu, rattaché à un pays).</summary>
        public string? BrowserZone { get; set; }

        /// <summary>Langues du navigateur (balises BCP 47), par ordre de préférence.</summary>
        public List<string>? BrowserLanguages { get; set; }

        /// <summary>Copie (rien de partagé avec le magasin).</summary>
        public Prefs Clone() => new() { Region = Region, Extended = Extended, BrowserZone = BrowserZone, BrowserLanguages = BrowserLanguages?.ToList() };
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    /// <summary>Balise de langue acceptable (« fr-FR », « pt-BR », « zh-Hant-TW ») ; null sinon.</summary>
    public static string? CleanLanguage(string? tag)
    {
        var t = tag?.Trim().Replace('_', '-');
        return t is { Length: > 0 and <= 35 } && LanguagePattern().IsMatch(t) ? t : null;
    }

    /// <summary>Fuseau acceptable : un fuseau IANA connu, rattaché à un pays ; null sinon.</summary>
    public static string? CleanZone(string? zone) => Regions.OfTimeZone(zone) is not null ? zone!.Trim() : null;

    /// <summary>Réglages et indices d'un compte (copie ; vide s'il n'a rien).</summary>
    public Prefs For(string userKey)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byUser.TryGetValue(userKey, out var p) ? p.Clone() : new Prefs();
        }
    }

    /// <summary>Indices signalés par le navigateur du compte connecté. Renvoie vrai s'ils ont changé.</summary>
    public bool Report(string userKey, string? zone, IEnumerable<string?>? languages)
    {
        var z = CleanZone(zone);
        var langs = (languages ?? Array.Empty<string?>()).Take(20).Select(CleanLanguage).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxLanguages).ToList();
        return Change(userKey, p =>
        {
            bool same = p.BrowserZone == z && (p.BrowserLanguages ?? new List<string>()).SequenceEqual(langs, StringComparer.Ordinal);
            (p.BrowserZone, p.BrowserLanguages) = (z, langs.Count > 0 ? langs : null);
            return !same;
        });
    }

    /// <summary>
    /// Réglages choisis par le compte connecté dans sa page ; seuls ceux que l'administrateur lui ouvre sont écrits
    /// (<paramref name="canRegion"/>, <paramref name="canExtended"/>). Un pays mal formé est refusé (faux).
    /// </summary>
    public bool Set(string userKey, string? region, bool? extended, bool canRegion, bool canExtended)
    {
        string? r = null;
        if (canRegion && !string.IsNullOrWhiteSpace(region) && (r = Regions.Clean(region)) is null)
        {
            return false;
        }

        Change(userKey, p =>
        {
            var before = (p.Region, p.Extended);
            if (canRegion)
            {
                p.Region = r;
            }

            if (canExtended)
            {
                p.Extended = extended;
            }

            return before != (p.Region, p.Extended);
        });
        return true;
    }

    /// <summary>Oublie les comptes qui n'existent plus (seuls ceux de <paramref name="users"/> restent).</summary>
    public int Retain(IReadOnlySet<string> users)
    {
        EnsureLoaded();
        List<string> gone;
        lock (_lock)
        {
            gone = _byUser.Keys.Where(u => !users.Contains(u)).ToList();
            foreach (var u in gone)
            {
                _byUser.Remove(u);
            }
        }

        if (gone.Count > 0)
        {
            Save();
        }

        return gone.Count;
    }

    private bool Change(string userKey, Func<Prefs, bool> edit)
    {
        EnsureLoaded();
        bool changed;
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userKey, out var p))
            {
                p = new Prefs();
            }

            changed = edit(p);
            if (p.Region is null && p.Extended is null && p.BrowserZone is null && p.BrowserLanguages is null)
            {
                changed |= _byUser.Remove(userKey);
            }
            else
            {
                _byUser[userKey] = p;
            }
        }

        if (changed)
        {
            Save();
        }

        return changed;
    }

    private void Save()
    {
        var path = _path();
        if (path is null)
        {
            return;
        }

        lock (_saveLock)
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_byUser);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Reco+: could not write the user preferences");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        var path = _path();
        lock (_lock)
        {
            if (_loaded || path is null)
            {
                return;
            }

            _loaded = true;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length is > 0 and < 4 * 1024 * 1024
                    && JsonSerializer.Deserialize<Dictionary<string, Prefs>>(File.ReadAllText(path)) is { } d)
                {
                    foreach (var (u, p) in d)
                    {
                        if (p is null || !Guid.TryParseExact(u, "N", out _))
                        {
                            continue;
                        }

                        // relu comme s'il venait d'être envoyé : un fichier modifié à la main ne fait rien passer d'invalide
                        var langs = p.BrowserLanguages?.Take(MaxLanguages).Select(CleanLanguage).OfType<string>().ToList();
                        _byUser[u] = new Prefs
                        {
                            Region = Regions.Clean(p.Region),
                            Extended = p.Extended,
                            BrowserZone = CleanZone(p.BrowserZone),
                            BrowserLanguages = langs is { Count: > 0 } ? langs : null,
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+: unreadable user preferences file ({Type}), starting from scratch", ex.GetType().Name);
            }
        }
    }
}
