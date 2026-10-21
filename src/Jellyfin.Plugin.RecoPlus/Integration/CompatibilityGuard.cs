using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>Parties du plugin qui touchent à Jellyfin, désactivables une à une.</summary>
public enum Feature
{
    /// <summary>/Movies/Recommendations.</summary>
    MovieRecommendations,

    /// <summary>/Items/Suggestions.</summary>
    Suggestions,

    /// <summary>« Plus comme ceci ».</summary>
    Similar,

    /// <summary>Rangée Home Screen Sections.</summary>
    HomeScreenSection,

    /// <summary>Script client de l'accueil (titres des rangées, titres montrés, titres hors bibliothèque).</summary>
    ClientScript
}

/// <summary>
/// Garde-fou de compatibilité : vérifie au démarrage que les points d'accroche de Jellyfin sont ceux attendus, coupe
/// proprement une partie qui ne colle plus (Jellyfin reprend alors son comportement normal) et prévient.
/// Coupe aussi une partie qui enchaîne les erreurs (5 en 10 min) jusqu'au prochain redémarrage.
/// </summary>
public sealed class CompatibilityGuard : IHostedService
{
    private readonly ConcurrentDictionary<Feature, string> _disabled = new();
    private readonly ConcurrentDictionary<Feature, Queue<DateTime>> _errors = new();
    private readonly Notifier _notifier;
    private readonly IServiceProvider _sp;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<CompatibilityGuard> _log;

    public CompatibilityGuard(Notifier notifier, IServiceProvider sp, IServerApplicationHost host, ILogger<CompatibilityGuard> log)
    {
        _notifier = notifier;
        _sp = sp;
        _host = host;
        _log = log;
        Instance = this;
        // configuration du serveur, lue à la demande : langue de l'interface (mode « langue de Jellyfin » sans client :
        // notifications, journal d'activité, rangées HSS) et langue / pays des métadonnées (TMDb, classifications)
        I18n.ServerConfig = () => (sp.GetService(typeof(MediaBrowser.Controller.Configuration.IServerConfigurationManager))
            as MediaBrowser.Controller.Configuration.IServerConfigurationManager)?.Configuration;
    }

    /// <summary>Instance courante.</summary>
    public static CompatibilityGuard? Instance { get; private set; }

    /// <summary>Le décorateur de ISimilarItemsManager a été posé à l'enregistrement des services.</summary>
    public static bool DecoratorInstalled { get; set; }

    /// <summary>Raison de non-pose du décorateur (si échec).</summary>
    public static string? DecoratorProblem { get; set; }

    /// <summary>Résultats des vérifications, pour la page de configuration.</summary>
    public List<(string Check, bool Ok, string Detail)> Checks { get; } = new();

    /// <summary>Parties coupées et pourquoi.</summary>
    public IReadOnlyDictionary<Feature, string> Disabled => _disabled;

    /// <summary>Vrai si la partie est active (plugin actif, interrupteur, non coupée).</summary>
    public static bool Allowed(Feature f)
    {
        var c = Plugin.Instance?.Configuration;
        if (c is null || !c.Enabled || Instance is null || Instance._disabled.ContainsKey(f))
        {
            return false;
        }

        return f switch
        {
            Feature.MovieRecommendations => c.OverrideMovieRecommendations,
            Feature.Suggestions => c.OverrideSuggestions,
            Feature.Similar => c.OverrideSimilar,
            Feature.HomeScreenSection => true,          // les rangées affichées se choisissent dans Home Screen Sections
            Feature.ClientScript => true,               // titres des rangées « Parce que … », affichages, mode étendu
            _ => false,
        };
    }

    /// <summary>Coupe une partie et prévient.</summary>
    public void Disable(Feature f, string reason)
    {
        if (_disabled.TryAdd(f, reason))
        {
            _notifier.Raise("off-" + f, Texts.Pack("{0} désactivé", Texts.Translatable(Label(f))),
                Texts.Pack("{0} Jellyfin reprend son comportement normal pour cette partie.", reason));
        }
    }

    /// <summary>Signale une erreur ; 5 en 10 minutes coupent la partie jusqu'au redémarrage.</summary>
    public void ReportError(Feature f, Exception ex)
    {
        _log.LogWarning(ex, "Reco+: error in {Feature} (falling back to Jellyfin)", f);
        var q = _errors.GetOrAdd(f, _ => new Queue<DateTime>());
        lock (q)
        {
            q.Enqueue(DateTime.UtcNow);
            while (q.Count > 0 && DateTime.UtcNow - q.Peek() > TimeSpan.FromMinutes(10))
            {
                q.Dequeue();
            }

            if (q.Count >= 5)
            {
                Disable(f, Texts.Pack("5 erreurs en 10 minutes (dernière : {0}). Probablement une version de Jellyfin qui ne correspond plus ; partie coupée jusqu'au prochain redémarrage.", ex.GetType().Name + ": " + ex.Message));
            }
        }
    }

