using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.RecoPlus.Service;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RecoPlus.Tasks;

/// <summary>
/// Recalcul complet de tous les comptes : après des ajouts à la bibliothèque (au plus une fois par heure), au démarrage
/// si aucun compte n'a de liste, ou à la main (bouton « Recalculer » de la page ou Tâches planifiées). Aucun
/// déclencheur horaire par défaut (chaque compte actif est recalculé après son activité) ; on peut en ajouter un ici.
/// </summary>
public sealed class NightlyTask : IScheduledTask
{
    private readonly RecoService _reco;

    public NightlyTask(RecoService reco) => _reco = reco;

    /// <inheritdoc />
    public string Name => "Recalcul des recommandations";

    /// <inheritdoc />
    public string Key => "RecoPlusNightly";

    /// <inheritdoc />
    public string Description => "Recalcule les recommandations de tous les comptes (bibliothèque, historiques, Seerr, mode étendu).";

    /// <inheritdoc />
    public string Category => "Reco+";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => Plugin.Instance?.Configuration.Enabled == true ? _reco.ComputeAsync(null, cancellationToken, progress) : Task.CompletedTask;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}

/// <summary>Calibrage sur l'historique (manuel uniquement : aucun déclencheur par défaut).</summary>
public sealed class CalibrationTask : IScheduledTask
{
    private readonly CalibrationService _calib;

    public CalibrationTask(CalibrationService calib) => _calib = calib;

    /// <inheritdoc />
    public string Name => "Calibrage sur l'historique";

    /// <inheritdoc />
    public string Key => "RecoPlusCalibration";

    /// <inheritdoc />
    public string Description => "Rejoue l'historique pour proposer un réglage du moteur. N'applique rien : le résultat s'affiche sur la page du plugin.";

    /// <inheritdoc />
    public string Category => "Reco+";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _calib.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
