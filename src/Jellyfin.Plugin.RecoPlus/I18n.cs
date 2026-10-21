using System;
using MediaBrowser.Model.Configuration;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus;

/// <summary>Langue de l'interface du plugin.</summary>
public enum UiLanguage
{
    /// <summary>Celle de Jellyfin : la langue de l'interface de celui qui lit, sinon celle du serveur.</summary>
    System,

    /// <summary>Français.</summary>
    French,

    /// <summary>Anglais.</summary>
    English
}

/// <summary>Choix de la langue des textes (voir <see cref="Texts"/>) : réglage du plugin, sinon langue de Jellyfin.</summary>
public static class I18n
{
    /// <summary>Configuration du serveur Jellyfin, lue à la demande (null avant le démarrage et dans les tests).</summary>
    public static Func<ServerConfiguration?>? ServerConfig { get; set; }

    private static ServerConfiguration? Server()
    {
        try
        {
            return ServerConfig?.Invoke();
        }
        catch (InvalidOperationException)
        {
            return null;   // configuration du serveur pas encore prête
        }
    }

    /// <summary>
    /// « fr » ou « en » : la langue imposée par le réglage du plugin, sinon celle du client qui demande (langue de
    /// l'interface Jellyfin, du navigateur), sinon celle de l'interface du serveur.
    /// </summary>
    public static string Lang(string? client = null)
        => (Plugin.Instance?.Configuration.Language ?? UiLanguage.System) switch
        {
            UiLanguage.French => "fr",
            UiLanguage.English => "en",
            _ => Texts.Normalize(!string.IsNullOrWhiteSpace(client) ? client : Server()?.UICulture),
        };

    /// <summary>Langue (ex. « fr ») et pays (ex. « FR ») préférés des métadonnées du serveur ; null si inconnus.</summary>
    public static (string? Language, string? Country) Metadata()
    {
        var c = Server();
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return (Clean(c?.PreferredMetadataLanguage), Clean(c?.MetadataCountryCode));
    }

    /// <summary>Texte (emballé ou non) dans la langue de <paramref name="lang"/>.</summary>
    public static string R(string? packed, string lang) => Texts.Render(packed, lang);
}
