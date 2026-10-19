using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>
/// Secrets (clé API Jellyseerr, URL et jeton du webhook) dans un fichier à part, droits 600, jamais renvoyés par l'API
/// (seul l'état « défini / non défini » et les 4 derniers caractères sont exposés).
/// </summary>
public sealed class SecretStore
{
    /// <summary>Clé API Jellyseerr.</summary>
    public const string SeerrApiKey = "SeerrApiKey";

    /// <summary>URL du webhook.</summary>
    public const string WebhookUrl = "WebhookUrl";

    /// <summary>Jeton du webhook (Gotify).</summary>
    public const string WebhookToken = "WebhookToken";

    private static readonly string[] Known = { SeerrApiKey, WebhookUrl, WebhookToken };
    private readonly ILogger<SecretStore> _log;
    private readonly object _lock = new();
    private Dictionary<string, string>? _cache;

    public SecretStore(ILogger<SecretStore> log) => _log = log;

    private static string PathOnDisk => Path.Combine(Plugin.Instance!.DataFolderPath, "secrets.json");

    /// <summary>Lit un secret (null si absent).</summary>
    public string? Get(string name)
    {
        lock (_lock)
        {
            Load();
            return _cache!.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;
        }
    }

    /// <summary>Écrit un secret (valeur vide = suppression). Écriture atomique, droits 600.</summary>
    public void Set(string name, string? value)
    {
        if (Array.IndexOf(Known, name) < 0)
        {
            throw new ArgumentException("secret inconnu", nameof(name));
        }

        lock (_lock)
        {
            Load();
            if (string.IsNullOrWhiteSpace(value))
            {
                _cache!.Remove(name);
            }
            else
            {
                _cache![name] = value.Trim();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            var tmp = PathOnDisk + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(fs, _cache);
            }

            File.Move(tmp, PathOnDisk, true);
        }
    }

    /// <summary>État affichable : « non défini » ou « défini (…1234) ».</summary>
    public string Describe(string name)
    {
        var v = Get(name);
        return v is null ? "non défini" : $"défini (…{(v.Length > 4 ? v[^4..] : "****")})";
    }

    private void Load()
    {
        if (_cache is not null)
        {
            return;
        }

        try
        {
            _cache = File.Exists(PathOnDisk)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathOnDisk)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Reco+ : fichier de secrets illisible ({Type}), secrets considérés absents", ex.GetType().Name);
            _cache = new();
        }
    }
}
