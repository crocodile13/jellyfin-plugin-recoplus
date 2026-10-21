using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>
/// Ajoute la balise du script client (titres des rangées « Parce que … », affichages, titres hors bibliothèque) à
/// index.html au moment de la requête, sans écrire dans le dossier web (même méthode que Jellyfin Enhanced). Inactif
/// plugin coupé. Toute erreur : la page d'origine est servie telle quelle.
/// </summary>
public sealed class ScriptInjection : IStartupFilter
{
    private const string Marker = "RecoPlus/client.js";

    // version dans l'URL : un cache devant Jellyfin (ou celui du navigateur) ne peut pas garder l'ancien script après une mise à jour
    private static readonly string Version = Uri.EscapeDataString(typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0");
    private readonly ILogger<ScriptInjection> _log;

    public ScriptInjection(ILogger<ScriptInjection> log) => _log = log;

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    private static bool IsIndex(string? p)
        => p is not null && (p.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                             || p.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                             || p.EndsWith("/web", StringComparison.OrdinalIgnoreCase));

    private async Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) || !IsIndex(ctx.Request.Path.Value) || !CompatibilityGuard.Allowed(Feature.ClientScript))
        {
            await next().ConfigureAwait(false);
            return;
        }

        ctx.Request.Headers.Remove("Accept-Encoding");
        ctx.Request.Headers.Remove("Range");
        ctx.Request.Headers.Remove("If-Range");
        ctx.Request.Headers.Remove("If-None-Match");
        ctx.Request.Headers.Remove("If-Modified-Since");
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            ctx.Response.Body = original;
            throw;
        }

        ctx.Response.Body = original;
        buffer.Position = 0;
        bool html = ctx.Response.StatusCode == 200 && (ctx.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!html)
        {
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        string page = Encoding.UTF8.GetString(buffer.ToArray());
        try
        {
            int close = page.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0 && page.IndexOf(Marker, StringComparison.Ordinal) < 0)
            {
                page = page[..close] + "<script defer src=\"../" + Marker + "?v=" + Version + "\"></script>\n" + page[close..];   // i18n:skip
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Reco+ : injection du script impossible ({Msg}), page d'origine servie", ex.Message);
        }

        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html;charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers.Remove("ETag");
        ctx.Response.Headers.Remove("Last-Modified");
        ctx.Response.Headers.Remove("Accept-Ranges");
        await original.WriteAsync(bytes).ConfigureAwait(false);
    }
}
