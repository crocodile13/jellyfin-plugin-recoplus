using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Registre des propositions (<see cref="Measurement"/>) : pour chaque compte, les titres de la bibliothèque proposés dans
/// ses rangées, avec la rangée et la place de la première proposition. Alimenté par chaque calcul, lu pour le bilan de
/// l'état du moteur. Écrit sur disque en différé (atomique) ; rien n'y identifie un compte au-delà de sa clé Jellyfin.
/// </summary>
public sealed class RecoLedger : IDisposable
{
    private readonly Func<string?> _path;
    private readonly ILogger<RecoLedger> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, Measurement.Entry>> _byUser = new(StringComparer.Ordinal);
    private readonly Timer _flush;
    private bool _dirty;
    private bool _loaded;

    /// <param name="path">Chemin du fichier (null tant que le plugin n'est pas prêt).</param>
    public RecoLedger(Func<string?> path, ILogger<RecoLedger> log)
    {
        _path = path;
        _log = log;
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Note les rangées calculées pour un compte (titres de la bibliothèque seulement).</summary>
    public void Record(string userKey, IReadOnlyDictionary<string, List<string>> rows, Func<string, bool> keep, double now)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userKey, out var m))
            {
                _byUser[userKey] = m = new Dictionary<string, Measurement.Entry>(StringComparer.Ordinal);
            }

            Measurement.Record(m, rows, keep, now);
            _dirty = true;
        }
    }

    /// <summary>Copie du registre de tous les comptes (pour le bilan, hors verrou).</summary>
    public Dictionary<string, Dictionary<string, Measurement.Entry>> Snapshot()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _byUser.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(e => e.Key, e => new Measurement.Entry { Row = e.Value.Row, Position = e.Value.Position, First = e.Value.First, Last = e.Value.Last }, StringComparer.Ordinal),
                StringComparer.Ordinal);
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

            _log.LogWarning(ex, "Reco+: could not write the recommendation ledger");
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
                    && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Measurement.Entry>>>(File.ReadAllText(path)) is { } d)
                {
                    foreach (var (u, m) in d)
                    {
                        if (m is null)
                        {
                            continue;
                        }

                        var clean = m.Where(kv => kv.Value is { Position: > 0 } e && !string.IsNullOrEmpty(e.Row) && double.IsFinite(e.First) && double.IsFinite(e.Last) && e.First <= e.Last)
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                        _byUser.TryAdd(u, clean);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning("Reco+: unreadable recommendation ledger ({Type}), starting from scratch", ex.GetType().Name);
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
