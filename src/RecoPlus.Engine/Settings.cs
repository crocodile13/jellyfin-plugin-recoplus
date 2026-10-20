using System;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Décroissance du poids d'un titre avec l'ancienneté de la dernière activité.</summary>
public enum DecayKind
{
    /// <summary>0,5^(âge / demi-vie).</summary>
    Exponential,

    /// <summary>(1 + âge / demi-vie)^-1 (Larrain, Parra et al., RecSys 2015).</summary>
    Power
}

/// <summary>Note utilisée pour le bonus de qualité.</summary>
public enum RatingSource
{
    /// <summary>Note de la communauté (TMDb, IMDb… selon les fournisseurs de métadonnées).</summary>
    Community,

    /// <summary>Note des critiques (Rotten Tomatoes…), à défaut celle de la communauté.</summary>
    Critic,

    /// <summary>La meilleure des deux.</summary>
    Best,

    /// <summary>La moyenne des deux disponibles.</summary>
    Mean
}

/// <summary>Pondération des caractéristiques de contenu.</summary>
public enum WeightingScheme
{
    /// <summary>TF-IDF.</summary>
    TfIdf,

    /// <summary>BM25 (k1, b).</summary>
    Bm25
}

/// <summary>Façon de varier la liste.</summary>
public enum DiversityMode
{
    /// <summary>Pénalité de redondance fixe (MMR), la même pour tous les comptes.</summary>
    Fixed,

    /// <summary>
    /// Personnalisée : la liste est aussi variée que ce que le compte regarde (ressemblance moyenne de ses titres
    /// aimés) — un éclectique reçoit une liste variée, un spécialiste une liste resserrée.
    /// </summary>
    Personalized,

    /// <summary>
    /// Calibrée (Steck, RecSys 2018) : la répartition des genres de la liste suit celle de l'historique du compte
    /// (70 % de thrillers vus → environ 70 % de thrillers proposés, pas 100 %).
    /// </summary>
    Calibrated
}

/// <summary>Tous les réglages du moteur. Valeurs par défaut = réglage retenu le 19/10/2026.</summary>
public sealed class EngineSettings
{
    // ---- signaux (interaction → poids signé)

    /// <summary>Demi-vie de l'historique, en jours.</summary>
    public double HalfLifeDays { get; set; } = 90;

    /// <summary>Poids minimal d'un titre ancien (fraction).</summary>
    public double RecencyFloor { get; set; } = 0.25;

    /// <summary>Bonus d'un favori.</summary>
    public double FavoriteBoost { get; set; } = 1.0;

    /// <summary>Un film est « vu » au-delà de cette part (0..1).</summary>
    public double MovieDone { get; set; } = 0.70;

    /// <summary>En dessous, simple zapping (0..1).</summary>
    public double Zap { get; set; } = 0.05;

    /// <summary>Jours sans reprise avant qu'un titre commencé compte comme abandonné (la marge).</summary>
    public double AbandonDays { get; set; } = 30;

    /// <summary>Poids négatif d'un abandon (valeur positive, appliquée en négatif).</summary>
    public double AbandonWeight { get; set; } = 0.5;

    /// <summary>Épisodes vus pour qu'une série compte « pleinement » (si l'on compte en épisodes).</summary>
    public int SeriesFullEpisodes { get; set; } = 10;

    /// <summary>Poids d'une série selon le temps regardé (durée réelle des épisodes) plutôt que le nombre d'épisodes.</summary>
    public bool SeriesByTime { get; set; } = true;

    /// <summary>Heures regardées pour qu'une série compte « pleinement » (poids 1).</summary>
    public double SeriesFullHours { get; set; } = 7.5;

    /// <summary>Poids maximal d'une série très regardée (au-delà de 1 : une série de 100 heures pèse plus qu'une de 8).</summary>
    public double SeriesMaxWeight { get; set; } = 1.0;

    /// <summary>Une série est abandonnée si au plus ce nombre d'épisodes a été vu…</summary>
    public int SeriesAbandonEpisodes { get; set; } = 2;

    /// <summary>…et moins de cette part de la série…</summary>
    public double SeriesAbandonFraction { get; set; } = 0.10;

    /// <summary>…et si la série compte au moins ce nombre d'épisodes.</summary>
    public int SeriesMinEpisodes { get; set; } = 6;

    /// <summary>Forme de la décroissance.</summary>
    public DecayKind Decay { get; set; } = DecayKind.Exponential;

    /// <summary>Poids d'une demande Seerr (titre voulu, pas encore vu) au moment de la demande.</summary>
    public double RequestWeight { get; set; } = 0.6;

