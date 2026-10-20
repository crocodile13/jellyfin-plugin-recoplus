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

    /// <summary>
    /// Titre de la rangée principale. Les rangées affichées, leur ordre et leur activation par compte se règlent dans
    /// Home Screen Sections : Reco+ déclare toujours toutes ses rangées et ne fait que les remplir.
    /// </summary>
    public string HssSectionTitle { get; set; } = "Pour toi";

    /// <summary>Titres par rangée d'accueil.</summary>
    public int RowSize { get; set; } = 20;

    /// <summary>Titre de la rangée des films.</summary>
    public string RowMoviesTitle { get; set; } = "Films pour toi";

    /// <summary>Titre de la rangée des séries.</summary>
    public string RowSeriesTitle { get; set; } = "Séries pour toi";

    /// <summary>Titre de la rangée des nouveautés.</summary>
    public string RowNewTitle { get; set; } = "Nouveautés pour toi";

    /// <summary>« Nouveautés » : arrivés depuis ce nombre de jours.</summary>
    public int RowNewDays { get; set; } = 30;

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

    /// <summary>Écart minimal entre deux recalculs d'un même compte, en minutes (0 = aucun).</summary>
    public int UserMinIntervalMinutes { get; set; } = 15;

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

    /// <summary>Appels de Reco+ à Seerr par seconde, au plus (2 en cours au plus) : laisse Seerr répondre aux utilisateurs.</summary>
    public int SeerrRate { get; set; } = 8;

    /// <summary>Pays de référence des classifications (contrôle parental des titres hors bibliothèque).</summary>
    public string RatingCountry { get; set; } = "FR";

    // ---- mode étendu

    /// <summary>Mode étendu : recommande aussi des titres absents de la bibliothèque (catalogue Seerr).</summary>
    public bool ExtendedEnabled { get; set; }

    /// <summary>Nombre de titres hors bibliothèque proposés.</summary>
    public int ExtendedCount { get; set; } = 20;

    /// <summary>Titres aimés utilisés comme points de départ dans le catalogue.</summary>
    public int ExtendedAnchors { get; set; } = 30;

    /// <summary>Pages de recommandations et de titres similaires TMDb lues par titre aimé (20 titres environ par page).</summary>
    public int ExtendedPages { get; set; } = 2;

    /// <summary>Candidats détaillés par compte (appels Seerr, mis en cache).</summary>
    public int ExtendedDetails { get; set; } = 200;

    // exploration : d'autres sources de candidats que les recommandations des titres aimés

    /// <summary>Source : les mieux notés de TMDb, tous genres (liste commune à tous les comptes).</summary>
    public bool ExtendedTopRated { get; set; } = true;

    /// <summary>« Les mieux notés » : nombre minimal de votes TMDb.</summary>
    public int ExtendedTopRatedMinVotes { get; set; } = 1000;

    /// <summary>« Les mieux notés » : pages lues par sorte (films, séries), la première et les autres tirées au hasard du jour.</summary>
    public int ExtendedTopRatedPages { get; set; } = 2;

    /// <summary>Source : les mieux notés des genres préférés du compte.</summary>
    public bool ExtendedByGenre { get; set; } = true;

    /// <summary>Genres préférés explorés.</summary>
    public int ExtendedGenres { get; set; } = 5;

    /// <summary>Source : les mieux notés des thèmes (mots-clés TMDb) récurrents dans les titres aimés.</summary>
    public bool ExtendedByKeyword { get; set; } = true;

    /// <summary>Thèmes explorés (présents dans au moins 2 titres aimés).</summary>
    public int ExtendedKeywords { get; set; } = 10;

    /// <summary>Source : volets manquants des sagas dont un titre est aimé.</summary>
    public bool ExtendedSagas { get; set; } = true;

    /// <summary>Source : titres des réalisateurs, créateurs et scénaristes récurrents dans les titres aimés.</summary>
    public bool ExtendedByPeople { get; set; } = true;

    /// <summary>Auteurs : présents dans au moins ce nombre de titres aimés.</summary>
    public int ExtendedPeopleMinTitles { get; set; } = 2;

    /// <summary>Auteurs explorés.</summary>
    public int ExtendedPeople { get; set; } = 10;

    /// <summary>Source : les mieux notés des studios (films) et chaînes (séries) récurrents dans les titres aimés.</summary>
    public bool ExtendedByStudio { get; set; } = true;

    /// <summary>Studios : présents dans au moins ce nombre de titres aimés.</summary>
    public int ExtendedStudioMinTitles { get; set; } = 3;

    /// <summary>Studios explorés.</summary>
    public int ExtendedStudios { get; set; } = 5;

    /// <summary>Source : les mieux notés dans les langues d'origine qui pèsent dans les titres aimés.</summary>
    public bool ExtendedByLanguage { get; set; } = true;

    /// <summary>Langues : part minimale (en %) des titres aimés.</summary>
    public int ExtendedLanguageMinShare { get; set; } = 15;

    /// <summary>Source : croisements des pistes les plus fortes (genre × thème, genre × langue d'origine).</summary>
    public bool ExtendedCrossings { get; set; } = true;

    /// <summary>Croisements explorés (par sorte : films, séries).</summary>
    public int ExtendedCrossingsMax { get; set; } = 6;

    /// <summary>Source : pépites, titres très bien notés mais peu connus (peu de votes), dans les genres préférés.</summary>
    public bool ExtendedGems { get; set; } = true;

    /// <summary>Pépites : note TMDb minimale (plus haute : peu de votes, note moins sûre).</summary>
    public double ExtendedGemMinRating { get; set; } = 7.8;

    /// <summary>Pépites : votes TMDb minimum (en dessous, la note ne veut rien dire).</summary>
    public int ExtendedGemMinVotes { get; set; } = 50;

    /// <summary>Pépites : votes TMDb maximum (au-dessus, le titre est déjà connu).</summary>
    public int ExtendedGemMaxVotes { get; set; } = 500;

    /// <summary>Pépites : genres préférés explorés (en plus d'une recherche tous genres).</summary>
    public int ExtendedGemGenres { get; set; } = 3;

    /// <summary>Exploration : note TMDb minimale (genres, thèmes, studios, langues, mieux notés).</summary>
    public double ExtendedMinRating { get; set; } = 7.5;

    /// <summary>Exploration : nombre minimal de votes TMDb (genres, thèmes, studios, langues).</summary>
    public int ExtendedMinVotes { get; set; } = 500;

    /// <summary>Exploration : pages lues par recherche (la première, puis des pages tirées au hasard du jour).</summary>
    public int ExtendedDiscoverPages { get; set; } = 2;

    /// <summary>Exploration : profondeur du tirage (pages 2 à N), donc jusqu'où on va chercher sous le haut du classement.</summary>
    public int ExtendedDiscoverDepth { get; set; } = 10;

    /// <summary>
    /// Bruit du jour sur le classement des titres hors bibliothèque (0 = aucun, 1 = fort) : les titres de valeur proche
    /// changent de place d'un jour à l'autre.
    /// </summary>
    public double ExtendedNoise { get; set; } = 0.3;

    /// <summary>
    /// Recalcul complet : l'exploration Seerr n'est refaite que pour les comptes actifs depuis ce nombre de jours (0 = tous) ;
    /// les autres gardent leurs titres hors bibliothèque jusqu'à leur prochaine activité.
    /// </summary>
    public int ExtendedActiveDays { get; set; } = 30;

    /// <summary>Mélange libre : places « découverte » réservées aux titres hors bibliothèque les mieux notés (0 = aucune).</summary>
    public int ExtendedDiscoverySlots { get; set; }

    /// <summary>
    /// Titres hors bibliothèque sur l'accueil (script client) : mélangés dans la rangée « Pour toi » si Home Screen
    /// Sections est présent, sinon rangée à part.
    /// </summary>
    public bool ExtendedHomeRow { get; set; } = true;

    /// <summary>Mélange dans « Pour toi » : à intervalle fixe, ou libre (classement commun du moteur).</summary>
    public MixMode ExtendedMixMode { get; set; } = MixMode.Interleave;

    /// <summary>Mélange dans « Pour toi » : un titre hors bibliothèque toutes les N cartes (2 = un sur deux).</summary>
    public int ExtendedMixEvery { get; set; } = 3;

    /// <summary>
    /// Mélange libre : nombre minimal de titres hors bibliothèque montrés dans « Pour toi » ; si le classement commun en
    /// place moins, les mieux classés sont ajoutés quand même, répartis dans la rangée (0 = classement seul).
    /// </summary>
    public int ExtendedMinFree { get; set; }

    /// <summary>Titre de la rangée du mode étendu.</summary>
    public string ExtendedTitle { get; set; } = "À découvrir pour toi";

    /// <summary>Bouton « Demander » (la demande est faite au nom de l'utilisateur, avec ses droits et quotas Seerr).</summary>
    public bool ExtendedRequestButton { get; set; } = true;

    // ---- notifications

    /// <summary>Type de webhook (URL et jeton sont des secrets, stockés à part : un sujet ntfy est un secret).</summary>
    public WebhookKind Webhook { get; set; } = WebhookKind.None;
}
