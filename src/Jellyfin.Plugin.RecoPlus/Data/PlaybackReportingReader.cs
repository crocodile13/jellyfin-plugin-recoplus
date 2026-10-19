using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Lecture facultative, en lecture seule, de la base du plugin Playback Reporting : date du premier lancement de
/// chaque élément par chaque compte. Absente ou illisible : le plugin s'en passe (le calibrage est moins précis).
/// </summary>
public sealed class PlaybackReportingReader
{
    private readonly IApplicationPaths _paths;
    private readonly ILogger<PlaybackReportingReader> _log;

    public PlaybackReportingReader(IApplicationPaths paths, ILogger<PlaybackReportingReader> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>Chemin utilisé (configuré ou détecté), null si introuvable.</summary>
    public string? ResolvePath()
    {
        var configured = Plugin.Instance?.Configuration.PlaybackReportingDbPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var guess = Path.Combine(_paths.DataPath, "playback_reporting.db");
        return File.Exists(guess) ? guess : null;
    }

    /// <summary>(utilisateur, élément) → premier lancement (secondes Unix). Vide si indisponible.</summary>
    public Dictionary<(Guid User, Guid Item), double> FirstStarts(out string status)
    {
        var res = new Dictionary<(Guid, Guid), double>();
        var path = ResolvePath();
        if (path is null)
        {
            status = "absente";
            return res;
        }

        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT UserId, ItemId, MIN(DateCreated) FROM PlaybackActivity GROUP BY UserId, ItemId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1) || r.IsDBNull(2)
                    || !Guid.TryParse(r.GetString(0), out var u) || !Guid.TryParse(r.GetString(1), out var i))
                {
                    continue;
                }

                var raw = r.GetString(2);
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    res[(u, i)] = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
                }
            }

            status = $"lue ({res.Count} entrées)";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or TypeLoadException or InvalidOperationException or DllNotFoundException)
        {
            _log.LogWarning("Reco+ : base Playback Reporting illisible ({Type}: {Msg}), ignorée", ex.GetType().Name, ex.Message);
            status = "illisible : " + ex.GetType().Name;
            res.Clear();
        }

        return res;
    }
}