    /// <summary>Demi-vie d'une demande (jours) : signal de court terme, vite remplacé par ce que le compte a vraiment regardé.</summary>
    public double RequestHalfLifeDays { get; set; } = 21;

    /// <summary>Au-delà de cet âge (jours), une demande jamais regardée ne compte plus du tout.</summary>
    public double RequestMaxDays { get; set; } = 90;

    /// <summary>Poids d'un titre mis dans la liste de suivi Seerr (comme une demande, en plus faible).</summary>
    public double WatchlistWeight { get; set; } = 0.4;

    /// <summary>Part du bonus de favori pour une saison mise en favori (sans la série).</summary>
    public double FavSeasonShare { get; set; } = 0.6;

    /// <summary>Part du bonus de favori pour des épisodes mis en favori (atteinte à 3 épisodes).</summary>
    public double FavEpisodeShare { get; set; } = 0.3;

    /// <summary>Bonus d'un titre revu (film relu, épisodes revus). Coupé : aucun gain mesuré sur l'historique de référence.</summary>
    public double RewatchBoost { get; set; }

    /// <summary>Poids d'une note explicite du compte (avis Jellyfin Enhanced, note ou pouce Jellyfin), dans les deux sens.</summary>
    public double RatingWeight { get; set; } = 1.0;

    /// <summary>Poids négatif d'un titre masqué (« pas intéressé »).</summary>
    public double HiddenWeight { get; set; } = 1.0;

    // ---- contenu

    /// <summary>Poids des genres.</summary>
    public double FeatGenre { get; set; } = 1.0;

    /// <summary>Poids des mots-clés.</summary>
    public double FeatTag { get; set; } = 0.5;

    /// <summary>Poids des studios.</summary>
    public double FeatStudio { get; set; } = 0.35;

    /// <summary>Poids des acteurs.</summary>
    public double FeatActor { get; set; } = 0.7;

    /// <summary>Poids des réalisateurs.</summary>
    public double FeatDirector { get; set; } = 1.3;

    /// <summary>Poids des scénaristes.</summary>
    public double FeatWriter { get; set; } = 0.6;

    /// <summary>Poids des créateurs (séries).</summary>
    public double FeatCreator { get; set; } = 1.0;

    /// <summary>Poids de la décennie.</summary>
    public double FeatDecade { get; set; } = 0.5;

    /// <summary>Poids du type (film / série).</summary>
    public double FeatKind { get; set; } = 0.6;

    /// <summary>Poids des pays de production (0 = ignorés).</summary>
    public double FeatCountry { get; set; }

    /// <summary>Poids de la langue originale (0 = ignorée).</summary>
    public double FeatLanguage { get; set; }

    /// <summary>Acteurs pris en compte (premiers du générique).</summary>
    public int Actors { get; set; } = 8;

    /// <summary>Studios pris en compte.</summary>
    public int StudiosKept { get; set; } = 3;

    /// <summary>Pondération TF-IDF ou BM25.</summary>
    public WeightingScheme Weighting { get; set; } = WeightingScheme.TfIdf;

    /// <summary>BM25 k1.</summary>
    public double Bm25K1 { get; set; } = 1.2;

    /// <summary>BM25 b.</summary>
    public double Bm25B { get; set; } = 0.75;

    // ---- collaboratif (« vus ensemble »)

    /// <summary>Rétrécissement : c / (√(ni·nj) + shrink).</summary>
    public double Shrink { get; set; } = 10;

    /// <summary>Minimum de spectateurs communs pour lier deux titres.</summary>
    public int MinCommon { get; set; } = 2;

    // ---- score

    /// <summary>Proximité au profil moyen.</summary>
    public double WCentroid { get; set; } = 1.0;

    /// <summary>Proximité au titre aimé le plus proche.</summary>
    public double WMaxSim { get; set; } = 2.0;

    /// <summary>Vus ensemble.</summary>
    public double WCollab { get; set; } = 0.3;

    /// <summary>Pénalité de ressemblance avec un abandon.</summary>
    public double WNegative { get; set; } = 0.6;

    /// <summary>Popularité dans le foyer (bonus réservé au haut du classement par goût).</summary>
    public double WPopularity { get; set; } = 0.5;

    /// <summary>Note de la communauté (bonus réservé).</summary>
    public double WQuality { get; set; } = 0.1;

    /// <summary>Nouveauté (bonus réservé).</summary>
    public double WFresh { get; set; } = 2.0;

    /// <summary>Plafond de la nouveauté, que le calibrage ne peut pas dépasser.</summary>
    public const double FreshCeiling = 4.0;

