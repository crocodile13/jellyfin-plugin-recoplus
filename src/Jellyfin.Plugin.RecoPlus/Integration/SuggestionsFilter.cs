using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>
/// Remplace la réponse de /Items/Suggestions et /Users/{id}/Suggestions (du hasard dans Jellyfin) par la liste du compte.
/// Placé au plus près de l'action : les filtres des autres plugins (contenu masqué de Jellyfin Enhanced…) continuent
/// de s'appliquer à notre réponse. Cas non couverts (types demandés autres que films/séries, aucun compte, liste pas
/// prête, partie coupée) : la vraie action de Jellyfin s'exécute.
/// </summary>
public sealed class SuggestionsFilter : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>Actions interceptées.</summary>
    public static readonly string[] Actions = { "GetSuggestions", "GetSuggestionsLegacy" };

    private readonly RecoService _reco;
    private readonly ILibraryManager _lib;
    private readonly IUserManager _users;
    private readonly IDtoService _dto;

    public SuggestionsFilter(RecoService reco, ILibraryManager lib, IUserManager users, IDtoService dto)
    {
        _reco = reco;
        _lib = lib;
        _users = users;
        _dto = dto;
    }

    /// <summary>Ordre très élevé : exécuté en dernier avant l'action, donc enveloppé par les filtres des autres plugins.</summary>
    public int Order => int.MaxValue - 100;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor d
            && d.ControllerName == "Suggestions"
            && Array.IndexOf(Actions, d.ActionName) >= 0
            && CompatibilityGuard.Allowed(Feature.Suggestions))
        {
            try
            {
                var result = TryAnswer(context);
                if (result is not null)
                {
                    context.Result = new OkObjectResult(result);
                    return;
                }
            }
            catch (Exception ex)
            {
                CompatibilityGuard.Instance?.ReportError(Feature.Suggestions, ex);
            }
        }

        await next().ConfigureAwait(false);
    }

    private static T? Arg<T>(ActionExecutingContext c, string name)
        => c.ActionArguments.TryGetValue(name, out var v) && v is T t ? t : default;

    private QueryResult<BaseItemDto>? TryAnswer(ActionExecutingContext c)
    {
        // même résolution du compte que Jellyfin : paramètre userId, sinon le compte de la session
        var userId = Arg<Guid?>(c, "userId") ?? (c.ActionArguments.TryGetValue("userId", out var raw) && raw is Guid g ? g : (Guid?)null);
        if (userId is null || userId == Guid.Empty)
        {
            var claim = c.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
            userId = Guid.TryParse(claim, out var cu) ? cu : null;
        }

        if (userId is null)
        {
            return null;
        }

        // un compte ne lit que ses propres suggestions, sauf administrateur (comme Jellyfin)
        var callerClaim = c.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
        bool isAdmin = c.HttpContext.User.IsInRole("Administrator");
        if (Guid.TryParse(callerClaim, out var caller) && caller != userId && !isAdmin)
        {
            return null; // Jellyfin répondra (et refusera) lui-même
        }

        var user = _users.GetUserById(userId.Value);
        var r = user is null ? null : _reco.For(user.Id);
        if (user is null || r is null)
        {
            return null;
        }

        var types = Arg<BaseItemKind[]>(c, "type") ?? Array.Empty<BaseItemKind>();
        if (types.Any(t => t is not (BaseItemKind.Movie or BaseItemKind.Series)))
        {
            return null; // épisodes, musique… : pas notre rayon
        }

        var mediaTypes = Arg<MediaType[]>(c, "mediaType") ?? Array.Empty<MediaType>();
        if (mediaTypes.Any(m => m != MediaType.Video))
        {
            return null;
        }

        int start = Math.Max(0, Arg<int?>(c, "startIndex") ?? 0);
        int limit = Math.Clamp(Arg<int?>(c, "limit") ?? 20, 1, 500);
        var ordered = r.Pinned.Concat(r.Ranked).Distinct().Select(x => Guid.ParseExact(x, "N")).ToArray();
        var q = new InternalItemsQuery(user)
        {
            ItemIds = ordered,
            IncludeItemTypes = types.Length > 0 ? types : new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
        };
        var allowed = _lib.GetItemList(q).ToDictionary(i => i.Id);
        var list = ordered.Where(allowed.ContainsKey).Select(x => allowed[x]).ToList();
        var page = list.Skip(start).Take(limit).ToList();
        var opts = new DtoOptions();
        var dtos = _dto.GetBaseItemDtos(page, opts, user);
        return new QueryResult<BaseItemDto>(start, list.Count, dtos);
    }
}
