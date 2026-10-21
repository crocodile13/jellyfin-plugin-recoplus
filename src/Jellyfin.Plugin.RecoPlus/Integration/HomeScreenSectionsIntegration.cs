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
    private static readonly ItemFields[] CardFields = { ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSourceCount };

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

            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || !cfg.Enabled)
            {
                return new QueryResult<BaseItemDto>();
            }

            var user = _users.GetUserById(payload.UserId);
            var r = user is null ? null : _reco.For(user.Id);
            if (user is null || r is null)
            {
                return new QueryResult<BaseItemDto>();
            }

            int size = Math.Clamp(cfg.RowSize, 5, 60);
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
            // champs utiles aux cartes seulement (comme les rangées de HSS) : pas d'acteurs, chapitres ni sources par titre
            var dtos = _dto.GetBaseItemDtos(items, new DtoOptions { Fields = CardFields }, user);
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

    /// <summary>
    /// Toutes les rangées Reco+, toujours déclarées (identifiant, titre affiché, méthode) : elles figurent dans la liste
    /// de Home Screen Sections, où l'on choisit lesquelles afficher, dans quel ordre et pour quels comptes.
    /// </summary>
    /// <param name="cfg">Configuration.</param>
    /// <param name="lang">Langue des titres par défaut (« fr », « en ») ; null = celle du serveur (rangées déclarées à HSS).</param>
    public static List<(string Id, string Title, string Method)> Sections(Configuration.PluginConfiguration cfg, string? lang = null)
    {
        lang ??= I18n.Lang();
        string because = BecauseGeneric(cfg, lang);
        return new List<(string, string, string)>
        {
            (SectionId, RowTitle(cfg, HomeRows.PourToi, lang), nameof(HssSectionHandler.GetResults)),
            (Prefix + HomeRows.Nouveautes, RowTitle(cfg, HomeRows.Nouveautes, lang), nameof(HssSectionHandler.GetNew)),
            (Prefix + HomeRows.ParceQue + 1, because, nameof(HssSectionHandler.GetBecause1)),
            (Prefix + HomeRows.ParceQue + 2, because, nameof(HssSectionHandler.GetBecause2)),
            (Prefix + HomeRows.ParceQue + 3, because, nameof(HssSectionHandler.GetBecause3)),
            (Prefix + HomeRows.Films, RowTitle(cfg, HomeRows.Films, lang), nameof(HssSectionHandler.GetMovies)),
            (Prefix + HomeRows.Series, RowTitle(cfg, HomeRows.Series, lang), nameof(HssSectionHandler.GetSeries)),
        };
    }

    /// <summary>Titres par défaut des rangées (français : la clé de traduction ; voir <see cref="Texts"/>).</summary>
    public const string DefaultPourToi = "Pour toi", DefaultNew = "Nouveautés pour toi", DefaultMovies = "Films pour toi",
        DefaultSeries = "Séries pour toi", DefaultBecause = "Parce que tu as aimé {titre}", DefaultExtended = "À découvrir pour toi";

    /// <summary>
    /// Titre choisi par l'administrateur, sinon le titre par défaut dans la langue voulue. Un titre resté égal au défaut
    /// français (configuration d'avant la traduction) est un titre par défaut : il suit la langue lui aussi.
    /// </summary>
    public static string Title(string? stored, string frDefault, string lang)
        => string.IsNullOrWhiteSpace(stored) || string.Equals(stored.Trim(), frDefault, StringComparison.Ordinal) ? Texts.T(frDefault, lang) : stored.Trim();

    /// <summary>Titre d'une rangée simple (« PourToi », « Nouveautes », « Films », « Series »).</summary>
    public static string RowTitle(Configuration.PluginConfiguration cfg, string row, string lang) => row switch
    {
        HomeRows.Nouveautes => Title(cfg.RowNewTitle, DefaultNew, lang),
        HomeRows.Films => Title(cfg.RowMoviesTitle, DefaultMovies, lang),
        HomeRows.Series => Title(cfg.RowSeriesTitle, DefaultSeries, lang),
        _ => Title(cfg.HssSectionTitle, DefaultPourToi, lang),
    };

    /// <summary>Titre de la rangée à part du mode étendu (sans Home Screen Sections).</summary>
    public static string ExtendedTitle(Configuration.PluginConfiguration cfg, string lang) => Title(cfg.ExtendedTitle, DefaultExtended, lang);

    /// <summary>Repère du titre aimé dans le modèle des rangées « Parce que … » : {titre} ou {title}.</summary>
    private static readonly string[] Anchors = { "{titre}", "{title}" };

    /// <summary>Titre d'une rangée « Parce que … » autour d'un titre aimé.</summary>
    public static string Because(Configuration.PluginConfiguration cfg, string anchor, string lang)
    {
        var t = Title(cfg.RowBecauseTitle, DefaultBecause, lang);
        foreach (var a in Anchors)
        {
            t = t.Replace(a, anchor, StringComparison.OrdinalIgnoreCase);
        }

        return t;
    }

    /// <summary>Titre générique des rangées « Parce que … » (clients sans le script web : TV, applications mobiles).</summary>
    public static string BecauseGeneric(Configuration.PluginConfiguration cfg, string lang)
    {
        var t = Title(cfg.RowBecauseTitle, DefaultBecause, lang);
        foreach (var a in Anchors)
        {
            int i = t.IndexOf(a, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                // « Parce que tu as aimé {titre} » → « Parce que tu as aimé… » (l'espace avant le repère disparaît)
                return (t[..i].TrimEnd() + "…" + t[(i + a.Length)..]).Trim();
            }
        }

        return t;
    }

    /// <summary>
    /// Rangées Reco+ que Home Screen Sections affiche à un compte (ses réglages, ou ceux par défaut de l'administrateur),
    /// lues par réflexion ; null si HSS est absent ou d'une forme inattendue (on remplit alors toutes les rangées).
    /// Sert à ne pas « réserver » des titres pour une rangée que le compte ne voit pas.
    /// </summary>
    public static HashSet<string>? EnabledFor(IServiceProvider sp, Guid user)
    {
        try
        {
            var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.HomeScreenSections");
            var t = asm?.GetType("Jellyfin.Plugin.HomeScreenSections.Library.IHomeScreenManager");
            var mgr = t is null ? null : sp.GetService(t);
            var get = t?.GetMethod("GetUserSettings", new[] { typeof(Guid) });
            var settings = mgr is null || get is null ? null : get.Invoke(mgr, new object[] { user });
            if (settings?.GetType().GetProperty("EnabledSections")?.GetValue(settings) is not System.Collections.IEnumerable list)
            {
                return null;
            }

            return list.OfType<string>().Where(x => x.StartsWith(Prefix, StringComparison.Ordinal)).Select(x => x[Prefix.Length..]).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or InvalidCastException or ArgumentException or MissingMethodException)
        {
            return null;
        }
    }

    private readonly ILogger<HomeScreenSectionsIntegration> _log;
    private readonly Notifier _notifier;

    public HomeScreenSectionsIntegration(ILogger<HomeScreenSectionsIntegration> log, Notifier notifier)
    {
        _log = log;
        _notifier = notifier;
    }

    /// <summary>État lisible.</summary>
    public static string State { get; private set; } = "non tenté";   // emballé (Texts.Pack) : traduit à l'affichage

    /// <summary>La rangée « Pour toi » est déclarée auprès de Home Screen Sections.</summary>
    public static bool Registered { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        Register();

        // titres ou rangées modifiés : déclarés à nouveau (HSS remplace la déclaration de même identifiant), sans redémarrage
        if (Plugin.Instance is { } p)
        {
            p.ConfigurationChanged += OnConfigChanged;
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // arrêt de Jellyfin
            }
            finally
            {
                p.ConfigurationChanged -= OnConfigChanged;
            }
        }
    }

    private void OnConfigChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => Register();

    private void Register()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
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
            State = Texts.Pack("{0} rangées déclarées ({1}) — à choisir et ordonner dans la configuration de Home Screen Sections", sections.Count, string.Join(" · ", sections.Select(x => x.Title).Distinct()));
            _notifier.Clear("off-" + Feature.HomeScreenSection);
            _log.LogInformation("Reco+: {N} rows registered with Home Screen Sections", sections.Count);
        }
        catch (Exception ex)
        {
            State = Texts.Pack("échec : {0}", (ex.InnerException ?? ex).Message);
            CompatibilityGuard.Instance?.Disable(Feature.HomeScreenSection, Texts.Pack("Home Screen Sections n'a pas l'interface attendue ({0}).", (ex.InnerException ?? ex).Message));
        }
    }
}
