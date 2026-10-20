using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Configuration;

/// <summary>Mélange des titres hors bibliothèque dans « Pour toi ».</summary>
public enum MixMode
{
    /// <summary>Un titre hors bibliothèque toutes les N cartes.</summary>
    Interleave,

    /// <summary>Libre : chacun à la place que lui donne le classement commun du moteur.</summary>
    Score
}

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

    /// <summary>Titres par rangée d'accueil.</summary>
    public int RowSize { get; set; } = 20;

    /// <summary>Rangée « Films pour toi » (Home Screen Sections).</summary>
    public bool RowMovies { get; set; }

    /// <summary>Titre de la rangée des films.</summary>
    public string RowMoviesTitle { get; set; } = "Films pour toi";

    /// <summary>Rangée « Séries pour toi ».</summary>
    public bool RowSeries { get; set; }

    /// <summary>Titre de la rangée des séries.</summary>
    public string RowSeriesTitle { get; set; } = "Séries pour toi";

    /// <summary>Rangée « Nouveautés pour toi » : arrivés récemment, classés selon le goût du compte.</summary>
    public bool RowNew { get; set; }

    /// <summary>Titre de la rangée des nouveautés.</summary>
    public string RowNewTitle { get; set; } = "Nouveautés pour toi";

    /// <summary>« Nouveautés » : arrivés depuis ce nombre de jours.</summary>
    public int RowNewDays { get; set; } = 30;

    /// <summary>Rangées « Parce que tu as aimé … » (0 à 3), chacune autour d'un titre aimé différent.</summary>
    public int RowBecause { get; set; }

    /// <summary>Titre des rangées « Parce que … » ({titre} = le titre aimé).</summary>
    public string RowBecauseTitle { get; set; } = "Parce que tu as aimé {titre}";

    /// <summary>
    /// Compter ce qui est montré sans être ouvert (seulement quand une rangée Reco+ est réellement à l'écran du compte
    /// connecté) : un titre ignoré plusieurs jours descend, puis remonte avec le temps.
    /// </summary>
    public bool TrackImpressions { get; set; } = true;

    /// <summary>Fuseau horaire des visionnages (identifiant IANA, ex. Europe/Paris) ; vide = celui du serveur.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Méthode du calibrage.</summary>
    public CalibrationMethod CalibrationMethod { get; set; } = CalibrationMethod.Auto;

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

    /// <summary>Nouveaux épisodes d'une série rattrapée et aimée placés en tête (0 = jamais).</summary>
    public int NewEpisodesPinned { get; set; } = 2;

    /// <summary>Pendant ce nombre de jours après l'arrivée du dernier épisode.</summary>
    public int NewEpisodesMaxDays { get; set; } = 45;

    /// <summary>Lire les masquages (« pas intéressé ») et les avis notés de Jellyfin Enhanced.</summary>
    public bool UseJellyfinEnhanced { get; set; } = true;

    /// <summary>Lire le nombre de votes TMDb dans les fiches Seerr (confiance dans les notes).</summary>
    public bool UseSeerrVotes { get; set; } = true;

    /// <summary>Lire les listes de suivi Seerr (intention, plus faible qu'une demande).</summary>
    public bool UseSeerrWatchlist { get; set; } = true;

    /// <summary>Présentation des listes : filtres, hasard du jour, minimums de films et de séries.</summary>
    public ListOptions Lists { get; set; } = new();

    /// <summary>Comptes ignorés par Reco+ (identifiants) : Jellyfin leur répond normalement et leur historique ne compte pas pour le foyer.</summary>
    public List<string> ExcludedUsers { get; set; } = new();

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

    /// <summary>
    /// Titres hors bibliothèque sur l'accueil (script client) : mélangés dans la rangée « Pour toi » si Home Screen
    /// Sections est présent, sinon rangée à part.
    /// </summary>
    public bool ExtendedHomeRow { get; set; } = true;

    /// <summary>Mélange dans « Pour toi » : à intervalle fixe, ou libre (classement commun du moteur).</summary>
    public MixMode ExtendedMixMode { get; set; } = MixMode.Interleave;

    /// <summary>Mélange dans « Pour toi » : un titre hors bibliothèque toutes les N cartes (2 = un sur deux).</summary>
    public int ExtendedMixEvery { get; set; } = 3;

    /// <summary>Titre de la rangée du mode étendu.</summary>
    public string ExtendedTitle { get; set; } = "À découvrir pour toi";

    /// <summary>Bouton « Demander » (la demande est faite au nom de l'utilisateur, avec ses droits et quotas Seerr).</summary>
    public bool ExtendedRequestButton { get; set; } = true;

    // ---- notifications

    /// <summary>Type de webhook (URL et jeton sont des secrets, stockés à part : un sujet ntfy est un secret).</summary>
    public WebhookKind Webhook { get; set; } = WebhookKind.None;
}
