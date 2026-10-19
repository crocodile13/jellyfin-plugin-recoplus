using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>
/// Enveloppe le gestionnaire de Jellyfin : nos réponses quand elles sont prêtes, sinon (ou en cas d'erreur, ou partie
/// coupée) exactement celles de Jellyfin. Toutes les autres méthodes passent telles quelles.
/// </summary>
public sealed class SimilarItemsManagerDecorator : ISimilarItemsManager
{
    private readonly ISimilarItemsManager _inner;
    private readonly IServiceProvider _sp;
    private RecoService? _reco;
    private ILibraryManager? _lib;

    public SimilarItemsManagerDecorator(ISimilarItemsManager inner, IServiceProvider sp)
    {
        _inner = inner;
        _sp = sp;
    }

    // résolus à la demande : le décorateur est construit très tôt, avant nos propres services
    private RecoService Reco => _reco ??= (RecoService)_sp.GetService(typeof(RecoService))!;

    private ILibraryManager Lib => _lib ??= (ILibraryManager)_sp.GetService(typeof(ILibraryManager))!;

    /// <inheritdoc />
    public void AddParts(IEnumerable<ISimilarItemsProvider> providers) => _inner.AddParts(providers);

    /// <inheritdoc />
    public IReadOnlyList<ISimilarItemsProvider> GetSimilarItemsProviders<T>()
        where T : BaseItem
        => _inner.GetSimilarItemsProviders<T>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(BaseItem item, IReadOnlyList<Guid> excludeArtistIds, User? user,
        DtoOptions dtoOptions, int? limit, LibraryOptions? libraryOptions, CancellationToken cancellationToken)
    {
        if ((item is Movie || item is Series) && CompatibilityGuard.Allowed(Feature.Similar))
        {
            try
            {
                var mine = Similar(item, user, limit ?? 50);
                if (mine is not null)
                {
                    return mine;
                }
            }
            catch (Exception ex)
            {
                CompatibilityGuard.Instance?.ReportError(Feature.Similar, ex);
            }
        }

        return await _inner.GetSimilarItemsAsync(item, excludeArtistIds, user, dtoOptions, limit, libraryOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ressemblance de contenu (+ part de goût du compte), même type, saga : volet suivant d'abord.</summary>
    private IReadOnlyList<BaseItem>? Similar(BaseItem item, User? user, int limit)
    {
        var vec = Reco.Vectors;
        var lib = Reco.Library;
        var id = item.Id.ToString("N");
        if (vec is null || lib is null || !vec.TryGetValue(id, out var v) || !lib.Titles.TryGetValue(id, out var src))
        {
            return null; // pas encore calculé : réponse de Jellyfin
        }

        var taste = user is null ? null : Reco.For(user.Id)?.Taste;
        double maxTaste = taste is { Count: > 0 } ? Math.Max(1e-9, taste.Values.Max()) : 1;
        double pers = Math.Clamp(Plugin.Instance!.Configuration.SimilarPersonalization, 0, 1);
        var scored = new List<(string Id, double S)>();
        foreach (var (tid, t) in lib.Titles)
        {
            if (t.Kind != src.Kind || tid == id || !vec.TryGetValue(tid, out var tv))
            {
                continue;
            }

            double sim = v.Dot(tv);
            if (sim <= 0.02)
            {
                continue;
            }

            double tasteN = taste is not null && taste.TryGetValue(tid, out var ts) ? Math.Max(0, ts) / maxTaste : 0;
            scored.Add((tid, ((1 - pers) * sim) + (pers * tasteN * sim)));
        }

        // saga : le volet suivant (par année) de la même collection passe devant
        var sagaNext = lib.Collections.Where(c => c.Value.Count <= 12 && c.Value.Contains(id))
            .SelectMany(c => c.Value).Where(x => x != id && lib.Titles.TryGetValue(x, out var t) && t.Kind == src.Kind && (t.Year ?? 9999) >= (src.Year ?? 0))
            .OrderBy(x => lib.Titles[x].Year ?? 9999).Take(1).ToHashSet(StringComparer.Ordinal);
        var ids = scored.OrderByDescending(x => sagaNext.Contains(x.Id) ? double.MaxValue : x.S)
            .Take(Math.Clamp(limit, 1, 200) * 2)
            .Select(x => Guid.ParseExact(x.Id, "N"))
            .ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<BaseItem>();
        }

        // droits du compte (bibliothèques, contrôle parental) appliqués par Jellyfin lui-même
        var allowed = Lib.GetItemList(new InternalItemsQuery(user)
        {
            ItemIds = ids,
            IncludeItemTypes = new[] { item is Series ? BaseItemKind.Series : BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false,
        }).ToDictionary(i => i.Id);
        return ids.Where(allowed.ContainsKey).Select(g => allowed[g]).Take(Math.Clamp(limit, 1, 200)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SimilarItemsRecommendation>> GetMovieRecommendationsAsync(User? user, Guid parentId, int categoryLimit,
        int itemLimit, DtoOptions dtoOptions, CancellationToken cancellationToken)
    {
        if (user is not null && CompatibilityGuard.Allowed(Feature.MovieRecommendations))
        {
            try
            {
                var mine = MovieRecommendations(user, parentId, categoryLimit, itemLimit);
                if (mine is not null)
                {
                    return mine;
                }
            }
            catch (Exception ex)
            {
                CompatibilityGuard.Instance?.ReportError(Feature.MovieRecommendations, ex);
            }
        }

        return await _inner.GetMovieRecommendationsAsync(user, parentId, categoryLimit, itemLimit, dtoOptions, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<SimilarItemsRecommendation>? MovieRecommendations(User user, Guid parentId, int categoryLimit, int itemLimit)
    {
        var r = Reco.For(user.Id);
        if (r is null || r.MovieCategories.Count == 0)
        {
            return null;
        }

        // filtre bibliothèque (parentId) + droits du compte, en une requête
        var all = r.MovieCategories.SelectMany(c => c.Items).Concat(r.Pinned).Distinct().Select(x => Guid.ParseExact(x, "N")).ToArray();
        var q = new InternalItemsQuery(user)
        {
            ItemIds = all,
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false,
        };
        if (parentId != Guid.Empty)
        {
            q.ParentId = parentId;
        }

        var allowed = Lib.GetItemList(q).ToDictionary(i => i.Id.ToString("N"), StringComparer.Ordinal);
        var res = new List<SimilarItemsRecommendation>();
        var pinned = r.Pinned.Where(allowed.ContainsKey).Select(x => allowed[x]).ToList();
        foreach (var c in r.MovieCategories)
        {
            var items = c.Items.Where(allowed.ContainsKey).Select(x => allowed[x]).ToList();
            if (res.Count == 0 && pinned.Count > 0)
            {
                items = pinned.Concat(items).ToList(); // demandés et arrivés : en tête de la première catégorie
            }

            items = items.Take(Math.Clamp(itemLimit, 1, 100)).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            res.Add(new SimilarItemsRecommendation
            {
                BaselineItemName = c.AnchorName,
                CategoryId = Guid.TryParseExact(c.AnchorId, "N", out var g) ? g : Guid.Empty,
                RecommendationType = c.Recent ? RecommendationType.SimilarToRecentlyPlayed : RecommendationType.SimilarToLikedItem,
                Items = items,
            });
            if (res.Count >= Math.Clamp(categoryLimit, 1, 50))
            {
                break;
            }
        }

        return res.Count > 0 ? res : null;
    }
}