    /// <summary>Échelle de la nouveauté, en jours.</summary>
    public double FreshDays { get; set; } = 45;

    /// <summary>Seuls les N premiers titres par goût reçoivent les bonus (garde-fou, toujours actif).</summary>
    public int GateTop { get; set; } = 60;

    /// <summary>Accord TMDb : part des titres aimés dont TMDb recommande le candidat (mode étendu, bonus réservé).</summary>
    public double WTmdbAgreement { get; set; } = 0.5;

    /// <summary>Sortie récente (date de sortie, pas d'ajout) : bonus réservé.</summary>
    public double WRelease { get; set; }

    /// <summary>Échelle de « sortie récente », en jours.</summary>
    public double ReleaseDays { get; set; } = 365;

    /// <summary>Note utilisée pour le bonus de qualité.</summary>
    public RatingSource Rating { get; set; } = RatingSource.Community;

    /// <summary>
    /// Confiance dans la note de la communauté : moyenne bayésienne avec ce nombre de votes « fictifs » à 6,5 (IMDb
    /// Top 250, TMDb). Une note de 9 sur 12 votes pèse peu ; sur 20 000, pleinement. 0 = note brute. Sans nombre de
    /// votes connu, la note est prise telle quelle.
    /// </summary>
    public double RatingPriorVotes { get; set; } = 150;

    /// <summary>Avis du foyer : bonus réservé des titres bien notés par les AUTRES comptes (Jellyfin Enhanced).</summary>
    public double WHousehold { get; set; } = 0.3;

    /// <summary>Confiance dans les avis du foyer : avis « fictifs » neutres (3 étoiles) ajoutés à la moyenne.</summary>
    public double HouseholdPrior { get; set; } = 2;

    /// <summary>
    /// Goût du moment : proximité aux derniers titres aimés (profil court terme), en plus du profil de toujours.
    /// Une période « séries coréennes » se voit tout de suite, sans effacer le reste.
    /// </summary>
    public double WShortTerm { get; set; }

    /// <summary>
    /// Heure de la journée : proximité aux titres aimés que le compte a regardés dans la même tranche horaire (nuit,
    /// matin, après-midi, soirée) que maintenant. On ne regarde pas la même chose à 8 h et à 23 h.
    /// </summary>
    public double WTimeOfDay { get; set; }

    /// <summary>Nombre de derniers titres aimés qui forment le goût du moment.</summary>
    public int ShortTermTitles { get; set; } = 5;

    /// <summary>
    /// Montré sans être choisi : un titre vu à l'écran plusieurs jours sans être ouvert descend progressivement
    /// (part maximale retirée de son score, 0 = coupé). Il remonte avec le temps.
    /// </summary>
    public double WImpression { get; set; } = 0.4;

    /// <summary>Jours d'affichage pour atteindre environ les deux tiers de la pénalité.</summary>
    public double ImpressionDays { get; set; } = 7;

    /// <summary>Demi-vie de l'oubli (jours) : la pénalité s'efface d'autant après le dernier affichage.</summary>
    public double ImpressionRecoveryDays { get; set; } = 30;

    /// <summary>
    /// Correction apprise par le calibrage « réseau de neurones » : petit réseau (10 → 8 → 1, tanh) ajouté au score,
    /// à partir des 10 ingrédients du score. Vide = aucune correction (par défaut). Voir <see cref="NeuralNet"/>.
    /// </summary>
    public double[]? Neural { get; set; }

    /// <summary>Force de la correction apprise (0 = coupée, 1 = telle qu'apprise).</summary>
    public double WNeural { get; set; } = 1.0;

    // ---- liste

    /// <summary>Façon de varier la liste.</summary>
    public DiversityMode Diversity { get; set; } = DiversityMode.Personalized;

    /// <summary>Pénalité de redondance (diversité fixe ; base de la diversité personnalisée).</summary>
    public double Mmr { get; set; } = 0.3;

    /// <summary>Diversité calibrée : force de l'alignement sur la répartition des genres de l'historique (0..1).</summary>
    public double CalibrationStrength { get; set; } = 0.5;

    /// <summary>Une collection plus grande est un regroupement thématique, pas une saga.</summary>
    public int SagaMax { get; set; } = 12;

    /// <summary>Règle de saga active.</summary>
    public bool Saga { get; set; } = true;

    /// <summary>Copie indépendante.</summary>
    public EngineSettings Clone() => (EngineSettings)MemberwiseClone();

