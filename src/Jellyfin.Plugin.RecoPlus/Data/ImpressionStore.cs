using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Ce que chaque compte a vu à l'écran sans l'ouvrir. Alimenté uniquement par le script de l'interface web, quand une
/// rangée Reco+ est réellement affichée au compte connecté (carte visible au moins une seconde, onglet au premier plan) :
/// un calcul, une page jamais ouverte ou un autre compte ne comptent jamais. Un jour d'affichage au plus par titre et
/// par jour ; ouvrir le titre remet son compteur à zéro. Écrit sur disque en différé (atomique).
/// </summary>
public sealed partial class ImpressionStore : IDisposable
{
    /// <summary>Titres retenus par compte (les plus anciens affichages sont oubliés au-delà).</summary>
    public const int MaxPerUser = 3000;

    /// <summary>Titres acceptés par appel.</summary>
    public const int MaxPerCall = 100;

    /// <summary>Un affichage plus ancien est oublié (jours).</summary>
    public const double ForgetDays = 365;

    private readonly Func<string?> _path;
    private readonly ILogger<ImpressionStore> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _byUser = new(StringComparer.Ordinal);
    private readonly Timer _flush;
    private bool _dirty;
    private bool _loaded;

    /// <param name="path">Chemin du fichier (null tant que le plugin n'est pas prêt).</param>
    public ImpressionStore(Func<string?> path, ILogger<ImpressionStore> log)
    {
        _path = path;
        _log = log;
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    /// <summary>Un titre affiché à un compte.</summary>
    public sealed class Entry
    {
        /// <summary>Jours distincts d'affichage.</summary>
        public int Days { get; set; }

        /// <summary>Jour (UTC, jours depuis 1970) du dernier affichage compté.</summary>
        public long LastDay { get; set; }

        /// <summary>Dernier affichage (secondes Unix).</summary>
        public double Last { get; set; }
    }

    [GeneratedRegex("^(?:[0-9a-f]{32}|tmdb:(?:movie|tv):[0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Identifiant de titre acceptable (Jellyfin en « N », ou « tmdb:movie:123 » / « tmdb:tv:123 »).</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40)
        {
            return null;
        }

        var s = id.Trim().ToLowerInvariant();
        if (Guid.TryParse(s, out var g))
        {
            s = g.ToString("N");
        }

        return IdPattern().IsMatch(s) ? s : null;
    }

    /// <summary>Enregistre des titres vus à l'écran et des titres ouverts (remis à zéro). Renvoie le nombre de titres pris en compte.</summary>
    public int Record(string userKey, IEnumerable<string?>? shown, IEnumerable<string?>? opened, double now)
    {
        EnsureLoaded();
        long day = (long)Math.Floor(now / 86400.0);
        int n = 0;
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userKey, out var m))
            {
                _byUser[userKey] = m = new Dictionary<string, Entry>(StringComparer.Ordinal);
            }

            foreach (var id in (opened ?? Array.Empty<string>()).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxPerCall))
            {
                n += m.Remove(id) ? 1 : 0;
            }

            var openedSet = (opened ?? Array.Empty<string>()).Select(Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var id in (shown ?? Array.Empty<string>()).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxPerCall))
            {
                if (openedSet.Contains(id))
                {
                    continue; // ouvert dans le même envoi : pas un « non »
                }

                if (!m.TryGetValue(id, out var e))
                {
                    m[id] = e = new Entry();
                }

                if (e.LastDay != day)
                {
                    e.Days++;
                    e.LastDay = day;
                }

                e.Last = Math.Max(e.Last, now);
                n++;
            }

            if (m.Count > MaxPerUser)
            {
                foreach (var old in m.OrderBy(kv => kv.Value.Last).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(m.Count - MaxPerUser).Select(kv => kv.Key).ToList())
                {
                    m.Remove(old);
                }
            }

            _dirty = true;
        }

        return n;
    }

    /// <summary>Affichages d'un compte, pour le moteur (oubliés au-delà d'un an).</summary>
    public Dictionary<string, Exposure> For(string userKey, double now)
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byUser.TryGetValue(userKey, out var m)
                ? m.Where(kv => now - kv.Value.Last < ForgetDays * 86400.0).ToDictionary(kv => kv.Key, kv => new Exposure(kv.Value.Days, kv.Value.Last), StringComparer.Ordinal)
                : new Dictionary<string, Exposure>(StringComparer.Ordinal);
        }
    }

    /// <summary>Oublie tout pour un compte.</summary>
    public void Forget(string userKey)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _dirty |= _byUser.Remove(userKey);
        }
    }

    /// <summary>Écrit sur disque si nécessaire (atomique).</summary>
    public void Flush()
    {
        var path = _path();
        if (path is null)
        {
            return;
        }

        string json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_byUser);
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                _dirty = true;
            }

            _log.LogWarning(ex, "Reco+ : écriture des affichages impossible");
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
                if (fi.Exists && fi.Length is > 0 and < 64 * 1024 * 1024
                    && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(path)) is { } d)
                {
                    foreach (var (u, m) in d)
                    {
                        if (m is null)
                        {
                            continue;
                        }

                        var clean = m.Where(kv => Normalize(kv.Key) == kv.Key && kv.Value is { Days: > 0 } && double.IsFinite(kv.Value.Last))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                        _byUser.TryAdd(u, clean);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+ : fichier des affichages illisible ({Type}), il repart de zéro", ex.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _flush.Dispose();
        Flush();
    }
}
