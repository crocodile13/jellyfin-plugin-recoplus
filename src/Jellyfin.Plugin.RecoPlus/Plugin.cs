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

    /// <summary>
    /// Versions de Jellyfin (majeure.mineure) pour lesquelles ce plugin est construit et testé : compilé contre la plus
    /// ancienne (<c>targetAbi</c>), et en CI contre les paquets Jellyfin 12 les plus récents.
    /// </summary>
    public static readonly Version TestedFrom = new(12, 2), TestedTo = new(12, 4);

    /// <summary>La version de Jellyfin est dans l'intervalle testé.</summary>
    public static bool IsTested(Version v) => new Version(v.Major, v.Minor) >= TestedFrom && new Version(v.Major, v.Minor) <= TestedTo;

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
    public override string Description => Texts.T("Recommandations personnelles (historique complet, abandons, sagas, demandes Seerr, mode étendu) à la place de celles de Jellyfin.", I18n.Lang());

    /// <summary>
    /// Anciens réglages : « Sur l'accueil » coupé (avant 1.0.21) = aucune rangée avec des titres hors bibliothèque ;
    /// titres de rangées restés aux valeurs françaises par défaut (avant 1.0.25) = titres par défaut, qui suivent la langue.
    /// </summary>
    private void Migrate()
    {
        var c = this.Configuration;
        bool changed = false;
        if (c.ExtendedHomeRow is bool home)
        {
            if (!home)
            {
                c.SourcePourToi = c.SourceNouveautes = c.SourceParceQue = c.SourceFilms = RowSource.Local;
            }

            c.ExtendedHomeRow = null;
            changed = true;
        }

        string Default(string v, string fr)
        {
            if (v.Trim() != fr)
            {
                return v;
            }

            changed = true;
            return string.Empty;
        }

        c.HssSectionTitle = Default(c.HssSectionTitle, Integration.HomeScreenSectionsIntegration.DefaultPourToi);
        c.RowNewTitle = Default(c.RowNewTitle, Integration.HomeScreenSectionsIntegration.DefaultNew);
        c.RowMoviesTitle = Default(c.RowMoviesTitle, Integration.HomeScreenSectionsIntegration.DefaultMovies);
        c.RowSeriesTitle = Default(c.RowSeriesTitle, Integration.HomeScreenSectionsIntegration.DefaultSeries);
        c.RowBecauseTitle = Default(c.RowBecauseTitle, Integration.HomeScreenSectionsIntegration.DefaultBecause);
        c.ExtendedTitle = Default(c.ExtendedTitle, Integration.HomeScreenSectionsIntegration.DefaultExtended);
        if (!changed)
        {
            return;
        }

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
        // entrée « Reco+ » dans la section Extensions du menu du tableau de bord ; icône Material « style » (affiches
        // empilées), remplacée par le logo par le script de l'interface web (client.js)
        yield return new PluginPageInfo
        {
            Name = "RecoPlus",
            DisplayName = "Reco+",
            EmbeddedResourcePath = ns + ".Web.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "style",
        };
    }
}
