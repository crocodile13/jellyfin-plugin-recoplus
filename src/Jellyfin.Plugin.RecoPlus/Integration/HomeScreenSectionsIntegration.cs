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
using MediaBrowser.Common.Configuration;
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

    /// <summary>
    /// Toute autre rangée (tendances, populaires, à venir, moitiés « films » et « séries » d'une rangée séparée) :
    /// l'identifiant de la rangée arrive dans la donnée attachée à la section lors de sa déclaration.
    /// </summary>
    public QueryResult<BaseItemDto> GetRow(HssPayload payload)
        => HomeScreenSectionsIntegration.IsRowId(payload.AdditionalData) ? Row(payload, payload.AdditionalData!) : new QueryResult<BaseItemDto>();

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
    public static List<(string Id, string Title, string Method, string Row)> Sections(Configuration.PluginConfiguration cfg, string? lang = null)
    {
        lang ??= I18n.Lang();
        var res = new List<(string, string, string, string)>();
        // les listes TMDb seulement avec Seerr
        foreach (var b in HomeRows.BaseRows(cfg.SeerrEnabled))
        {
            foreach (var row in HomeRows.Expand(b, cfg.KindOf(b)))
            {
                res.Add((Prefix + row, DisplayTitle(cfg, row, null, lang), MethodOf(row), row));
            }
        }

        return res;
    }

    /// <summary>Méthode d'une rangée : celle de toujours pour les rangées d'origine (déclarations existantes), sinon GetRow.</summary>
    private static string MethodOf(string row) => row switch
    {
        HomeRows.PourToi => nameof(HssSectionHandler.GetResults),
        HomeRows.Nouveautes => nameof(HssSectionHandler.GetNew),
        HomeRows.Films => nameof(HssSectionHandler.GetMovies),
        HomeRows.Series => nameof(HssSectionHandler.GetSeries),
        "ParceQue1" => nameof(HssSectionHandler.GetBecause1),
        "ParceQue2" => nameof(HssSectionHandler.GetBecause2),
        "ParceQue3" => nameof(HssSectionHandler.GetBecause3),
        _ => nameof(HssSectionHandler.GetRow),
    };

    /// <summary>Identifiant de rangée acceptable (lettres et chiffres).</summary>
    public static bool IsRowId(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 40 && s.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Titre affiché d'une rangée : celui de sa rangée de base, avec « films » ou « séries » pour une moitié de rangée
    /// séparée ; pour « Parce que … », le titre aimé (<paramref name="anchor"/>) ou la forme générique.
    /// </summary>
    public static string DisplayTitle(Configuration.PluginConfiguration cfg, string row, string? anchor, string lang)
    {
        // moitiés de la suite du classement : leurs titres de toujours
        if (row is HomeRows.Films or HomeRows.Series)
        {
            return RowTitle(cfg, row, lang);
        }

        var (b, sfx) = HomeRows.SplitOf(row);

        bool because = b.StartsWith(HomeRows.ParceQue, StringComparison.Ordinal);
        string Fill(string tpl) => !because ? tpl : anchor is null ? WithoutAnchor(tpl) : WithAnchor(tpl, anchor);

        // moitié d'une rangée séparée : son propre titre s'il est saisi
        if (sfx is { } half && cfg.SplitTitle(HomeRows.KindOfRow(b), half) is { } custom && !string.IsNullOrWhiteSpace(custom))
        {
            return Fill(custom.Trim());
        }

        string t = Fill(because ? Title(cfg.RowBecauseTitle, DefaultBecause, lang) : RowTitle(cfg, b, lang));
        return sfx switch
        {
            KindFilter.Movies => Texts.T("{0} : films", lang, t),
            KindFilter.Series => Texts.T("{0} : séries", lang, t),
            _ => t,
        };
    }

    /// <summary>Titres par défaut des rangées (français : la clé de traduction ; voir <see cref="Texts"/>).</summary>
    public const string DefaultPourToi = "Pour toi", DefaultNew = "Nouveautés pour toi", DefaultMovies = "Films pour toi",
        DefaultSeries = "Séries pour toi", DefaultBecause = "Parce que tu as aimé {titre}", DefaultExtended = "À découvrir pour toi",
        DefaultTrending = "Tendances", DefaultSuite = "Encore pour toi", DefaultPopular = "Populaires", DefaultUpcoming = "À venir";

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
        HomeRows.Tendances => Title(cfg.RowTrendingTitle, DefaultTrending, lang),
        HomeRows.Populaires => Title(cfg.RowPopularTitle, DefaultPopular, lang),
        HomeRows.AVenir => Title(cfg.RowUpcomingTitle, DefaultUpcoming, lang),
        HomeRows.Suite => Title(cfg.RowSuiteTitle, DefaultSuite, lang),
        _ => Title(cfg.HssSectionTitle, DefaultPourToi, lang),
    };

    /// <summary>Titre de la rangée à part du mode étendu (sans Home Screen Sections).</summary>
    public static string ExtendedTitle(Configuration.PluginConfiguration cfg, string lang) => Title(cfg.ExtendedTitle, DefaultExtended, lang);

    /// <summary>Repère du titre aimé dans le modèle des rangées « Parce que … » : {titre} ou {title}.</summary>
    private static readonly string[] Anchors = { "{titre}", "{title}" };

    /// <summary>Titre d'une rangée « Parce que … » autour d'un titre aimé.</summary>
    public static string Because(Configuration.PluginConfiguration cfg, string anchor, string lang)
        => WithAnchor(Title(cfg.RowBecauseTitle, DefaultBecause, lang), anchor);

    /// <summary>Titre générique des rangées « Parce que … » (clients sans le script web : TV, applications mobiles).</summary>
    public static string BecauseGeneric(Configuration.PluginConfiguration cfg, string lang)
        => WithoutAnchor(Title(cfg.RowBecauseTitle, DefaultBecause, lang));

    /// <summary>Modèle « … {titre} … » rempli avec le titre aimé.</summary>
    private static string WithAnchor(string t, string anchor)
    {
        foreach (var a in Anchors)
        {
            t = t.Replace(a, anchor, StringComparison.OrdinalIgnoreCase);
        }

        return t;
    }

    /// <summary>Modèle « … {titre} … » sans titre aimé : « Parce que tu as aimé… ».</summary>
    private static string WithoutAnchor(string t)
    {
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
    private readonly IServiceProvider _services;
    private readonly IUserManager _users;
    private readonly IApplicationPaths _paths;

    public HomeScreenSectionsIntegration(ILogger<HomeScreenSectionsIntegration> log, Notifier notifier, IServiceProvider services, IUserManager users,
        IApplicationPaths paths)
    {
        _log = log;
        _notifier = notifier;
        _services = services;
        _users = users;
        _paths = paths;
    }

    /// <summary>
    /// Comptes qui ont enregistré leur propre choix de sections dans Home Screen Sections (fichier
    /// <c>ModularHomeSettings.json</c>) ; les autres suivent le tableau de l'administrateur.
    /// </summary>
    private HashSet<Guid> SavedHssUsers()
    {
        var res = new HashSet<Guid>();
        var file = System.IO.Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Plugin.HomeScreenSections", "ModularHomeSettings.json");
        try
        {
            if (System.IO.File.Exists(file))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("UserId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g))
                    {
                        res.Add(g);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _log.LogWarning("Reco+: could not read Home Screen Sections user settings ({Msg})", ex.Message);
        }

        return res;
    }

    /// <summary>
    /// Tableau des sections de l'administrateur (Home Screen Sections) : une rangée séparée ou réunie y prend la place de
    /// l'ancienne forme, avec ses réglages (ordre, affichage, droits). Sans cela, les comptes qui suivent le tableau par
    /// défaut perdraient la rangée jusqu'au prochain enregistrement de la page de HSS.
    /// </summary>
    /// <returns>Le tableau a changé.</returns>
    private bool ReconcileAdminTable(Assembly asm, IReadOnlyList<string> bases, Configuration.PluginConfiguration cfg)
    {
        var pt = asm.GetType("Jellyfin.Plugin.HomeScreenSections.HomeScreenSectionsPlugin");
        var inst = pt?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var conf = inst is null ? null : pt!.GetProperty("Configuration")?.GetValue(inst);
        var prop = conf?.GetType().GetProperty("SectionSettings");
        if (prop?.GetValue(conf) is not Array arr || arr.GetType().GetElementType() is not { } et || et.GetProperty("SectionId") is not { } idProp)
        {
            return false;
        }

        var items = arr.Cast<object>().ToList();
        string Id(object o) => idProp.GetValue(o) as string ?? string.Empty;
        bool changed = false;
        foreach (var b in bases)
        {
            var (remove, add) = SplitLayout.Replace(items.Select(Id).ToList(), Prefix, b, cfg.KindOf(b));
            if (remove.Count == 0)
            {
                continue;
            }

            int at = items.FindIndex(o => Id(o) == remove[0]);
            var copies = add.Select(a =>
            {
                var from = items.First(o => Id(o) == a.From);
                var copy = Activator.CreateInstance(et)!;
                foreach (var p in et.GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
                {
                    p.SetValue(copy, p.GetValue(from));
                }

                idProp.SetValue(copy, a.Id);
                return copy;
            }).ToList();
            items.InsertRange(at, copies);
            items.RemoveAll(o => remove.Contains(Id(o)));
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        var res = Array.CreateInstance(et, items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            res.SetValue(items[i], i);
        }

        prop!.SetValue(conf, res);
        pt!.GetMethod("SaveConfiguration", Type.EmptyTypes)?.Invoke(inst, null);
        return true;
    }

    /// <summary>
    /// Rangées séparées ou réunies : l'accueil de chaque compte suit tout seul. Là où un compte affichait la rangée, il
    /// affiche ses deux moitiés (films, séries) à la même place, et inversement ; une rangée que le compte n'affichait
    /// pas reste absente. Rien n'est fait si Home Screen Sections n'a pas l'interface attendue.
    /// </summary>
    /// <returns>Nombre de comptes mis à jour.</returns>
    private int ReconcileSplits(Assembly asm, Configuration.PluginConfiguration cfg)
    {
        try
        {
            var t = asm.GetType("Jellyfin.Plugin.HomeScreenSections.Library.IHomeScreenManager");
            var mgr = t is null ? null : _services.GetService(t);
            var get = t?.GetMethod("GetUserSettings", new[] { typeof(Guid) });
            var upd = t?.GetMethods().FirstOrDefault(m => m.Name == "UpdateUserSettings" && m.GetParameters().Length == 2);
            if (mgr is null || get is null || upd is null)
            {
                return 0;
            }

            // toutes les rangées séparables, listes TMDb comprises (même Seerr coupé : l'accueil reste cohérent)
            var bases = HomeRows.BaseRows(true).ToList();
            if (ReconcileAdminTable(asm, bases, cfg))
            {
                _log.LogInformation("Reco+: Home Screen Sections table updated after a row was split or merged");
            }

            // seulement les comptes qui ont leur propre choix : les enregistrer tous figerait ceux qui suivent le tableau
            var saved = SavedHssUsers();
            int changed = 0;
            foreach (var u in _users.GetUsers().Where(u => saved.Contains(u.Id)))
            {
                var settings = get.Invoke(mgr, new object[] { u.Id });
                if (settings?.GetType().GetProperty("EnabledSections")?.GetValue(settings) is not List<string> list)
                {
                    continue;
                }

                bool any = false;
                foreach (var b in bases)
                {
                    any |= SplitLayout.Apply(list, Prefix, b, cfg.KindOf(b));
                }

                if (any)
                {
                    upd.Invoke(mgr, new[] { u.Id, settings });
                    changed++;
                }
            }

            if (changed > 0)
            {
                _log.LogInformation("Reco+: home rows of {N} account(s) updated after a row was split or merged", changed);
            }

            return changed;
        }
        catch (Exception ex) when (ex is TargetInvocationException or TargetException or InvalidOperationException or InvalidCastException or ArgumentException or MissingMethodException or MemberAccessException)
        {
            _log.LogWarning("Reco+: could not update the accounts' home rows ({Msg})", (ex.InnerException ?? ex).Message);
            return 0;
        }
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
            foreach (var (id, title, method, row) in sections)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    id,
                    displayText = title,
                    limit = 1,
                    additionalData = row,
                    resultsAssembly = typeof(HssSectionHandler).Assembly.FullName,
                    resultsClass = typeof(HssSectionHandler).FullName,
                    resultsMethod = method,
                });
                register.Invoke(null, new[] { parse.Invoke(null, new object[] { payload }) });
            }

            Registered = true;
            ReconcileSplits(asm, cfg);
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
