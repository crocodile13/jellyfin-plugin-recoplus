using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

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

    /// <summary>Premier lancement de chaque élément par chaque compte, avec le type et le nom de l'élément. Vide si indisponible.</summary>
    public List<PlaybackStart> FirstStarts(out string status)
    {
        var res = new List<PlaybackStart>();
        var path = ResolvePath();
        if (path is null)
        {
            status = Texts.Pack("absente");
            return res;
        }

        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT UserId, ItemId, MAX(ItemType), MAX(ItemName), MIN(DateCreated) FROM PlaybackActivity GROUP BY UserId, ItemId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1) || r.IsDBNull(4)
                    || !Guid.TryParse(r.GetString(0), out var u) || !Guid.TryParse(r.GetString(1), out var i))
                {
                    continue;
                }

                var raw = r.GetString(4);
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                {
                    res.Add(new PlaybackStart(u, i, r.IsDBNull(2) ? string.Empty : r.GetString(2), r.IsDBNull(3) ? string.Empty : r.GetString(3),
                        new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds()));
                }
            }

            status = Texts.Pack("lue ({0} entrées)", res.Count);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or TypeLoadException or InvalidOperationException or DllNotFoundException)
        {
            _log.LogWarning("Reco+: unreadable Playback Reporting database ({Type}: {Msg}), ignored", ex.GetType().Name, ex.Message);
            status = Texts.Pack("illisible : {0}", ex.GetType().Name);
            res.Clear();
        }

        return res;
    }
}
