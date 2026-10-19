using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RecoPlus.Data;
using Jellyfin.Plugin.RecoPlus.Integration;
using Jellyfin.Plugin.RecoPlus.Seerr;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Api;

/// <summary>Demande d'un titre du mode étendu.</summary>
public sealed class RequestBody
{
    public string MediaType { get; set; } = string.Empty;

    public int TmdbId { get; set; }
}

/// <summary>Test de Seerr avec les valeurs du formulaire.</summary>
public sealed class SeerrTestBody
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }
}

/// <summary>Test du webhook avec les valeurs du formulaire.</summary>
public sealed class WebhookTestBody
{
    public Configuration.WebhookKind? Kind { get; set; }

    public string? Url { get; set; }

    public string? Token { get; set; }
}

/// <summary>Écriture d'un secret (jamais relu par l'API).</summary>
public sealed class SecretBody
{
    public string Name { get; set; } = string.Empty;

    public string? Value { get; set; }
}

/// <summary>API du plugin. Tout ce qui touche à la configuration exige un administrateur.</summary>
[ApiController]
[Route("RecoPlus")]
public sealed class RecoController : ControllerBase
{
    private readonly RecoService _reco;
    private readonly SeerrService _seerr;
    private readonly SecretStore _secrets;
    private readonly Notifier _notifier;
    private readonly CalibrationService _calib;
    private readonly IUserManager _users;
    private readonly System.Net.Http.IHttpClientFactory _httpFactory;

    public RecoController(RecoService reco, SeerrService seerr, SecretStore secrets, Notifier notifier, CalibrationService calib, IUserManager users,
        System.Net.Http.IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
        _reco = reco;
        _seerr = seerr;
        _secrets = secrets;
        _notifier = notifier;
        _calib = calib;
        _users = users;
    }

