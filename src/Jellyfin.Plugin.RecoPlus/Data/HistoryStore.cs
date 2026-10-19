using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Date du premier lancement de chaque titre par chaque compte (film, ou série pour un épisode), relevée par le plugin
/// à chaque début de lecture. Jellyfin ne garde que la dernière date de lecture ; cette date-ci sert au calibrage.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly ILogger<HistoryStore> _log;
    private readonly ConcurrentDictionary<string, double> _first = new(StringComparer.Ordinal);
    private readonly Timer _flush;
    private int _dirty;
    private bool _loaded;

    public HistoryStore(ILogger<HistoryStore> log)
    {
        _log = log;
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static string PathOnDisk => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "premiers-lancements.json");

    private static string Key(Guid user, Guid title) => user.ToString("N") + "|" + title.ToString("N");

    /// <summary>Enregistre un lancement (garde la date la plus ancienne).</summary>
    public void Record(Guid user, Guid title, DateTime whenUtc)
    {
        EnsureLoaded();
        double t = new DateTimeOffset(DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;
        _first.AddOrUpdate(Key(user, title), t, (_, old) => Math.Min(old, t));
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Premier lancement connu (secondes Unix), ou 0.</summary>
    public double First(Guid user, Guid title)
    {
        EnsureLoaded();
        return _first.TryGetValue(Key(user, title), out var v) ? v : 0;
    }

    /// <summary>Écrit sur disque si nécessaire (atomique).</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 || Plugin.Instance is null)
        {
            return;
        }

        try
        {
            var tmp = PathOnDisk + ".tmp";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, double>(_first)));
            File.Move(tmp, PathOnDisk, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _log.LogWarning(ex, "Reco+ : écriture de l'historique des premiers lancements impossible");
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded || Plugin.Instance is null)
        {
            return;
        }

        lock (_first)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(PathOnDisk))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PathOnDisk));
                    if (d is not null)
                    {
                        foreach (var (k, v) in d)
                        {
                            _first.TryAdd(k, v);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Reco+ : historique des premiers lancements illisible, repart de zéro");
            }

            _loaded = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _flush.Dispose();
        Flush();
    }
}
