using System;
using System.Linq;
using Jellyfin.Plugin.RecoPlus.Data;
using Jellyfin.Plugin.RecoPlus.Integration;
using Jellyfin.Plugin.RecoPlus.Seerr;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.RecoPlus;

/// <summary>Enregistre les services du plugin et pose le décorateur du gestionnaire de recommandations.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient();
        services.AddSingleton<SecretStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<PlaybackReportingReader>();
        services.AddSingleton<LibraryIndex>();
        services.AddSingleton<UserHistory>();
        services.AddSingleton<UserDataReader>();
        services.AddSingleton(sp => new JellyfinEnhancedReader(
            System.IO.Path.Combine(sp.GetRequiredService<MediaBrowser.Common.Configuration.IApplicationPaths>().PluginsPath, "configurations", "Jellyfin.Plugin.JellyfinEnhanced"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JellyfinEnhancedReader>>()));
        services.AddSingleton(sp => new ImpressionStore(
            () => Plugin.Instance is null ? null : System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "affichages.json"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ImpressionStore>>()));
        services.AddSingleton<SeerrService>();
        services.AddSingleton<RecoService>();
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<CompatibilityGuard>();
        services.AddHostedService(sp => sp.GetRequiredService<CompatibilityGuard>());
        services.AddHostedService<RecoWorker>();
        services.AddHostedService<HomeScreenSectionsIntegration>();
        services.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackStartConsumer>();
        services.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackStopConsumer>();
        services.AddSingleton<SuggestionsFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<SuggestionsFilter>(int.MaxValue - 100));
        services.AddSingleton<IStartupFilter, ScriptInjection>();
        DecorateSimilarItemsManager(services);
    }

    /// <summary>
    /// Remplace l'enregistrement de ISimilarItemsManager par notre enveloppe autour de l'implémentation d'origine
    /// (construite par l'injecteur à partir de son type, sans dépendre de son assembly). Forme inattendue : rien
    /// n'est modifié, et la vérification de démarrage le signale.
    /// </summary>
    private static void DecorateSimilarItemsManager(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ISimilarItemsManager));
        if (existing is null)
        {
            CompatibilityGuard.DecoratorProblem = "aucun ISimilarItemsManager enregistré par Jellyfin";
            return;
        }

        if (existing.ImplementationType is null || existing.Lifetime != ServiceLifetime.Singleton)
        {
            CompatibilityGuard.DecoratorProblem = $"enregistrement inattendu ({existing.Lifetime}, {existing.ImplementationType?.FullName ?? "fabrique ou instance"})";
            return;
        }

        var implType = existing.ImplementationType;
        services.Remove(existing);
        services.AddSingleton(implType);
        services.AddSingleton<ISimilarItemsManager>(sp => new SimilarItemsManagerDecorator((ISimilarItemsManager)sp.GetRequiredService(implType), sp));
        CompatibilityGuard.DecoratorInstalled = true;
    }
}