    private Guid? CurrentUser => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var g) ? g : null;

    /// <summary>Script client (rangée du mode étendu). Public, comme tout fichier de l'interface web.</summary>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult ClientScript()
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.client.js");
        if (s is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(s, "application/javascript; charset=utf-8");
    }

    /// <summary>Mode étendu du compte connecté.</summary>
    [HttpGet("Extended")]
    [Authorize]
    public ActionResult Extended()
    {
        var cfg = Plugin.Instance!.Configuration;
        var me = CurrentUser;
        if (me is null)
        {
            return Unauthorized();
        }

        var r = _reco.For(me.Value);
        bool on = cfg.Enabled && cfg.ExtendedEnabled && _seerr.Configured;
        bool hss = cfg.HssSection && HomeScreenSectionsIntegration.Registered && CompatibilityGuard.Allowed(Feature.HomeScreenSection);
        return Ok(new
        {
            enabled = on,
            homeRow = on && cfg.ExtendedHomeRow && CompatibilityGuard.Allowed(Feature.ClientScript),
            hss,
            mixMode = cfg.ExtendedMixMode == Configuration.MixMode.Score ? "score" : "interleave",
            mixEvery = Math.Clamp(cfg.ExtendedMixEvery, 2, 20),
            skip = Math.Min(r?.Pinned.Count ?? 0, 20),
            title = cfg.ExtendedTitle,
            canRequest = on && cfg.ExtendedRequestButton && (r?.SeerrUserId ?? 0) > 0,
            seerrUrl = string.IsNullOrWhiteSpace(cfg.SeerrPublicUrl) ? cfg.SeerrUrl.TrimEnd('/') : cfg.SeerrPublicUrl.TrimEnd('/'),
            items = on ? (object)(r?.Extended ?? new List<ExternalRec>()) : Array.Empty<object>(),
        });
    }

    /// <summary>Demande un titre du mode étendu, au nom du compte connecté (ses droits et quotas Seerr).</summary>
    [HttpPost("Request")]
    [Authorize]
    public async Task<ActionResult> RequestTitle([FromBody] RequestBody body, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var me = CurrentUser;
        if (me is null)
        {
            return Unauthorized();
        }

        if (!cfg.Enabled || !cfg.ExtendedEnabled || !cfg.ExtendedRequestButton || !_seerr.Configured)
        {
            return BadRequest(new { message = "demandes désactivées" });
        }

        var r = _reco.For(me.Value);
        if (r is null || r.SeerrUserId <= 0)
        {
            return BadRequest(new { message = "compte non lié à Seerr" });
        }

        // seuls les titres effectivement proposés à ce compte peuvent être demandés par cette voie
        if (!r.Extended.Any(e => e.MediaType == body.MediaType && e.TmdbId == body.TmdbId))
        {
            return BadRequest(new { message = "titre non proposé" });
        }

        try
        {
            var id = await _seerr.Client().RequestAsync(r.SeerrUserId, body.MediaType, body.TmdbId, ct).ConfigureAwait(false);
            r.Extended = r.Extended.Where(e => !(e.MediaType == body.MediaType && e.TmdbId == body.TmdbId)).ToList();
            return Ok(new { requestId = id, message = "demandé" });
        }
        catch (SeerrException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Seerr injoignable" });
        }
    }

    // ------------------------------------------------------------------ administration

    /// <summary>État complet pour la page de configuration (sans aucun secret).</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Status()
    {
        var g = CompatibilityGuard.Instance;
        return Ok(new
        {
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            engine = _reco.Status,
            alerts = _notifier.Active,
            checks = g?.Checks.Select(c => new { check = c.Check, ok = c.Ok, detail = c.Detail }),
            disabled = g?.Disabled.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            hss = HomeScreenSectionsIntegration.State,
            secrets = new
            {
                seerrApiKey = _secrets.Describe(SecretStore.SeerrApiKey),
                webhookUrl = _secrets.Get(SecretStore.WebhookUrl) is null ? "non défini" : "défini",
                webhookToken = _secrets.Describe(SecretStore.WebhookToken),
            },
            calibration = new
            {
                running = _calib.Running,
                progress = _calib.Progress,
                error = _calib.Error,
                at = _calib.ReportAt,
                report = _calib.Report is null ? null : new
                {
                    current = new { hit10 = _calib.Report.Current.Hit10, hit20 = _calib.Report.Current.Hit20, gain = _calib.Report.Current.Gain, events = _calib.Report.Current.Events, accounts = _calib.Report.Current.Accounts },
                    proposed = new { hit10 = _calib.Report.Proposed.Hit10, hit20 = _calib.Report.Proposed.Hit20, gain = _calib.Report.Proposed.Gain },
                    cvDelta = _calib.Report.CvDelta,
                    ciLow = _calib.Report.DeltaCi.Low,
                    ciHigh = _calib.Report.DeltaCi.High,
                    recommended = _calib.Report.Recommended,
                    verdict = _calib.Report.Verdict,
                    steps = _calib.Report.Steps.Where(s => s.Kept),
                    settings = _calib.Report.ProposedSettings,
                },
            },
            canRollback = Plugin.Instance!.Configuration.PreviousEngine is not null,
            users = _users.GetUsers().Select(u => new { id = u.Id.ToString("N"), name = u.Username, ready = _reco.For(u.Id) is not null }),
        });
    }

    /// <summary>Genres de la bibliothèque (choix des genres exclus).</summary>
    [HttpGet("Genres")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Genres() => Ok(_reco.Genres());

    /// <summary>Aperçu des 20 premiers titres d'un compte.</summary>
    [HttpGet("Preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Preview([FromQuery] Guid userId)
    {
        var r = _reco.For(userId);
        return Ok(new { items = _reco.Preview(userId), extended = r?.Extended.Take(10).Select(e => $"{e.Name} ({e.Year}) — parce que : {e.Because}"), computedAt = r?.ComputedAt });
    }

    /// <summary>Recalcul complet immédiat (en arrière-plan).</summary>
    [HttpPost("Recompute")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Recompute()
    {
        RecoWorker.Instance?.ScheduleAll(TimeSpan.Zero);   // → tâche planifiée « Recalcul des recommandations »
        return Accepted();
    }

    /// <summary>Rejoue les vérifications de compatibilité.</summary>
    [HttpPost("Checks")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Checks()
    {
        CompatibilityGuard.Instance?.RunChecks();
        return NoContent();
    }

    /// <summary>Définit ou efface un secret.</summary>
    [HttpPost("Secret")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Secret([FromBody] SecretBody body)
    {
        if (body.Name is not (SecretStore.SeerrApiKey or SecretStore.WebhookUrl or SecretStore.WebhookToken))
        {
            return BadRequest(new { message = "secret inconnu" });
        }

        if (body.Value is { Length: > 2048 })
        {
            return BadRequest(new { message = "valeur trop longue" });
        }

        _secrets.Set(body.Name, body.Value);
        return Ok(new { state = _secrets.Describe(body.Name) });
    }

    /// <summary>
    /// Teste Seerr avec les valeurs saisies sur la page (sans les enregistrer) : URL du formulaire, clé saisie ou, à
    /// défaut, clé déjà enregistrée.
    /// </summary>
    [HttpPost("TestSeerr")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> TestSeerr([FromBody] SeerrTestBody body, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(body.Url) ? Plugin.Instance!.Configuration.SeerrUrl : body.Url.Trim();
        var key = string.IsNullOrWhiteSpace(body.ApiKey) ? _secrets.Get(SecretStore.SeerrApiKey) : body.ApiKey.Trim();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            return Ok(new { ok = false, message = "URL ou clé manquante" });
        }

        try
        {
            var http = _httpFactory.CreateClient("RecoPlusSeerr");
            http.Timeout = TimeSpan.FromSeconds(15);
            var cl = new SeerrClient(http, url, key);
            var version = await cl.CheckAsync(ct).ConfigureAwait(false);
            var users = await cl.UsersAsync(ct).ConfigureAwait(false);
            var linked = users.Select(u => u.JellyfinUserId).ToHashSet(StringComparer.Ordinal);
            var unmapped = _users.GetUsers().Where(u => !linked.Contains(u.Id.ToString("N"))).Select(u => u.Username).ToList();
            return Ok(new
            {
                ok = true,
                message = $"connecté à Seerr v{version} — {linked.Count} compte(s) lié(s) à Jellyfin"
                          + (unmapped.Count > 0 ? $" ; sans compte Seerr : {string.Join(", ", unmapped.Take(10))}{(unmapped.Count > 10 ? "…" : string.Empty)}" : string.Empty),
            });
        }
        catch (Exception ex) when (ex is SeerrException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Ok(new { ok = false, message = ex is TaskCanceledException ? "pas de réponse (URL injoignable depuis le serveur Jellyfin ?)" : ex.Message });
        }
    }

    /// <summary>Envoie une notification de test avec les valeurs saisies (sans les enregistrer).</summary>
    [HttpPost("TestWebhook")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> TestWebhook([FromBody] WebhookTestBody body)
        => Ok(new { message = await _notifier.SendWebhookAsync("test", "Notification de test de Reco+.", body.Kind, body.Url, body.Token).ConfigureAwait(false) });

    /// <summary>Lance le calibrage.</summary>
    [HttpPost("Calibration/Start")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CalibrationStart()
    {
        if (_calib.Running)
        {
            return Conflict(new { message = "déjà en cours" });
        }

        _calib.Start();
        return Accepted();
    }

    /// <summary>Interrompt le calibrage.</summary>
    [HttpPost("Calibration/Cancel")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CalibrationCancel()
    {
        _calib.Cancel();
        return NoContent();
    }

    /// <summary>Applique le réglage proposé (l'actuel est gardé pour revenir en arrière).</summary>
    [HttpPost("Calibration/Apply")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CalibrationApply()
    {
        var rep = _calib.Report;
        if (rep is null)
        {
            return BadRequest(new { message = "aucun calibrage" });
        }

        var p = Plugin.Instance!;
        p.Configuration.PreviousEngine = p.Configuration.Engine.Clone();
        p.Configuration.Engine = rep.ProposedSettings.Sanitized();
        p.SaveConfiguration();
        RecoWorker.Instance?.ScheduleAll(TimeSpan.Zero);
        return Ok(new { message = "appliqué ; recalcul lancé" });
    }

    /// <summary>Revient au réglage précédent.</summary>
    [HttpPost("Calibration/Rollback")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CalibrationRollback()
    {
        var p = Plugin.Instance!;
        if (p.Configuration.PreviousEngine is null)
        {
            return BadRequest(new { message = "rien à restaurer" });
        }

        (p.Configuration.Engine, p.Configuration.PreviousEngine) = (p.Configuration.PreviousEngine, p.Configuration.Engine);
        p.SaveConfiguration();
        RecoWorker.Instance?.ScheduleAll(TimeSpan.Zero);
        return Ok(new { message = "réglage précédent restauré ; recalcul lancé" });
    }

    /// <summary>Remet les réglages du moteur par défaut.</summary>
    [HttpPost("Engine/Reset")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult EngineReset()
    {
        var p = Plugin.Instance!;
        p.Configuration.PreviousEngine = p.Configuration.Engine.Clone();
        p.Configuration.Engine = new EngineSettings();
        p.SaveConfiguration();
        RecoWorker.Instance?.ScheduleAll(TimeSpan.Zero);
        return Ok(new { message = "réglages par défaut ; recalcul lancé" });
    }
}
