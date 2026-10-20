using System;

namespace RecoPlus.Engine;

/// <summary>Nombre de fils d'exécution des calculs parallèles (calcul des recommandations, calibrage).</summary>
public static class Cpu
{
    private static int _threads;

    /// <summary>Fils demandés ; 0 = automatique (la moitié des cœurs : Jellyfin garde de quoi servir les lectures).</summary>
    public static int Threads
    {
        get => _threads;
        set => _threads = Math.Clamp(value, 0, 256);
    }

    /// <summary>Fils effectivement utilisés.</summary>
    public static int Degree => _threads > 0 ? Math.Min(_threads, Math.Max(1, Environment.ProcessorCount)) : Auto;

    /// <summary>Valeur automatique sur cette machine.</summary>
    public static int Auto => Math.Max(1, Environment.ProcessorCount / 2);
}
