using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Data;

/// <summary>Ce que Jellyfin sait d'un compte pour un élément (agrégé si plusieurs clés).</summary>
public sealed class UserDataRow
{
    public Guid UserId { get; init; }

    public Guid ItemId { get; init; }

    public bool Played { get; set; }

    public bool Favorite { get; set; }

    public int PlayCount { get; set; }

    public long Position { get; set; }

    public DateTime? LastPlayed { get; set; }

    /// <summary>Note donnée dans Jellyfin (0-10), si un client en a enregistré une.</summary>
    public double? Rating { get; set; }

    /// <summary>Pouce Jellyfin (vrai = aime, faux = n'aime pas), si un client l'a enregistré.</summary>
    public bool? Likes { get; set; }
}

/// <summary>
/// Lit en UNE requête légère (projection, lecture seule) la table des données utilisateur de Jellyfin, pour tous les
/// comptes. Remplace les requêtes « vus / en cours / favoris » par compte, dont la requête « favoris » fait exploser
/// la mémoire de Jellyfin 12.2 (constaté : 7 Go).
/// </summary>
public sealed class UserDataReader
{
    private readonly IDbContextFactory<JellyfinDbContext> _db;
    private readonly ILogger<UserDataReader> _log;

    public UserDataReader(IDbContextFactory<JellyfinDbContext> db, ILogger<UserDataReader> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Compte → (élément → données), uniquement les lignes avec une activité ou un favori.</summary>
    public Dictionary<Guid, Dictionary<Guid, UserDataRow>> ReadAll(Guid? onlyUser = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctx = _db.CreateDbContext();
        var q = ctx.UserData.Where(u => u.Played || u.IsFavorite || u.PlaybackPositionTicks > 0 || u.LastPlayedDate != null
            || u.Rating != null || u.Likes != null);
        if (onlyUser is Guid ou)
        {
            q = q.Where(u => u.UserId == ou);
        }

        var rows = q.Select(u => new { u.UserId, u.ItemId, u.Played, u.IsFavorite, u.PlayCount, u.PlaybackPositionTicks, u.LastPlayedDate, u.Rating, u.Likes }).ToList();
        var res = new Dictionary<Guid, Dictionary<Guid, UserDataRow>>();
        foreach (var r in rows)
        {
            if (!res.TryGetValue(r.UserId, out var per))
            {
                res[r.UserId] = per = new Dictionary<Guid, UserDataRow>();
            }

            if (!per.TryGetValue(r.ItemId, out var row))
            {
                per[r.ItemId] = row = new UserDataRow { UserId = r.UserId, ItemId = r.ItemId };
            }

            // plusieurs clés pour un même élément : on agrège
            row.Played |= r.Played;
            row.Favorite |= r.IsFavorite;
            row.PlayCount = Math.Max(row.PlayCount, r.PlayCount);
            row.Rating ??= r.Rating;
            row.Likes ??= r.Likes;
            if (r.LastPlayedDate is { } lp && (row.LastPlayed is null || lp > row.LastPlayed))
            {
                row.LastPlayed = lp;
                row.Position = r.PlaybackPositionTicks;
            }
            else if (row.LastPlayed is null)
            {
                row.Position = Math.Max(row.Position, r.PlaybackPositionTicks);
            }
        }

        _log.LogInformation("Reco+: user data read — {Rows} rows, {Users} accounts in {Ms} ms", rows.Count, res.Count, sw.ElapsedMilliseconds);
        return res;
    }
}
