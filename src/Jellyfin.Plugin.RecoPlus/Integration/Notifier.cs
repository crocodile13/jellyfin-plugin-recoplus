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
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>Alerte affichée en bannière sur la page de configuration (titre et message emballés : <see cref="Texts.Pack"/>).</summary>
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

    /// <summary>Lève une alerte (titre et message emballés ou non : rendus dans la langue de chaque destinataire).</summary>
    public void Raise(string key, string title, string message)
    {
        _alerts[key] = new Alert(key, title, message, DateTime.UtcNow);
        _log.LogWarning("Reco+: {Title} — {Message}", Texts.Render(title, "en"), Texts.Render(message, "en"));
        if (_sent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(24))
        {
            return;
        }

        _sent[key] = DateTime.UtcNow;
        // journal d'activité et webhook : langue du serveur (pas de lecteur connu)
        var lang = I18n.Lang();
        _ = Task.Run(() => SendAsync(Texts.Render(title, lang), Texts.Render(message, lang)));
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
            _log.LogWarning(ex, "Reco+: could not write to the activity log");
        }

        await SendWebhookAsync(title, message).ConfigureAwait(false);
    }

    /// <summary>État renvoyé par <see cref="SendWebhookAsync"/> quand le webhook a accepté le message.</summary>
    public const string Sent = "envoyé";

    /// <summary>Texte d'en-tête HTTP : tel quel s'il est en ASCII, sinon encodé « =?UTF-8?B?…?= » (RFC 2047).</summary>
    internal static string HeaderText(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        foreach (var ch in s)
        {
            if (ch > 126 || ch < 32)
            {
                return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
            }
        }

        return s;
    }

    /// <summary>Envoie au webhook configuré ; renvoie l'état (emballé), pour le bouton « tester ».</summary>
    /// <param name="kindOverride">Valeurs saisies non enregistrées (bouton « tester ») ; null = valeurs enregistrées.</param>
    public async Task<string> SendWebhookAsync(string title, string message, WebhookKind? kindOverride = null, string? urlOverride = null, string? tokenOverride = null)
    {
        var kind = kindOverride ?? Plugin.Instance?.Configuration.Webhook ?? WebhookKind.None;
        var url = string.IsNullOrWhiteSpace(urlOverride) ? _secrets.Get(SecretStore.WebhookUrl) : urlOverride.Trim();
        var token = string.IsNullOrWhiteSpace(tokenOverride) ? _secrets.Get(SecretStore.WebhookToken) : tokenOverride.Trim();
        if (kind == WebhookKind.None || url is null)
        {
            return Texts.Pack("aucun webhook configuré");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            return Texts.Pack("URL de webhook invalide");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            switch (kind)
            {
                case WebhookKind.Ntfy:
                    req.Content = new StringContent(message, Encoding.UTF8, "text/plain");
                    req.Headers.Add("Title", HeaderText("Reco+ : " + title)); // accents : en-tête encodé (RFC 2047), compris par ntfy
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
            return res.IsSuccessStatusCode ? Sent : Texts.Pack("refusé ({0})", (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            _log.LogWarning("Reco+: webhook failed ({Type})", ex.GetType().Name);
            return Texts.Pack("échec : {0}", ex.GetType().Name);
        }
    }
}