    /// <summary>Ramène chaque valeur dans un domaine sûr (protège contre une configuration corrompue ou un calibrage).</summary>
    public EngineSettings Sanitized()
    {
        var s = Clone();
        s.HalfLifeDays = Clamp(s.HalfLifeDays, 7, 3650);
        s.RecencyFloor = Clamp(s.RecencyFloor, 0, 1);
        s.FavoriteBoost = Clamp(s.FavoriteBoost, 0, 5);
        s.MovieDone = Clamp(s.MovieDone, 0.3, 1);
        s.Zap = Clamp(s.Zap, 0, 0.3);
        s.AbandonDays = Clamp(s.AbandonDays, 1, 365);
        s.AbandonWeight = Clamp(s.AbandonWeight, 0, 2);
        s.SeriesFullEpisodes = (int)Clamp(s.SeriesFullEpisodes, 1, 200);
        s.SeriesAbandonEpisodes = (int)Clamp(s.SeriesAbandonEpisodes, 0, 20);
        s.SeriesAbandonFraction = Clamp(s.SeriesAbandonFraction, 0, 1);
        s.SeriesMinEpisodes = (int)Clamp(s.SeriesMinEpisodes, 1, 500);
        s.RequestWeight = Clamp(s.RequestWeight, 0, 2);
        s.RequestHalfLifeDays = Clamp(s.RequestHalfLifeDays, 1, 365);
        s.RequestMaxDays = Clamp(s.RequestMaxDays, 1, 730);
        s.Actors = (int)Clamp(s.Actors, 0, 30);
        s.StudiosKept = (int)Clamp(s.StudiosKept, 0, 10);
        s.Shrink = Clamp(s.Shrink, 0, 1000);
        s.MinCommon = (int)Clamp(s.MinCommon, 1, 50);
        s.WFresh = Clamp(s.WFresh, 0, FreshCeiling);
        s.FreshDays = Clamp(s.FreshDays, 1, 365);
        s.GateTop = (int)Clamp(s.GateTop, 5, 500);
        s.Mmr = Clamp(s.Mmr, 0, 2);
        s.SagaMax = (int)Clamp(s.SagaMax, 2, 100);
        s.WatchlistWeight = Clamp(s.WatchlistWeight, 0, 2);
        s.FavSeasonShare = Clamp(s.FavSeasonShare, 0, 1);
        s.FavEpisodeShare = Clamp(s.FavEpisodeShare, 0, 1);
        s.RewatchBoost = Clamp(s.RewatchBoost, 0, 3);
        s.RatingWeight = Clamp(s.RatingWeight, 0, 3);
        s.HiddenWeight = Clamp(s.HiddenWeight, 0, 3);
        s.ReleaseDays = Clamp(s.ReleaseDays, 7, 3650);
        s.RatingPriorVotes = Clamp(s.RatingPriorVotes, 0, 100_000);
        s.HouseholdPrior = Clamp(s.HouseholdPrior, 0, 50);
        s.Bm25K1 = Clamp(s.Bm25K1, 0, 5);
        s.Bm25B = Clamp(s.Bm25B, 0, 1);
        foreach (var p in typeof(EngineSettings).GetProperties())
        {
            if (p.PropertyType == typeof(double) && p.Name.StartsWith('W') && p.CanWrite)
            {
                p.SetValue(s, Clamp((double)p.GetValue(s)!, 0, p.Name == nameof(WFresh) ? FreshCeiling : 10));
            }
            else if (p.PropertyType == typeof(double) && p.Name.StartsWith("Feat", StringComparison.Ordinal) && p.CanWrite)
            {
                p.SetValue(s, Clamp((double)p.GetValue(s)!, 0, 10));
            }
        }

        s.ShortTermTitles = (int)Clamp(s.ShortTermTitles, 1, 30);
        s.WNeural = Clamp(s.WNeural, 0, 2);
        if (s.Neural is not null && (s.Neural.Length != NeuralNet.Size || s.Neural.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100)))
        {
            s.Neural = null; // réseau corrompu ou d'une autre forme : ignoré
        }
        s.SeriesFullHours = Clamp(s.SeriesFullHours, 0.5, 500);
        s.SeriesMaxWeight = Clamp(s.SeriesMaxWeight, 1, 3);
        s.WImpression = Clamp(s.WImpression, 0, 0.9);
        s.ImpressionDays = Clamp(s.ImpressionDays, 1, 90);
        s.ImpressionRecoveryDays = Clamp(s.ImpressionRecoveryDays, 1, 365);
        s.CalibrationStrength = Clamp(s.CalibrationStrength, 0, 1);
        if (!Enum.IsDefined(s.Diversity))
        {
            s.Diversity = DiversityMode.Fixed;
        }

        return s;
    }

    private static double Clamp(double v, double lo, double hi) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;
}
