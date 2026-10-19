using System;
using System.Collections.Generic;

namespace RecoPlus.Engine;

/// <summary>Film ou série (les épisodes sont agrégés par série).</summary>
public enum TitleKind
{
    /// <summary>Film.</summary>
    Movie,

    /// <summary>Série.</summary>
    Series
}

/// <summary>Un titre et ses métadonnées de contenu. Les dates sont en secondes Unix.</summary>
public sealed class Title
{
    /// <summary>Identifiant stable (Guid Jellyfin en « N », ou « tmdb:movie:123 » pour un titre hors bibliothèque).</summary>
    public required string Id { get; init; }

    /// <summary>Film ou série.</summary>
    public required TitleKind Kind { get; init; }

    /// <summary>Nom affiché.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Année de sortie.</summary>
    public int? Year { get; init; }

    /// <summary>Genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Mots-clés (tags Jellyfin = mots-clés TMDb).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Studios (ou chaînes pour une série).</summary>
    public IReadOnlyList<string> Studios { get; init; } = Array.Empty<string>();

    /// <summary>Personnes par rôle (« Actor », « Director », « Writer », « Creator »), dans l'ordre du générique.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> People { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Note de la communauté (0-10).</summary>
    public double? Community { get; init; }

    /// <summary>Date d'ajout effective (secondes Unix), ou null.</summary>
    public double? Added { get; set; }

    /// <summary>Nombre d'épisodes (séries).</summary>
    public int EpisodeCount { get; init; }

    /// <summary>Collections qui contiennent ce titre.</summary>
    public List<string> Collections { get; } = new();

    /// <summary>Vrai si le titre n'est pas dans la bibliothèque (catalogue Jellyseerr).</summary>
    public bool External { get; init; }

    /// <summary>Copie (collections vides, à relier par Dataset.LinkCollections) : chaque calcul travaille sur ses propres objets.</summary>
    public Title Copy() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        Year = Year,
        Genres = Genres,
        Tags = Tags,
        Studios = Studios,
        People = People,
        Community = Community,
        Added = Added,
        EpisodeCount = EpisodeCount,
        External = External,
    };
}

/// <summary>Ce qu'un utilisateur a fait d'un titre.</summary>
public sealed class Interaction
{
    /// <summary>Titre concerné.</summary>
    public required string Title { get; init; }

    /// <summary>Premier contact (secondes Unix, 0 si inconnu).</summary>
    public double Start { get; set; }

    /// <summary>Dernière activité (secondes Unix, 0 si inconnue).</summary>
    public double Last { get; set; }

    /// <summary>Avancement 0..1 (film : vu = 1 ou position ; série : part des épisodes vus).</summary>
    public double Completion { get; set; }

    /// <summary>Film vu, ou au moins un épisode vu.</summary>
    public bool Played { get; set; }

    /// <summary>Épisodes vus (séries).</summary>
    public int Episodes { get; set; }

    /// <summary>Nombre de lectures.</summary>
    public int Plays { get; set; }

    /// <summary>Favori.</summary>
    public bool Favorite { get; set; }

    /// <summary>Demandé dans Jellyseerr (date de la demande, secondes Unix), 0 sinon.</summary>
    public double Requested { get; set; }

    /// <summary>Copie indépendante.</summary>
    public Interaction Copy() => (Interaction)MemberwiseClone();
}

/// <summary>Bibliothèque + historiques, figés à un instant.</summary>
public sealed class Dataset
{
    /// <summary>Titres par identifiant (ordre d'insertion conservé).</summary>
    public required Dictionary<string, Title> Titles { get; init; }

    /// <summary>Historique par utilisateur : titre → interaction (ordre d'insertion conservé).</summary>
    public required Dictionary<string, Dictionary<string, Interaction>> Users { get; init; }

    /// <summary>Collections : identifiant → titres (dans l'ordre de la collection, filtrés aux titres connus).</summary>
    public Dictionary<string, List<string>> Collections { get; init; } = new();

    /// <summary>Instant de référence (secondes Unix).</summary>
    public double Now { get; init; }

    /// <summary>Renseigne Title.Collections à partir de Collections (à appeler une fois après construction).</summary>
    public void LinkCollections()
    {
        foreach (var t in Titles.Values)
        {
            t.Collections.Clear();
        }

        foreach (var (cid, items) in Collections)
        {
            foreach (var id in items)
            {
                if (Titles.TryGetValue(id, out var t))
                {
                    t.Collections.Add(cid);
                }
            }
        }
    }
}