    /// <summary>Nom d'une partie (français : clé de traduction).</summary>
    public static string Label(Feature f) => f switch
    {
        Feature.MovieRecommendations => "Remplacement de /Movies/Recommendations",
        Feature.Suggestions => "Remplacement de /Items/Suggestions",
        Feature.Similar => "Remplacement de « Plus comme ceci »",
        Feature.HomeScreenSection => "Rangée Home Screen Sections",
        _ => "Script de l'accueil",
    };

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
            try
            {
                RunChecks();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Reco+: compatibility checks failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Vérifie les points d'accroche (rejouable depuis la page de configuration).</summary>
    public void RunChecks()
    {
        Checks.Clear();
        var v = _host.ApplicationVersion;
        bool sameVersion = v.Major == Plugin.TestedJellyfin.Major && v.Minor == Plugin.TestedJellyfin.Minor;
        Checks.Add(("Version de Jellyfin", sameVersion, sameVersion ? Texts.Pack("{0} (testée)", v) : Texts.Pack("{0} — plugin construit et testé pour {1}.x", v, Plugin.TestedJellyfin)));
        if (!sameVersion)
        {
            _notifier.Raise("version", "version de Jellyfin non testée",
                Texts.Pack("Jellyfin {0} alors que Reco+ a été construit pour {1}.x. Les points d'accroche sont vérifiés un par un ; toute partie qui ne colle plus est coupée automatiquement. Une mise à jour du plugin est à prévoir.", v, Plugin.TestedJellyfin));
        }
        else
        {
            _notifier.Clear("version");
        }

        // 1. décorateur de ISimilarItemsManager posé et effectivement utilisé
        var resolved = _sp.GetService<ISimilarItemsManager>();
        bool deco = DecoratorInstalled && resolved is SimilarItemsManagerDecorator;
        Checks.Add(("Remplacement du gestionnaire de recommandations", deco,
            deco ? Texts.Pack("actif") : DecoratorProblem ?? Texts.Pack("le service résolu est {0} (un autre plugin le remplace ?)", resolved?.GetType().FullName ?? Texts.Translatable("absent"))));
        if (!deco)
        {
            Disable(Feature.MovieRecommendations, Texts.Pack("Le gestionnaire de recommandations de Jellyfin n'a pas la forme attendue : {0}.", DecoratorProblem ?? Texts.Translatable("décorateur non utilisé")));
            Disable(Feature.Similar, "Même cause que ci-dessus.");
        }

        // 2. contrôleur des suggestions : actions et paramètres attendus
        var actions = _sp.GetService<IActionDescriptorCollectionProvider>()?.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(a => a.ControllerName == "Suggestions")
            .ToList() ?? new List<ControllerActionDescriptor>();
        var expected = new[] { "userId", "mediaType", "type", "startIndex", "limit" };
        var bad = new List<string>();
        foreach (var name in SuggestionsFilter.Actions)
        {
            var a = actions.FirstOrDefault(x => x.ActionName == name);
            if (a is null)
            {
                bad.Add(Texts.Pack("{0} absente", name));
                continue;
            }

            var ps = a.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var missing = expected.Where(e => !ps.Contains(e)).ToList();
            if (missing.Count > 0)
            {
                bad.Add(Texts.Pack("{0} sans {1}", name, string.Join(", ", missing)));
            }
        }

        string badText = bad.Count == 0 ? Texts.Pack("conforme") : Texts.Join(" ; ", bad);
        Checks.Add(("Point d'accroche /Items/Suggestions", bad.Count == 0, badText));
        if (bad.Count > 0)
        {
            Disable(Feature.Suggestions, Texts.Pack("Le contrôleur des suggestions de Jellyfin a changé ({0}).", badText));
        }

        // 3. dépendances facultatives
        var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Select(a => a.GetName()).ToList();
        string Opt(string name) => asm.FirstOrDefault(a => a.Name == name)?.Version?.ToString() ?? string.Empty;
        var hss = Opt("Jellyfin.Plugin.HomeScreenSections");
        Checks.Add(("Home Screen Sections", hss.Length > 0, hss.Length > 0 ? Texts.Pack("présent ({0}) : rangées Reco+ disponibles dans sa configuration", hss) : "absent : pas de rangée d'accueil (le reste fonctionne)"));
        var jfe = Opt("Jellyfin.Plugin.JellyfinEnhanced");
        Checks.Add(("Jellyfin Enhanced", jfe.Length > 0, jfe.Length > 0 ? Texts.Pack("présent ({0}) : le mode étendu utilise ses cartes, sa fiche et ses demandes (s'il est relié à Seerr)", jfe) : "absent : le mode étendu utilise les cartes Reco+ et ouvre la page Seerr"));
        var mbe = Opt("Jellyfin.Plugin.MediaBarEnhanced");
        Checks.Add(("Media Bar Enhanced", true, mbe.Length > 0 ? Texts.Pack("présent ({0}) : avec « Personalized Recommendations » activé dans Media Bar Enhanced, il montre les recommandations Reco+ (bibliothèque seulement)", mbe) : Texts.Pack("absent")));
        var lr = asm.FirstOrDefault(a => a.Name?.Contains("LocalRecs", StringComparison.OrdinalIgnoreCase) == true);
        if (lr is not null)
        {
            Checks.Add(("LocalRecs", false, "installé : recommandations en double, désinstallation conseillée"));
        }
    }
}
