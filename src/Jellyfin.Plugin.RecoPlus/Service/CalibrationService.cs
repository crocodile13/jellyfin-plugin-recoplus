using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.RecoPlus.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Service;

/// <summary>
/// Calibrage manuel. Il s'exécute comme tâche planifiée (« Reco+ : calibrage sur l'historique », visible avec sa
/// progression dans Tableau de bord → Tâches planifiées) ; rien n'est appliqué sans clic.
/// </summary>
public sealed class CalibrationService
{
    private readonly RecoService _reco;
    private readonly ITaskManager _tasks;
    private readonly ILogger<CalibrationService> _log;

    public CalibrationService(RecoService reco, ITaskManager tasks, ILogger<CalibrationService> log)
    {
        _reco = reco;
        _tasks = tasks;
        _log = log;
    }

    private IScheduledTaskWorker? Worker => _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is CalibrationTask);

    /// <summary>En cours (d'après le gestionnaire de tâches).</summary>
    public bool Running => Worker?.State is TaskState.Running or TaskState.Cancelling;

    /// <summary>Avancement 0-100.</summary>
    public double Progress => Math.Round(Worker?.CurrentProgress ?? 0, 1);

    /// <summary>Dernier rapport.</summary>
    public CalibrationReport? Report { get; private set; }

    /// <summary>Date du dernier rapport.</summary>
    public DateTime? ReportAt { get; private set; }

    /// <summary>Erreur éventuelle.</summary>
    public string? Error { get; private set; }

    /// <summary>Met la tâche en file (sans effet si déjà en cours).</summary>
    public void Start() => _tasks.QueueIfNotRunning<CalibrationTask>();

    /// <summary>Interrompt.</summary>
    public void Cancel() => _tasks.CancelIfRunning<CalibrationTask>();

    private (int N, DateTime At) _cases = (-1, DateTime.MinValue);

    /// <summary>Nombre de cas d'évaluation disponibles (relu au plus toutes les 10 minutes).</summary>
    public int CaseCount()
    {
        if (_cases.N < 0 || DateTime.UtcNow - _cases.At > TimeSpan.FromMinutes(10))
        {
            _cases = (Calibrator.Cases(_reco.BuildCalibrationDataset()).Count, DateTime.UtcNow);
        }

        return _cases.N;
    }

    /// <summary>Corps de la tâche.</summary>
    public void Run(IProgress<double> progress, CancellationToken ct)
    {
        Error = null;
        try
        {
            progress.Report(1);
            var ds = _reco.BuildCalibrationDataset();
            var cur = Plugin.Instance!.Configuration.Engine;
            Cpu.Threads = Plugin.Instance.Configuration.Threads;
            Report = Calibrator.Calibrate(ds, cur, new Progress<double>(p => progress.Report(Math.Max(1, p))), ct,
                method: Plugin.Instance!.Configuration.CalibrationMethod);
            ReportAt = DateTime.UtcNow;
            _log.LogInformation("Reco+ : calibrage terminé — {Verdict}", Report.Verdict);
        }
        catch (OperationCanceledException)
        {
            Error = "interrompu";
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ": " + ex.Message;
            _log.LogError(ex, "Reco+ : calibrage en échec");
            throw;
        }
    }
}
