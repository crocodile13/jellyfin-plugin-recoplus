using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Bibliothèque (films + séries) traduite pour le moteur. Reconstruite quand la bibliothèque change.</summary>
public sealed class LibrarySnapshot
{
    /// <summary>Titres de la bibliothèque.</summary>
    public required Dictionary<string, Title> Titles { get; init; }

    /// <summary>Collections (BoxSet) → titres.</summary>
    public required Dictionary<string, List<string>> Collections { get; init; }

    /// <summary>Identifiant TMDb → titre (« movie:123 » / « tv:456 »).</summary>
    public required Dictionary<string, string> ByTmdb { get; init; }

    /// <summary>
    /// Titres en double (même identifiant TMDb, par exemple la même série dans deux bibliothèques) : titre → tous les
    /// exemplaires, le premier étant celui de <see cref="ByTmdb"/>. Seuls les titres qui ont un double y figurent.
    /// </summary>
    public Dictionary<string, string[]> Copies { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Épisode → série, pour agréger l'historique.</summary>
    public required Dictionary<Guid, Guid> EpisodeSeries { get; init; }

    /// <summary>Durée des films et des épisodes (ticks) : avancement d'un film commencé, temps regardé d'une série.</summary>
    public Dictionary<Guid, long> Runtime { get; init; } = new();

    /// <summary>Série → durée moyenne connue d'un épisode (heures), pour les épisodes de durée inconnue.</summary>
    public Dictionary<Guid, double> SeriesMeanHours { get; init; } = new();

    /// <summary>Saison → série (favoris de saison).</summary>
    public Dictionary<Guid, Guid> SeasonSeries { get; init; } = new();

    /// <summary>Série → dates d'ajout de ses épisodes (secondes Unix, triées) : nouveaux épisodes depuis le dernier visionnage.</summary>
    public Dictionary<Guid, double[]> EpisodeAdded { get; init; } = new();

    /// <summary>Série → ses épisodes : identifiant, date d'ajout et date de première diffusion (0 si inconnue), triés par ajout.</summary>
    public Dictionary<Guid, (Guid Id, double Added, double Aired)[]> EpisodeDates { get; init; } = new();

    /// <summary>Date de construction (UTC).</summary>
    public DateTime BuiltAt { get; init; }
}

/// <summary>Construit l'instantané de la bibliothèque à partir de Jellyfin.</summary>
public sealed class LibraryIndex
{
    private readonly ILibraryManager _lib;
    private readonly ILogger<LibraryIndex> _log;
    private readonly object _lock = new();
    private LibrarySnapshot? _current;
    private volatile bool _dirty = true;

    public LibraryIndex(ILibraryManager lib, ILogger<LibraryIndex> log)
    {
        _lib = lib;
        _log = log;
        _lib.ItemAdded += OnChanged;
        _lib.ItemRemoved += OnChanged;
        _lib.ItemUpdated += OnUpdated;
    }

    /// <summary>Vrai si la bibliothèque a changé depuis le dernier instantané.</summary>
    public bool Dirty => _dirty;

    /// <summary>Dernier instantané (null avant le premier calcul).</summary>
    public LibrarySnapshot? Current => _current;

    private volatile bool _softDirty;

