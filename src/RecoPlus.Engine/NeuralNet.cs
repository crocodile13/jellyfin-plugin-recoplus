using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RecoPlus.Engine;

/// <summary>
/// Calibrage « réseau de neurones » (expérimental) : une correction non linéaire apprise, ajoutée au score du moteur.
/// Petit réseau résiduel 10 → 8 → 1 (tanh) sur les 10 ingrédients du score (ceux du modèle de choix). Il part de zéro
/// (score inchangé) et la régularisation L2 le ramène vers zéro : sans preuve, le moteur reste celui d'aujourd'hui.
/// Entraînement : vraisemblance du titre réellement choisi parmi les candidats (softmax), Adam, déterministe ; force de
/// régularisation choisie par validation croisée par comptes. 96 paramètres : il faut beaucoup d'historique.
/// </summary>
public static class NeuralNet
{
    /// <summary>Neurones cachés.</summary>
    public const int Hidden = 8;

    /// <summary>Nombre de paramètres : poids d'entrée (8 × 10), biais (8), poids de sortie (8).</summary>
    public const int Size = (Hidden * ChoiceModel.K) + Hidden + Hidden;

    /// <summary>Candidats gardés par cas pour l'entraînement (les mieux classés + le titre choisi) : le reste ne pèse rien dans la softmax.</summary>
    public const int TopCandidates = 300;

    /// <summary>Forces de régularisation essayées (× somme des poids des cas).</summary>
    public static IReadOnlyList<double> Kappas { get; } = new[] { 0.001, 0.01, 0.1, 1.0 };

    /// <summary>Correction du score pour un vecteur d'ingrédients x (10 valeurs).</summary>
    public static double Residual(double[] net, ReadOnlySpan<double> x)
    {
        const int K = ChoiceModel.K;
        double r = 0;
        for (int h = 0; h < Hidden; h++)
        {
            double a = net[(Hidden * K) + h];
            int o = h * K;
            for (int k = 0; k < K; k++)
            {
                a += net[o + k] * x[k];
            }

            r += net[(Hidden * K) + Hidden + h] * Math.Tanh(a);
        }

        return r;
    }

    private sealed class Data
    {
        public required double[] X { get; init; }      // candidats retenus × K

        public required double[] Lin { get; init; }    // score linéaire du moteur (sans correction)

        public required int N { get; init; }

        public required int Target { get; init; }

        public required double Weight { get; init; }
    }

    private static List<Data> Prepare(List<ChoiceModel.CaseData> raw, EngineSettings s)
    {
        const int K = ChoiceModel.K;
        var w = ChoiceModel.Weights.Select(x => x.Get(s)).ToArray();
        w[5] = Math.Min(w[5], EngineSettings.FreshCeiling);
        var res = new List<Data>(raw.Count);
        foreach (var c in raw)
        {
            var lin = new double[c.N];
            for (int i = 0; i < c.N; i++)
            {
                double v = 0;
                for (int k = 0; k < K; k++)
                {
                    v += w[k] * c.X[(i * K) + k];
                }

                lin[i] = v;
            }

            var keep = Enumerable.Range(0, c.N).OrderByDescending(i => lin[i]).ThenBy(i => i).Take(TopCandidates).ToList();
            if (!keep.Contains(c.Target))
            {
                keep.Add(c.Target);
            }

            var x = new double[keep.Count * K];
            var l = new double[keep.Count];
            for (int j = 0; j < keep.Count; j++)
            {
                Array.Copy(c.X, keep[j] * K, x, j * K, K);
                l[j] = lin[keep[j]];
            }

            res.Add(new Data { X = x, Lin = l, N = keep.Count, Target = keep.IndexOf(c.Target), Weight = c.Weight });
        }

        return res;
    }

    /// <summary>Objectif et gradient : Σ wᵢ (logΣ e^{τ(lin + r)} − τ(lin + r)_cible) + λ ‖θ‖².</summary>
    private static double Eval(List<Data> data, double[] th, double tau, double lambda, double[] g)
    {
        const int K = ChoiceModel.K;
        Array.Clear(g);
        double f = 0;
        var act = new double[Hidden];
        var pre = new double[Hidden];
        foreach (var c in data)
        {
            var sc = new double[c.N];
            var tanh = new double[c.N * Hidden];
            double mx = double.NegativeInfinity;
            for (int i = 0; i < c.N; i++)
            {
                double r = 0;
                for (int h = 0; h < Hidden; h++)
                {
                    double a = th[(Hidden * K) + h];
                    for (int k = 0; k < K; k++)
                    {
                        a += th[(h * K) + k] * c.X[(i * K) + k];
                    }

                    double t = Math.Tanh(a);
                    tanh[(i * Hidden) + h] = t;
                    r += th[(Hidden * K) + Hidden + h] * t;
                }

                sc[i] = tau * (c.Lin[i] + r);
                mx = Math.Max(mx, sc[i]);
            }

            double z = 0;
            for (int i = 0; i < c.N; i++)
            {
                z += Math.Exp(sc[i] - mx);
            }

            f += c.Weight * (mx + Math.Log(z) - sc[c.Target]);
            for (int i = 0; i < c.N; i++)
            {
                double coef = c.Weight * tau * ((Math.Exp(sc[i] - mx) / z) - (i == c.Target ? 1.0 : 0.0));
                if (Math.Abs(coef) < 1e-15)
                {
                    continue;
                }

                for (int h = 0; h < Hidden; h++)
                {
                    double t = tanh[(i * Hidden) + h];
                    double v = th[(Hidden * K) + Hidden + h];
                    g[(Hidden * K) + Hidden + h] += coef * t;
                    double d = coef * v * (1 - (t * t));
                    g[(Hidden * K) + h] += d;
                    for (int k = 0; k < K; k++)
                    {
                        g[(h * K) + k] += d * c.X[(i * K) + k];
                    }
                }
            }
        }

        for (int j = 0; j < th.Length; j++)
        {
            f += lambda * th[j] * th[j];
            g[j] += 2 * lambda * th[j];
        }

        return f;
    }

