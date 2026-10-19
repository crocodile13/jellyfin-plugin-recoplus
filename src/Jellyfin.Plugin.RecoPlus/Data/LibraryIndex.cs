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

    /// <summary>Épisode → série, pour agréger l'historique.</summary>
    public required Dictionary<Guid, Guid> EpisodeSeries { get; init; }

    /// <summary>Durée des films (ticks), pour l'avancement d'un film commencé.</summary>
    public Dictionary<Guid, long> Runtime { get; init; } = new();

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
    public LibrarySnapshot Get()
    {
        lock (_lock)
        {
            if (_current is null || _dirty || (_softDirty && DateTime.UtcNow - _current.BuiltAt > TimeSpan.FromHours(6)))
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
        foreach (var e in episodes.OfType<Episode>())
        {
            if (e.SeriesId != Guid.Empty)
            {
                epSeries[e.Id] = e.SeriesId;
                epCount[e.SeriesId] = epCount.GetValueOrDefault(e.SeriesId) + 1;
            }
        }

        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        var runtime = new Dictionary<Guid, long>();
        var byTmdb = new Dictionary<string, string>(StringComparer.Ordinal);
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
            };
            var tmdb = it.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrEmpty(tmdb))
            {
                byTmdb.TryAdd((kind == TitleKind.Series ? "tv:" : "movie:") + tmdb, id);
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

        _log.LogInformation("Reco+ : bibliothèque indexée — {Titles} titres, {Eps} épisodes, {Colls} collections en {Ms} ms",
            titles.Count, episodes.Count, colls.Count, sw.ElapsedMilliseconds);
        return new LibrarySnapshot { Titles = titles, Collections = colls, ByTmdb = byTmdb, EpisodeSeries = epSeries, Runtime = runtime, BuiltAt = DateTime.UtcNow };
    }
}
