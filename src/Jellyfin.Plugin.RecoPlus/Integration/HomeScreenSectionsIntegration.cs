using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>Charge utile envoyée par Home Screen Sections (désérialisée par lui, par nom de propriété).</summary>
public sealed class HssPayload
{
    public Guid UserId { get; set; }

    public string? AdditionalData { get; set; }
}

/// <summary>Contenu des rangées Reco+ (appelé par Home Screen Sections, instancié par son injecteur).</summary>
public sealed class HssSectionHandler
{
    private readonly RecoService _reco;
    private readonly ILibraryManager _lib;
    private readonly IUserManager _users;
    private readonly IDtoService _dto;

    public HssSectionHandler(RecoService reco, ILibraryManager lib, IUserManager users, IDtoService dto)
    {
        _reco = reco;
        _lib = lib;
        _users = users;
        _dto = dto;
    }

    /// <summary>« Pour toi » : épinglés puis liste classée, droits du compte appliqués.</summary>
    public QueryResult<BaseItemDto> GetResults(HssPayload payload) => Row(payload, HomeRows.PourToi);

    /// <summary>« Films pour toi ».</summary>
    public QueryResult<BaseItemDto> GetMovies(HssPayload payload) => Row(payload, HomeRows.Films);

    /// <summary>« Séries pour toi ».</summary>
    public QueryResult<BaseItemDto> GetSeries(HssPayload payload) => Row(payload, HomeRows.Series);

    /// <summary>« Nouveautés pour toi ».</summary>
    public QueryResult<BaseItemDto> GetNew(HssPayload payload) => Row(payload, HomeRows.Nouveautes);

    /// <summary>Première rangée « Parce que tu as aimé … ».</summary>
    public QueryResult<BaseItemDto> GetBecause1(HssPayload payload) => Row(payload, HomeRows.ParceQue + 1);

    /// <summary>Deuxième rangée « Parce que tu as aimé … ».</summary>
    public QueryResult<BaseItemDto> GetBecause2(HssPayload payload) => Row(payload, HomeRows.ParceQue + 2);

    /// <summary>Troisième rangée « Parce que tu as aimé … ».</summary>
    public QueryResult<BaseItemDto> GetBecause3(HssPayload payload) => Row(payload, HomeRows.ParceQue + 3);

    private QueryResult<BaseItemDto> Row(HssPayload payload, string row)
    {
        try
        {
            if (!CompatibilityGuard.Allowed(Feature.HomeScreenSection))
            {
                return new QueryResult<BaseItemDto>();
            }

            var user = _users.GetUserById(payload.UserId);
            var r = user is null ? null : _reco.For(user.Id);
            if (user is null || r is null)
            {
                return new QueryResult<BaseItemDto>();
            }

            int size = Math.Clamp(Plugin.Instance?.Configuration.RowSize ?? 20, 5, 60);
            var src = row == HomeRows.PourToi && !r.Rows.ContainsKey(HomeRows.PourToi)
                ? r.Pinned.Concat(r.Ranked)                                   // cache d'une version précédente
                : r.Rows.GetValueOrDefault(row) ?? new List<string>();
            var ids = src.Distinct().Take(size * 3).Select(x => Guid.ParseExact(x, "N")).ToArray();
            if (ids.Length == 0)
            {
                return new QueryResult<BaseItemDto>();
            }

            var allowed = _lib.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = ids,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
            }).ToDictionary(i => i.Id);
            var items = ids.Where(allowed.ContainsKey).Select(x => allowed[x]).Take(size).ToList();
            var dtos = _dto.GetBaseItemDtos(items, new DtoOptions(), user);
            return new QueryResult<BaseItemDto>(0, dtos.Count, dtos);
        }
        catch (Exception ex)
        {
            CompatibilityGuard.Instance?.ReportError(Feature.HomeScreenSection, ex);
            return new QueryResult<BaseItemDto>();
        }
    }
}

