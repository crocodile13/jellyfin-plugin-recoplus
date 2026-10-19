using System;
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

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>Charge utile envoyée par Home Screen Sections (désérialisée par lui, par nom de propriété).</summary>
public sealed class HssPayload
{
    public Guid UserId { get; set; }

    public string? AdditionalData { get; set; }
}

/// <summary>Contenu de la rangée « Pour toi » (appelé par Home Screen Sections, instancié par son injecteur).</summary>
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

    /// <summary>Rangée : épinglés puis liste classée, 20 titres, droits du compte appliqués.</summary>
    public QueryResult<BaseItemDto> GetResults(HssPayload payload)
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

            var ids = r.Pinned.Concat(r.Ranked).Distinct().Take(60).Select(x => Guid.ParseExact(x, "N")).ToArray();
            var allowed = _lib.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = ids,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
            }).ToDictionary(i => i.Id);
            var items = ids.Where(allowed.ContainsKey).Select(x => allowed[x]).Take(20).ToList();
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
    /// <summary>Identifiant de la rangée.</summary>
    public const string SectionId = "RecoPlusPourToi";

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

            var payload = JsonSerializer.Serialize(new
            {
                id = SectionId,
                displayText = cfg.HssSectionTitle,
                limit = 1,
                resultsAssembly = typeof(HssSectionHandler).Assembly.FullName,
                resultsClass = typeof(HssSectionHandler).FullName,
                resultsMethod = nameof(HssSectionHandler.GetResults),
            });
            register.Invoke(null, new[] { parse.Invoke(null, new object[] { payload }) });
            Registered = true;
            State = $"rangée « {cfg.HssSectionTitle} » déclarée (à activer dans la configuration de Home Screen Sections)";
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
