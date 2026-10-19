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
/// Planifie les recalculs : au démarrage, après chaque lecture (par compte, avec délai pour regrouper),
/// après un ajout à la bibliothèque ou un changement de réglage (tous les comptes, via la tâche planifiée).
/// </summary>
public sealed class RecoWorker : BackgroundService
{
    private static readonly TimeSpan LibraryDelay = TimeSpan.FromMinutes(10);
    private readonly RecoService _reco;
    private readonly ILibraryManager _lib;
    private readonly IUserDataManager _userData;
    private readonly ITaskManager _tasks;
    private readonly ILogger<RecoWorker> _log;
    private readonly ConcurrentDictionary<Guid, DateTime> _dueUsers = new();
    private DateTime? _dueAll;

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

        var delay = TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance!.Configuration.DebounceSeconds, 10, 3600));
        _dueUsers[user] = DateTime.UtcNow + delay;
    }

    /// <summary>Demande un recalcul complet (regroupé).</summary>
    public void ScheduleAll(TimeSpan delay)
    {
        if (!Enabled)
        {
            return;
        }

        var due = DateTime.UtcNow + delay;
        _dueAll = _dueAll is { } d && d < due ? d : due;
    }

    private void OnConfigChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        => ScheduleAll(TimeSpan.FromSeconds(30));

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            ScheduleAll(LibraryDelay);
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
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigChanged;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            if (Enabled)
            {
                ScheduleAll(TimeSpan.Zero);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                if (!Enabled)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                if (_dueAll is { } all && all <= now)
                {
                    _dueAll = null;
                    _dueUsers.Clear();
                    // calcul complet = tâche planifiée « Recalcul des recommandations » (visible, avec sa progression)
                    _log.LogInformation("Reco+ : recalcul de tous les comptes mis en file (Tâches planifiées)");
                    _tasks.QueueIfNotRunning<Tasks.NightlyTask>();
                    continue;
                }

                foreach (var (u, due) in _dueUsers)
                {
                    if (due <= now && _dueUsers.TryRemove(u, out _))
                    {
                        await _reco.ComputeAsync(u, stoppingToken).ConfigureAwait(false);
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
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged -= OnConfigChanged;
            }
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
