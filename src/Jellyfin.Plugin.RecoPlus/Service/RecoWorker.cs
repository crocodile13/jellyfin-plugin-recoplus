using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RecoPlus.Data;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RecoPlus.Service;

/// <summary>
/// Planifie les recalculs, sans réagir à tout : un compte est recalculé après son activité (lecture terminée, vu, favori,
/// note), une fois l'activité calmée (délai regroupant les actions rapprochées) et au plus une fois par écart minimal ;
/// tous les comptes après des ajouts à la bibliothèque (au plus une fois par heure). Au démarrage, seulement les comptes
/// sans liste (tous d'un coup s'il n'y en a aucune). Enregistrer les réglages ou appliquer un calibrage ne relance rien :
/// bouton « Recalculer ».
/// </summary>
public sealed class RecoWorker : BackgroundService
{
    private static readonly TimeSpan LibraryDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LibraryMinGap = TimeSpan.FromHours(1);
    private readonly RecoService _reco;
    private readonly ILibraryManager _lib;
    private readonly IUserDataManager _userData;
    private readonly ITaskManager _tasks;
    private readonly ILogger<RecoWorker> _log;
    private readonly ConcurrentDictionary<Guid, DateTime> _dueUsers = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastUserRun = new();
    private readonly object _allLock = new();
    private DateTime? _dueAll;
    private DateTime _lastLibraryRun = DateTime.MinValue;
    private bool _libraryPending;

    public RecoWorker(RecoService reco, ILibraryManager lib, IUserDataManager userData, ITaskManager tasks, ILogger<RecoWorker> log)
    {
        _reco = reco;
        _lib = lib;
        _userData = userData;
        _tasks = tasks;
        _log = log;
        Instance = this;
    }

    /// <summary>Instance (pour les consommateurs d'événements, de portée « scoped »).</summary>
    public static RecoWorker? Instance { get; private set; }

    private static bool Enabled => Plugin.Instance?.Configuration.Enabled == true;

    /// <summary>Demande le recalcul d'un compte (regroupé).</summary>
    public void ScheduleUser(Guid user)
    {
        if (!Enabled)
        {
            return;
        }

        var cfg = Plugin.Instance!.Configuration;
        _dueUsers[user] = Due(DateTime.UtcNow, TimeSpan.FromSeconds(Math.Clamp(cfg.DebounceSeconds, 10, 3600)),
            _lastUserRun.TryGetValue(user, out var last) ? last : (DateTime?)null, TimeSpan.FromMinutes(Math.Clamp(cfg.UserMinIntervalMinutes, 0, 1440)));
    }

    /// <summary>
    /// Échéance d'un recalcul : <paramref name="quiet"/> après la dernière action (chaque nouvelle action repousse),
    /// et jamais moins de <paramref name="minGap"/> après le recalcul précédent.
    /// </summary>
    internal static DateTime Due(DateTime now, TimeSpan quiet, DateTime? lastRun, TimeSpan minGap)
    {
        var due = now + quiet;
        return lastRun is { } l && l + minGap > due ? l + minGap : due;
    }

    /// <summary>Demande un recalcul complet (regroupé).</summary>
    public void ScheduleAll(TimeSpan delay)
    {
        if (!Enabled)
        {
            return;
        }

        var due = DateTime.UtcNow + delay;
        lock (_allLock)
        {
            _dueAll = _dueAll is { } d && d < due ? d : due;
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series && Enabled)
        {
            // ajouts en rafale regroupés ; au plus un recalcul complet par heure pour cette raison
            lock (_allLock)
            {
                var due = Due(DateTime.UtcNow, LibraryDelay, _lastLibraryRun == DateTime.MinValue ? (DateTime?)null : _lastLibraryRun, LibraryMinGap);
                _dueAll = _dueAll is { } d && d < due ? d : due;
                _libraryPending = true;
            }
        }
    }

    private void OnUserData(object? sender, UserDataSaveEventArgs e)
    {
        // marqué vu / non vu, favori : signaux forts
        if (e.SaveReason is UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserRating
            or UserDataSaveReason.PlaybackFinished or UserDataSaveReason.UpdateUserData)
        {
            ScheduleUser(e.UserId);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lib.ItemAdded += OnItemAdded;
        _userData.UserDataSaved += OnUserData;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            if (Enabled)
            {
                // listes reprises du cache disque ; on ne calcule que celles qui manquent : tout d'un coup si aucune
                // n'existe (première installation, cache perdu), sinon compte par compte (nouveau compte)
                var (tracked, missing) = _reco.UsersWithoutRecs();
                if (missing.Count > 0 && missing.Count == tracked)
                {
                    _log.LogInformation("Reco+: no cached list, computing all accounts");
                    ScheduleAll(TimeSpan.Zero);
                }
                else
                {
                    foreach (var u in missing)
                    {
                        _log.LogInformation("Reco+: account {User} has no list, computing this account only", u);
                        _dueUsers[u] = DateTime.UtcNow;
                    }
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                if (!Enabled)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                bool runAll = false;
                lock (_allLock)
                {
                    if (_dueAll is { } all && all <= now)
                    {
                        _dueAll = null;
                        runAll = true;
                        if (_libraryPending)
                        {
                            _libraryPending = false;
                            _lastLibraryRun = now;
                        }
                    }
                }

                if (runAll)
                {
                    _dueUsers.Clear();

                    // calcul complet = tâche planifiée « Recalcul des recommandations » (visible, avec sa progression)
                    _log.LogInformation("Reco+: recomputation of all accounts queued (Scheduled Tasks)");
                    _tasks.QueueIfNotRunning<Tasks.NightlyTask>();
                    continue;
                }

                foreach (var (u, due) in _dueUsers)
                {
                    if (due <= now && _dueUsers.TryRemove(u, out _))
                    {
                        _lastUserRun[u] = DateTime.UtcNow;
                        try
                        {
                            await _reco.ComputeAsync(u, stoppingToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            // jamais d'arrêt du planificateur pour un calcul raté : l'erreur est dans l'état du moteur
                            _log.LogError(ex, "Reco+: account recomputation failed");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _lib.ItemAdded -= OnItemAdded;
            _userData.UserDataSaved -= OnUserData;
        }
    }
}

/// <summary>Début de lecture : date du premier lancement.</summary>
public sealed class PlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly HistoryStore _store;
    private readonly LibraryIndex _index;

    public PlaybackStartConsumer(HistoryStore store, LibraryIndex index)
    {
        _store = store;
        _index = index;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs e)
    {
        if (e.Item is null || e.Users is null)
        {
            return Task.CompletedTask;
        }

        foreach (var u in e.Users)
        {
            _store.Record(u.Id, e.Item.Id, DateTime.UtcNow);
            if (e.Item is Episode ep && ep.SeriesId != Guid.Empty)
            {
                _store.Record(u.Id, ep.SeriesId, DateTime.UtcNow);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Fin de lecture : recalcul du compte (regroupé).</summary>
public sealed class PlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    /// <inheritdoc />
    public Task OnEvent(PlaybackStopEventArgs e)
    {
        if (e.Users is not null)
        {
            foreach (var u in e.Users)
            {
                RecoWorker.Instance?.ScheduleUser(u.Id);
            }
        }

        return Task.CompletedTask;
    }
}
