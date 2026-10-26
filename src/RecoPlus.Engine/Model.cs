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

    /// <summary>Date de sortie (première diffusion), secondes Unix, ou null. À ne pas confondre avec l'ajout (Added).</summary>
    public double? Released { get; init; }

    /// <summary>Note des critiques (0-100, ex. Rotten Tomatoes), ou null.</summary>
    public double? Critic { get; init; }

    /// <summary>Pays de production, dans l'ordre des métadonnées.</summary>
    public IReadOnlyList<string> Countries { get; init; } = Array.Empty<string>();

    /// <summary>Langue originale (code ISO à deux lettres), ou null.</summary>
    public string? Language { get; init; }

    /// <summary>Nombre de votes derrière la note de la communauté (TMDb via Seerr), ou null si inconnu.</summary>
    public int? Votes { get; set; }

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
        Released = Released,
        Critic = Critic,
        Countries = Countries,
        Language = Language,
        Votes = Votes,
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

    /// <summary>Ajouté à la liste de suivi Seerr (date, secondes Unix), 0 sinon.</summary>
    public double Watchlisted { get; set; }

    /// <summary>Revisionnage, 0..1 (film revu ; série : épisodes revus). Calculé à partir des nombres de lectures.</summary>
    public double Rewatch { get; set; }

    /// <summary>Saisons de cette série mises en favori (séries).</summary>
    public int FavSeasons { get; set; }

    /// <summary>Épisodes de cette série mis en favori (séries).</summary>
    public int FavEpisodes { get; set; }

    /// <summary>Note explicite du compte, ramenée à -1..+1 (avis, pouce), ou null.</summary>
    public double? Rating { get; set; }

    /// <summary>Masqué par le compte (« pas intéressé ») : signal négatif fort, jamais reproposé.</summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Séries : épisodes arrivés dans la bibliothèque après le dernier visionnage d'une série rattrapée (sinon 0).
    /// Information pour la mise en avant des nouvelles saisons ; le moteur ne s'en sert pas.
    /// </summary>
    public int NewEpisodes { get; set; }

    /// <summary>Séries : heures regardées (somme des durées des épisodes vus), 0 si inconnu (estimé d'après les épisodes).</summary>
    public double Hours { get; set; }

    /// <summary>
    /// Date inconnue : marqué « vu » sans date, ou marqué en masse (plusieurs titres en quelques minutes). Compte comme
    /// un goût ancien (poids minimal d'un titre ancien), connu depuis toujours pour le calibrage, jamais comme un choix à prédire.
    /// </summary>
    public bool Backfill { get; set; }

    /// <summary>Copie indépendante.</summary>
    public Interaction Copy() => (Interaction)MemberwiseClone();
}

/// <summary>
/// Affichages d'un titre à un compte sans qu'il l'ouvre : nombre de jours distincts où il a été vu à l'écran (rangée
/// effectivement affichée au compte connecté), dernier affichage (secondes Unix). Remis à zéro quand le titre est ouvert.
/// </summary>
public readonly record struct Exposure(int Days, double Last);

/// <summary>Bibliothèque + historiques, figés à un instant.</summary>
public sealed class Dataset
{
    /// <summary>Titres par identifiant (ordre d'insertion conservé).</summary>
    public required Dictionary<string, Title> Titles { get; init; }

    /// <summary>Historique par utilisateur : titre → interaction (ordre d'insertion conservé).</summary>
    public required Dictionary<string, Dictionary<string, Interaction>> Users { get; init; }

    /// <summary>Collections : identifiant → titres (dans l'ordre de la collection, filtrés aux titres connus).</summary>
    public Dictionary<string, List<string>> Collections { get; init; } = new();

    /// <summary>
    /// Collections qui ne sont pas des sagas (titres de plusieurs univers : filmographie, thème) : au plus deux entrées
    /// par liste, jamais de « premier volet » (voir <see cref="Sagas"/>).
    /// </summary>
    public HashSet<string> Thematic { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Instant de référence (secondes Unix).</summary>
    public double Now { get; init; }

    /// <summary>Avis notés du foyer (Jellyfin Enhanced) : titre → compte → étoiles (1 à 5).</summary>
    public Dictionary<string, Dictionary<string, double>> Reviews { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Heure locale (0-23) d'un instant en secondes Unix ; null = heure UTC.</summary>
    public Func<double, int>? LocalHour { get; set; }

    /// <summary>Heure locale d'un instant.</summary>
    public int HourOf(double t) => LocalHour?.Invoke(t) ?? (int)(((t % 86400) + 86400) % 86400 / 3600);

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
