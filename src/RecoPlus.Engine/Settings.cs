using System;

namespace RecoPlus.Engine;

/// <summary>Décroissance du poids d'un titre avec l'ancienneté de la dernière activité.</summary>
public enum DecayKind
{
    /// <summary>0,5^(âge / demi-vie).</summary>
    Exponential,

    /// <summary>(1 + âge / demi-vie)^-1 (Larrain, Parra et al., RecSys 2015).</summary>
    Power
}

/// <summary>Pondération des caractéristiques de contenu.</summary>
public enum WeightingScheme
{
    /// <summary>TF-IDF.</summary>
    TfIdf,

    /// <summary>BM25 (k1, b).</summary>
    Bm25
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

    /// <summary>Épisodes vus pour qu'une série compte « pleinement ».</summary>
    public int SeriesFullEpisodes { get; set; } = 10;

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

    // ---- liste

    /// <summary>Pénalité de redondance (diversité).</summary>
    public double Mmr { get; set; } = 0.3;

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

        return s;
    }

    private static double Clamp(double v, double lo, double hi) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;
}