    // Une mise à jour de métadonnées est fréquente : on ne reconstruit pour elle qu'au plus toutes les 6 h.
    private void OnUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series or BoxSet)
        {
            _softDirty = true;
        }
    }

    private void OnChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series or BoxSet)
        {
            _dirty = true;
        }
        else if (e.Item is Episode)
        {
            _softDirty = true;                                  // nombre d'épisodes : pas urgent
        }
    }

    /// <summary>Instantané à jour (reconstruit si la bibliothèque a changé).</summary>
    /// <param name="metadata">
    /// Prendre aussi en compte les métadonnées modifiées depuis le dernier instantané (calcul complet), au lieu
    /// d'attendre jusqu'à 6 h : après un nettoyage de métadonnées, le prochain calcul complet les voit.
    /// </param>
    public LibrarySnapshot Get(bool metadata = false)
    {
        lock (_lock)
        {
            if (_current is null || _dirty || (_softDirty && (metadata || DateTime.UtcNow - _current.BuiltAt > TimeSpan.FromHours(6))))
            {
                _dirty = false;
                _softDirty = false;
                try
                {
                    _current = Build();
                }
                catch
                {
                    _dirty = true;
                    throw;
                }
            }

            return _current;
        }
    }

    private static string N(Guid g) => g.ToString("N");

    private static double Unix(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;

    private LibrarySnapshot Build()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = _lib.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
        });
        var episodes = _lib.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        });
        var epSeries = new Dictionary<Guid, Guid>();
        var epCount = new Dictionary<Guid, int>();
        var epAdded = new Dictionary<Guid, List<double>>();
        var epDates = new Dictionary<Guid, List<(Guid, double, double)>>();
        var runtime = new Dictionary<Guid, long>();
        var epHours = new Dictionary<Guid, (double Sum, int N)>();
        foreach (var e in episodes.OfType<Episode>())
        {
            if (e.SeriesId != Guid.Empty)
            {
                epSeries[e.Id] = e.SeriesId;
                epCount[e.SeriesId] = epCount.GetValueOrDefault(e.SeriesId) + 1;
                if (e.RunTimeTicks is > 0)
                {
                    // durée de chaque épisode : poids d'une série selon le temps réellement regardé
                    runtime[e.Id] = e.RunTimeTicks.Value;
                    var (sum, n) = epHours.GetValueOrDefault(e.SeriesId);
                    epHours[e.SeriesId] = (sum + (e.RunTimeTicks.Value / 36_000_000_000.0), n + 1);
                }

                if (e.DateCreated != default)
                {
                    if (!epAdded.TryGetValue(e.SeriesId, out var l))
                    {
                        epAdded[e.SeriesId] = l = new List<double>();
                    }

                    l.Add(Unix(e.DateCreated));
                    if (!epDates.TryGetValue(e.SeriesId, out var dl))
                    {
                        epDates[e.SeriesId] = dl = new List<(Guid, double, double)>();
                    }

                    dl.Add((e.Id, Unix(e.DateCreated), e.PremiereDate is DateTime pd && pd != default ? Unix(pd) : 0));
                }
            }
        }

        var seasonSeries = new Dictionary<Guid, Guid>();
        foreach (var se in _lib.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Season }, Recursive = true, IsVirtualItem = false }).OfType<Season>())
        {
            if (se.SeriesId != Guid.Empty)
            {
                seasonSeries[se.Id] = se.SeriesId;
            }
        }

        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        var byTmdb = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var kind = it is Series ? TitleKind.Series : TitleKind.Movie;
            var people = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var p in _lib.GetPeople(it).OrderBy(p => p.SortOrder ?? int.MaxValue))
            {
                string? role = p.Type switch
                {
                    PersonKind.Actor or PersonKind.GuestStar => "Actor",
                    PersonKind.Director => "Director",
                    PersonKind.Writer => "Writer",
                    PersonKind.Creator => "Creator",
                    _ => null,
                };
                if (role is not null && !string.IsNullOrWhiteSpace(p.Name))
                {
                    if (!people.TryGetValue(role, out var l))
                    {
                        people[role] = l = new List<string>();
                    }

                    if (!l.Contains(p.Name))
                    {
                        l.Add(p.Name);
                    }
                }
            }

            var id = N(it.Id);
            if (it.RunTimeTicks is > 0)
            {
                runtime[it.Id] = it.RunTimeTicks.Value;
            }

            titles[id] = new Title
            {
                Id = id,
                Kind = kind,
                Name = it.Name ?? string.Empty,
                Year = it.ProductionYear,
                Genres = it.Genres ?? Array.Empty<string>(),
                Tags = it.Tags ?? Array.Empty<string>(),
                Studios = it.Studios ?? Array.Empty<string>(),
                People = people.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
                Community = it.CommunityRating,
                Added = it.DateCreated == default ? null : new DateTimeOffset(DateTime.SpecifyKind(it.DateCreated, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                EpisodeCount = kind == TitleKind.Series ? epCount.GetValueOrDefault(it.Id) : 0,
                Released = it.PremiereDate is { } pd && pd != default ? Unix(pd) : null,
                Critic = it.CriticRating is float cr && cr > 0 ? cr : null,
                Countries = it.ProductionLocations ?? Array.Empty<string>(),
                Language = string.IsNullOrWhiteSpace(it.OriginalLanguage) ? null : it.OriginalLanguage,
            };
            var tmdb = it.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(tmdb))
            {
                var tk = (kind == TitleKind.Series ? "tv:" : "movie:") + tmdb;
                if (!byTmdb.TryAdd(tk, id))
                {
                    if (!copies.TryGetValue(tk, out var cl))
                    {
                        copies[tk] = cl = new List<string> { byTmdb[tk] };
                    }

                    cl.Add(id);
                }
            }
        }

        var colls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var b in _lib.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.BoxSet }, Recursive = true }).OfType<BoxSet>())
        {
            var kids = b.GetLinkedChildren().Select(k => N(k.Id)).Where(titles.ContainsKey).Distinct().ToList();
            if (kids.Count > 0)
            {
                colls[N(b.Id)] = kids;
            }
        }

        _log.LogInformation("Reco+: library indexed — {Titles} titles, {Eps} episodes, {Colls} collections in {Ms} ms",
            titles.Count, episodes.Count, colls.Count, sw.ElapsedMilliseconds);
        return new LibrarySnapshot
        {
            Titles = titles,
            Collections = colls,
            ByTmdb = byTmdb,
            Copies = copies.Values.SelectMany(g => g.Select(id => (id, g))).ToDictionary(x => x.id, x => x.g.ToArray(), StringComparer.Ordinal),
            EpisodeSeries = epSeries,
            Runtime = runtime,
            SeriesMeanHours = epHours.ToDictionary(kv => kv.Key, kv => kv.Value.Sum / kv.Value.N),
            SeasonSeries = seasonSeries,
            EpisodeAdded = epAdded.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToArray()),
            EpisodeDates = epDates.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x.Item2).ToArray()),
            BuiltAt = DateTime.UtcNow,
        };
    }
}
