using System;
using System.Collections.Generic;
using Jellyfin.Plugin.RecoPlus.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus;

/// <summary>Reco+ : recommandations personnelles qui remplacent celles de Jellyfin.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Identifiant fixe du plugin.</summary>
    public static readonly Guid PluginGuid = Guid.Parse("6b1f3c52-8d0e-4f7a-9c41-2e5d7a90c3b8");

    /// <summary>Version de Jellyfin sur laquelle ce plugin a été construit et testé (majeure.mineure).</summary>
    public static readonly Version TestedJellyfin = new(12, 2);

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Migrate();
    }

    /// <summary>Instance courante (null avant le chargement).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Reco+";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description => "Recommandations personnelles (historique complet, abandons, sagas, demandes Seerr, mode étendu) à la place de celles de Jellyfin.";

    /// <summary>
    /// Anciens réglages : « Sur l'accueil » coupé (avant 1.0.21) = aucune rangée avec des titres hors bibliothèque.
    /// </summary>
    private void Migrate()
    {
        var c = this.Configuration;
        if (c.ExtendedHomeRow is not bool home)
        {
            return;
        }

        if (!home)
        {
            c.SourcePourToi = c.SourceNouveautes = c.SourceParceQue = c.SourceFilms = c.SourceSeries = RowSource.Local;
        }

        c.ExtendedHomeRow = null;
        try
        {
            SaveConfiguration();
        }
        catch (Exception)
        {
            // gardé en mémoire ; réécrit au prochain enregistrement de la page de configuration
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        yield return new PluginPageInfo { Name = "RecoPlus", EmbeddedResourcePath = ns + ".Web.configPage.html" };
    }
}
