using MediaBrowser.Model.Plugins;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Configuration;

/// <summary>Type de webhook de notification.</summary>
public enum WebhookKind
{
    /// <summary>Aucun.</summary>
    None,

    /// <summary>ntfy (texte brut, en-têtes Title/Priority).</summary>
    Ntfy,

    /// <summary>Gotify (JSON, jeton dans X-Gotify-Key).</summary>
    Gotify,

    /// <summary>Discord (JSON « content »).</summary>
    Discord,

    /// <summary>JSON générique {title, message, level}.</summary>
    Json
}

/// <summary>
/// Configuration. Les secrets (clé Seerr, jeton de webhook) ne sont PAS ici : ils sont dans un fichier séparé
/// (droits 600) et ne sont jamais renvoyés par l'API.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    // ---- interrupteurs généraux

    /// <summary>Plugin actif. Désactivé : Jellyfin se comporte exactement comme sans le plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Remplace /Movies/Recommendations (onglet Suggestions des films, Media Bar…).</summary>
    public bool OverrideMovieRecommendations { get; set; } = true;

    /// <summary>Remplace /Items/Suggestions (aujourd'hui : du hasard).</summary>
    public bool OverrideSuggestions { get; set; } = true;

    /// <summary>Remplace « Plus comme ceci » pour les films et séries (aujourd'hui : séries au hasard).</summary>
    public bool OverrideSimilar { get; set; } = true;

    /// <summary>Part de personnalisation dans « Plus comme ceci » (0 = pure ressemblance).</summary>
    public double SimilarPersonalization { get; set; } = 0.25;

    /// <summary>Ajoute la rangée « Pour toi » si Home Screen Sections est installé.</summary>
    public bool HssSection { get; set; } = true;

    /// <summary>Titre de la rangée Home Screen Sections.</summary>
    public string HssSectionTitle { get; set; } = "Pour toi";

    /// <summary>Nombre de titres demandés, arrivés et pas encore vus, placés en tête.</summary>
    public int PinnedRequests { get; set; } = 3;

    /// <summary>Un titre demandé n'est placé en tête que pendant ce nombre de jours après son arrivée.</summary>
    public int PinnedMaxDays { get; set; } = 30;

    /// <summary>Taille de la liste calculée par utilisateur.</summary>
    public int ListSize { get; set; } = 200;

    /// <summary>Délai avant recalcul d'un compte après une lecture (secondes).</summary>
    public int DebounceSeconds { get; set; } = 120;

    /// <summary>Réglages du moteur.</summary>
    public EngineSettings Engine { get; set; } = new();

    /// <summary>Réglages précédents (retour arrière après un calibrage appliqué).</summary>
    public EngineSettings? PreviousEngine { get; set; }

    /// <summary>Chemin de la base Playback Reporting (vide = détection automatique).</summary>
    public string PlaybackReportingDbPath { get; set; } = string.Empty;

    // ---- Seerr

    /// <summary>Utiliser Seerr (demandes = signal, titres demandés arrivés en tête).</summary>
    public bool SeerrEnabled { get; set; }

    /// <summary>URL interne de Seerr, vue depuis le serveur Jellyfin.</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>URL publique de Seerr pour les liens (vide = URL interne).</summary>
    public string SeerrPublicUrl { get; set; } = string.Empty;

    /// <summary>Langue des métadonnées TMDb (doit correspondre à celle de la bibliothèque).</summary>
    public string SeerrLanguage { get; set; } = "fr";

    /// <summary>Pays de référence des classifications (contrôle parental des titres hors bibliothèque).</summary>
    public string RatingCountry { get; set; } = "FR";

    // ---- mode étendu

    /// <summary>Mode étendu : recommande aussi des titres absents de la bibliothèque (catalogue Seerr).</summary>
    public bool ExtendedEnabled { get; set; }

    /// <summary>Nombre de titres hors bibliothèque proposés.</summary>
    public int ExtendedCount { get; set; } = 20;

    /// <summary>Titres aimés utilisés comme points de départ dans le catalogue.</summary>
    public int ExtendedAnchors { get; set; } = 15;

    /// <summary>Candidats détaillés par compte (appels Seerr, mis en cache).</summary>
    public int ExtendedDetails { get; set; } = 60;

    /// <summary>Affiche la rangée « À découvrir » sur l'accueil (script client).</summary>
    public bool ExtendedHomeRow { get; set; } = true;

    /// <summary>Titre de la rangée du mode étendu.</summary>
    public string ExtendedTitle { get; set; } = "À découvrir pour toi";

    /// <summary>Bouton « Demander » (la demande est faite au nom de l'utilisateur, avec ses droits et quotas Seerr).</summary>
    public bool ExtendedRequestButton { get; set; } = true;

    // ---- notifications

    /// <summary>Type de webhook (URL et jeton sont des secrets, stockés à part : un sujet ntfy est un secret).</summary>
    public WebhookKind Webhook { get; set; } = WebhookKind.None;
}
