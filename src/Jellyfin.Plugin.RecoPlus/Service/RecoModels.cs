using System;
using System.Collections.Generic;
using RecoPlus.Engine;

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

    /// <summary>Pourquoi ce titre est proposé, en clair (« Parce que tu as aimé … », « De Hayao Miyazaki », « Bien noté en animation »…).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Source d'exploration qui l'a apporté (related, saga, people, keyword, genre, cross, gems, studio, language, top).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Rangée d'accueil où il est placé (identifiant de rangée, ex. « PourToi », « Films ») ; vide = aucune.</summary>
    public string Row { get; set; } = string.Empty;

    /// <summary>
    /// Nombre de titres de la bibliothèque qui le précèdent dans sa rangée (hors demandes en tête de « Pour toi ») ;
    /// -1 = pas placé.
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

    /// <summary>Composition des rangées avec titres hors bibliothèque (voir <see cref="UserRecs.Layouts"/>).</summary>
    public Dictionary<string, List<string>> Layouts { get; set; } = new(StringComparer.Ordinal);
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

    /// <summary>Bilan de la dernière exploration, par source (aperçu de la configuration).</summary>
    public Dictionary<string, SourceStat> Sources { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Compte Jellyseerr lié (0 = aucun : pas de bouton Demander).</summary>
    public int SeerrUserId { get; set; }

    /// <summary>Rangées d'accueil supplémentaires (identifiant → titres), sans doublons entre elles ni avec « Pour toi ».</summary>
    public Dictionary<string, List<string>> Rows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Rangées « Parce que … » : titre aimé de chacune.</summary>
    public Dictionary<string, string> RowAnchors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Goût du compte pour chaque titre de la liste (personnalisation de « Plus comme ceci »).</summary>
    public Dictionary<string, double> Taste { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Rangées d'accueil avec titres hors bibliothèque (identifiant de rangée → cartes dans l'ordre) : « » = la carte
    /// suivante de la bibliothèque telle que Home Screen Sections l'affiche, « movie:123 » / « tv:123 » = un titre hors
    /// bibliothèque. Rangée absente = bibliothèque seule.
    /// </summary>
    public Dictionary<string, List<string>> Layouts { get; set; } = new(StringComparer.Ordinal);

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
        c.Layouts = v.Layouts.Count > 0 ? v.Layouts : Layouts;
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
