using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.RecoPlus.Configuration;
using Jellyfin.Plugin.RecoPlus.Data;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>Alerte affichée en bannière sur la page de configuration.</summary>
public sealed record Alert(string Key, string Title, string Message, DateTime At);

/// <summary>
/// Prévient l'administrateur quand une partie du plugin se désactive (incompatibilité de version, erreurs répétées) :
/// journal d'activité de Jellyfin, bannière rouge sur la page du plugin, webhook facultatif. Une même alerte n'est
/// renvoyée qu'une fois par 24 h.
/// </summary>
public sealed class Notifier
{
    private readonly IActivityManager _activity;
    private readonly IHttpClientFactory _http;
    private readonly SecretStore _secrets;
    private readonly ILogger<Notifier> _log;
    private readonly ConcurrentDictionary<string, Alert> _alerts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _sent = new(StringComparer.Ordinal);

    public Notifier(IActivityManager activity, IHttpClientFactory http, SecretStore secrets, ILogger<Notifier> log)
    {
        _activity = activity;
        _http = http;
        _secrets = secrets;
        _log = log;
    }

    /// <summary>Alertes actives (bannière).</summary>
    public IReadOnlyList<Alert> Active => _alerts.Values.OrderBy(a => a.At).ToList();

    /// <summary>Retire une alerte devenue sans objet.</summary>
    public void Clear(string key) => _alerts.TryRemove(key, out _);

    /// <summary>Lève une alerte.</summary>
    public void Raise(string key, string title, string message)
    {
        _alerts[key] = new Alert(key, title, message, DateTime.UtcNow);
        _log.LogWarning("Reco+ : {Title} — {Message}", title, message);
        if (_sent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(24))
        {
            return;
        }

        _sent[key] = DateTime.UtcNow;
        _ = Task.Run(() => SendAsync(title, message));
    }

    private async Task SendAsync(string title, string message)
    {
        try
        {
            await _activity.CreateAsync(new ActivityLog("Reco+ : " + title, "RecoPlusAlert", Guid.Empty)
            {
                Overview = message,
                ShortOverview = title,
                LogSeverity = LogLevel.Warning,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reco+ : écriture dans le journal d'activité impossible");
        }

        await SendWebhookAsync(title, message).ConfigureAwait(false);
    }

    /// <summary>Envoie au webhook configuré (renvoie un message d'état, pour le bouton « tester »).</summary>
    /// <param name="kindOverride">Valeurs saisies non enregistrées (bouton « tester ») ; null = valeurs enregistrées.</param>
    public async Task<string> SendWebhookAsync(string title, string message, WebhookKind? kindOverride = null, string? urlOverride = null, string? tokenOverride = null)
    {
        var kind = kindOverride ?? Plugin.Instance?.Configuration.Webhook ?? WebhookKind.None;
        var url = string.IsNullOrWhiteSpace(urlOverride) ? _secrets.Get(SecretStore.WebhookUrl) : urlOverride.Trim();
        var token = string.IsNullOrWhiteSpace(tokenOverride) ? _secrets.Get(SecretStore.WebhookToken) : tokenOverride.Trim();
        if (kind == WebhookKind.None || url is null)
        {
            return "aucun webhook configuré";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return "URL de webhook invalide";
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            switch (kind)
            {
                case WebhookKind.Ntfy:
                    req.Content = new StringContent(message, Encoding.UTF8, "text/plain");
                    req.Headers.Add("Title", "Reco+ : " + title);
                    req.Headers.Add("Priority", "high");
                    req.Headers.Add("Tags", "warning");
                    if (token is { } nt)
                    {
                        req.Headers.Add("Authorization", "Bearer " + nt);
                    }

                    break;
                case WebhookKind.Gotify:
                    req.Content = new StringContent(JsonSerializer.Serialize(new { title = "Reco+ : " + title, message, priority = 7 }), Encoding.UTF8, "application/json");
                    if (token is { } gt)
                    {
                        req.Headers.Add("X-Gotify-Key", gt);
                    }

                    break;
                case WebhookKind.Discord:
                    req.Content = new StringContent(JsonSerializer.Serialize(new { content = $"**Reco+ : {title}**\n{message}" }), Encoding.UTF8, "application/json");
                    break;
                default:
                    req.Content = new StringContent(JsonSerializer.Serialize(new { title = "Reco+ : " + title, message, level = "warning" }), Encoding.UTF8, "application/json");
                    break;
            }

            var http = _http.CreateClient("RecoPlusWebhook");
            http.Timeout = TimeSpan.FromSeconds(15);
            using var res = await http.SendAsync(req).ConfigureAwait(false);
            return res.IsSuccessStatusCode ? "envoyé" : $"refusé ({(int)res.StatusCode})";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            _log.LogWarning("Reco+ : webhook en échec ({Type})", ex.GetType().Name);
            return "échec : " + ex.GetType().Name;
        }
    }
}