    /// <summary>Entraîne la correction sur des cas (réglages du moteur fixés), à force de régularisation donnée.</summary>
    public static double[] Train(Dataset ds, SimilarityIndex sim, IReadOnlyList<Calibrator.Case> cases, EngineSettings s, double kappa,
        CancellationToken ct = default, int iterations = 300, ChoiceModel.FeatureCache? cache = null)
    {
        s = s.Sanitized();
        var raw = ChoiceModel.Build(ds, sim, cases, s, ct, cache);
        var data = Prepare(raw, s);
        var th = new double[Size];
        if (data.Count == 0)
        {
            return th;
        }

        // température : celle qui rend le moteur actuel le plus vraisemblable (le réseau corrige, il ne change pas d'échelle)
        var w = ChoiceModel.Weights.Select(x => x.Get(s)).ToArray();
        w[5] = Math.Min(w[5], EngineSettings.FreshCeiling);
        double tau = Math.Exp(ChoiceModel.NewtonTau(raw, w, 0));
        double lambda = kappa * Math.Max(1e-9, data.Sum(c => c.Weight));

        // départ : poids d'entrée petits et déterministes, sortie nulle (correction nulle)
        var rng = new Random(20261020);
        for (int j = 0; j < Hidden * ChoiceModel.K; j++)
        {
            th[j] = (rng.NextDouble() - 0.5) * 0.6;
        }

        // Adam, lot complet
        const double Lr = 0.02, B1 = 0.9, B2 = 0.999, Eps = 1e-8;
        var m = new double[Size];
        var v = new double[Size];
        var g = new double[Size];
        double best = double.PositiveInfinity;
        var bestTh = (double[])th.Clone();
        for (int it = 1; it <= iterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            double f = Eval(data, th, tau, lambda, g);
            if (f < best)
            {
                best = f;
                Array.Copy(th, bestTh, Size);
            }

            for (int j = 0; j < Size; j++)
            {
                m[j] = (B1 * m[j]) + ((1 - B1) * g[j]);
                v[j] = (B2 * v[j]) + ((1 - B2) * g[j] * g[j]);
                double mh = m[j] / (1 - Math.Pow(B1, it)), vh = v[j] / (1 - Math.Pow(B2, it));
                th[j] -= Lr * mh / (Math.Sqrt(vh) + Eps);
                th[j] = Math.Clamp(th[j], -50, 50);
            }
        }

        if (Eval(data, th, tau, lambda, g) < best)
        {
            bestTh = th;
        }

        return bestTh.Select(x => Math.Round(x, 6)).ToArray();
    }

    /// <summary>Vrai si la correction est nulle (poids de sortie tous nuls) : le réseau n'a rien appris.</summary>
    public static bool IsZero(double[]? net) => net is null || net.Length != Size || net.Skip((Hidden * ChoiceModel.K) + Hidden).All(v => Math.Abs(v) < 1e-4);

    /// <summary>Réglages avec la correction apprise (aucune si elle est nulle).</summary>
    public static EngineSettings With(EngineSettings s, double[] net)
    {
        var r = s.Clone();
        r.Neural = IsZero(net) ? null : net;
        r.WNeural = 1.0;
        return r.Sanitized();
    }

    /// <summary>Force de régularisation par validation croisée par comptes, k groupes (à égalité, la plus forte).</summary>
    public static double SelectKappa(Dataset ds, SimilarityIndex sim, IReadOnlyList<Calibrator.Case> cases, EngineSettings s,
        Action? tick = null, CancellationToken ct = default, ChoiceModel.FeatureCache? cache = null, int folds = 0)
        => Calibrator.SelectByCv(cases, Kappas, (train, test, kappa) =>
        {
            var net = Train(ds, sim, train, s, kappa, ct, cache: cache);
            tick?.Invoke();
            return Calibrator.EvaluateRanks(ds, sim, test, With(s, net), ct).Gain;
        }, folds);
}
