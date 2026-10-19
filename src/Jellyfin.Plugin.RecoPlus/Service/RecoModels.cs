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

    public string? Overview { get; set; }

    public double Vote { get; set; }

    /// <summary>Titre aimé qui a amené celui-ci (« parce que tu as aimé … »).</summary>
    public string Because { get; set; } = string.Empty;
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

    /// <summary>Goût du compte pour chaque titre de la liste (personnalisation de « Plus comme ceci »).</summary>
    public Dictionary<string, double> Taste { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Tout le cache persistant.</summary>
public sealed class RecsFile
{
    public int Format { get; set; } = 1;

    public string PluginVersion { get; set; } = string.Empty;

    public Dictionary<string, UserRecs> Users { get; set; } = new(StringComparer.Ordinal);
}
