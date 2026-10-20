using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.RecoPlus.Service;

/// <summary>Une catégorie « Parce que tu as aimé X » (pour /Movies/Recommendations).</summary>
public sealed class AnchorCategory
{
    /// <summary>Titre de départ (bibliothèque, « N »).</summary>
    public string AnchorId { get; set; } = string.Empty;

    /// <summary>Nom du titre de départ.</summary>
    public string AnchorName { get; set; } = string.Empty;

    /// <summary>Vu récemment (sinon : aimé).</summary>
    public bool Recent { get; set; }

    /// <summary>Films recommandés, du plus proche au moins proche.</summary>
    public List<string> Items { get; set; } = new();
}

/// <summary>Titre hors bibliothèque proposé par le mode étendu.</summary>
public sealed class ExternalRec
{
    public string MediaType { get; set; } = "movie";

    public int TmdbId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? PosterPath { get; set; }

    /// <summary>Image de fond TMDb (cartes en paysage dans les rangées Home Screen Sections).</summary>
    public string? BackdropPath { get; set; }

    public double Vote { get; set; }

    /// <summary>Titre aimé qui a amené celui-ci (« parce que tu as aimé … »).</summary>
    public string Because { get; set; } = string.Empty;

    /// <summary>
    /// Mélange libre : nombre de titres de la bibliothèque (rangée « Pour toi », hors demandes en tête) que le moteur classe
    /// devant celui-ci ; -1 = non calculé.
    /// </summary>
    public int After { get; set; } = -1;
}

/// <summary>Listes d'un compte pour une tranche horaire (heure de la journée).</summary>
public sealed class SlotRecs
{
    public List<string> Ranked { get; set; } = new();

    public Dictionary<string, double> Taste { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> Rows { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> RowAnchors { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Résultat précalculé pour un compte.</summary>
public sealed class UserRecs
{
    public DateTime ComputedAt { get; set; }

    /// <summary>Demandés, arrivés et pas encore vus (en tête).</summary>
    public List<string> Pinned { get; set; } = new();

    /// <summary>Liste classée (films et séries, bibliothèque), sans les épinglés.</summary>
    public List<string> Ranked { get; set; } = new();

    /// <summary>Catégories pour /Movies/Recommendations.</summary>
    public List<AnchorCategory> MovieCategories { get; set; } = new();

    /// <summary>Mode étendu.</summary>
    public List<ExternalRec> Extended { get; set; } = new();

    /// <summary>Compte Jellyseerr lié (0 = aucun : pas de bouton Demander).</summary>
    public int SeerrUserId { get; set; }

    /// <summary>Rangées d'accueil supplémentaires (identifiant → titres), sans doublons entre elles ni avec « Pour toi ».</summary>
    public Dictionary<string, List<string>> Rows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Rangées « Parce que … » : titre aimé de chacune.</summary>
    public Dictionary<string, string> RowAnchors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Goût du compte pour chaque titre de la liste (personnalisation de « Plus comme ceci »).</summary>
    public Dictionary<string, double> Taste { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Heure de la journée : listes par tranche horaire (0 nuit, 1 matin, 2 après-midi, 3 soirée), vide sinon.</summary>
    public Dictionary<int, SlotRecs> Slots { get; set; } = new();

    /// <summary>Copie servant les listes d'une tranche horaire (le reste est partagé, rien n'est modifié).</summary>
    public UserRecs WithSlot(SlotRecs v)
    {
        var c = (UserRecs)MemberwiseClone();
        c.Ranked = v.Ranked;
        c.Taste = v.Taste;
        c.Rows = v.Rows;
        c.RowAnchors = v.RowAnchors;
        return c;
    }
}

/// <summary>Tout le cache persistant.</summary>
public sealed class RecsFile
{
    public int Format { get; set; } = 1;

    public string PluginVersion { get; set; } = string.Empty;

    public Dictionary<string, UserRecs> Users { get; set; } = new(StringComparer.Ordinal);
}