/// <summary>
/// Déclare la rangée auprès de Home Screen Sections s'il est installé (par réflexion : aucune dépendance de
/// compilation ; absent ou d'une forme inattendue → rien n'est déclaré, le reste du plugin fonctionne).
/// </summary>
public sealed class HomeScreenSectionsIntegration : BackgroundService
{
    /// <summary>Identifiant de la rangée « Pour toi ».</summary>
    public const string SectionId = "RecoPlusPourToi";

    /// <summary>Préfixe des identifiants de section (l'identifiant de rangée suit).</summary>
    public const string Prefix = "RecoPlus";

    /// <summary>Sections à déclarer selon la configuration : (identifiant, titre affiché, méthode).</summary>
    public static List<(string Id, string Title, string Method)> Sections(Configuration.PluginConfiguration cfg)
    {
        var l = new List<(string, string, string)> { (SectionId, cfg.HssSectionTitle, nameof(HssSectionHandler.GetResults)) };
        if (cfg.RowNew)
        {
            l.Add((Prefix + HomeRows.Nouveautes, cfg.RowNewTitle, nameof(HssSectionHandler.GetNew)));
        }

        string because = (cfg.RowBecauseTitle ?? string.Empty).Replace("{titre}", "…", StringComparison.Ordinal).Trim();
        for (int i = 1; i <= Math.Clamp(cfg.RowBecause, 0, 3); i++)
        {
            l.Add((Prefix + HomeRows.ParceQue + i, because.Length > 0 ? because : "Parce que tu as aimé…", "GetBecause" + i));
        }

        if (cfg.RowMovies)
        {
            l.Add((Prefix + HomeRows.Films, cfg.RowMoviesTitle, nameof(HssSectionHandler.GetMovies)));
        }

        if (cfg.RowSeries)
        {
            l.Add((Prefix + HomeRows.Series, cfg.RowSeriesTitle, nameof(HssSectionHandler.GetSeries)));
        }

        return l;
    }

    private readonly ILogger<HomeScreenSectionsIntegration> _log;
    private readonly Notifier _notifier;

    public HomeScreenSectionsIntegration(ILogger<HomeScreenSectionsIntegration> log, Notifier notifier)
    {
        _log = log;
        _notifier = notifier;
    }

    /// <summary>État lisible.</summary>
    public static string State { get; private set; } = "non tenté";

    /// <summary>La rangée « Pour toi » est déclarée auprès de Home Screen Sections.</summary>
    public static bool Registered { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.HssSection)
        {
            State = "désactivée";
            return;
        }

        var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.HomeScreenSections");
        if (asm is null)
        {
            State = "Home Screen Sections absent";
            return;
        }

        try
        {
            var iface = asm.GetType("Jellyfin.Plugin.HomeScreenSections.PluginInterface");
            var register = iface?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
            var jobjType = register?.GetParameters().FirstOrDefault()?.ParameterType;
            var parse = jobjType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (register is null || jobjType is null || parse is null || register.GetParameters().Length != 1)
            {
                throw new InvalidOperationException("PluginInterface.RegisterSection(JObject) introuvable");
            }

            var sections = Sections(cfg);
            foreach (var (id, title, method) in sections)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    id,
                    displayText = title,
                    limit = 1,
                    resultsAssembly = typeof(HssSectionHandler).Assembly.FullName,
                    resultsClass = typeof(HssSectionHandler).FullName,
                    resultsMethod = method,
                });
                register.Invoke(null, new[] { parse.Invoke(null, new object[] { payload }) });
            }

            Registered = true;
            State = sections.Count == 1
                ? $"rangée « {cfg.HssSectionTitle} » déclarée (à activer dans la configuration de Home Screen Sections)"
                : $"{sections.Count} rangées déclarées : {string.Join(", ", sections.Select(x => "« " + x.Title + " »"))} (à activer dans la configuration de Home Screen Sections)";
            _notifier.Clear("off-" + Feature.HomeScreenSection);
            _log.LogInformation("Reco+ : rangée Home Screen Sections déclarée");
        }
        catch (Exception ex)
        {
            State = "échec : " + (ex.InnerException ?? ex).Message;
            CompatibilityGuard.Instance?.Disable(Feature.HomeScreenSection, "Home Screen Sections n'a pas l'interface attendue (" + (ex.InnerException ?? ex).Message + ").");
        }
    }
}
