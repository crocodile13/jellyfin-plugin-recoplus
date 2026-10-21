using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RecoPlus.Engine;

/// <summary>Méthode de réglage automatique.</summary>
public enum CalibrationMethod
{
    /// <summary>Essaie toutes les méthodes et garde celle qui se généralise le mieux (validation croisée par comptes).</summary>
    Auto,

    /// <summary>Recherche par curseurs : chaque réglage tour à tour (grille puis section dorée). Tout est réglable, mais il faut beaucoup d'historique.</summary>
    Coordinate,

    /// <summary>
    /// Modèle de choix appris : les poids du score sont appris d'un coup (logit conditionnel, régularisé vers les
    /// réglages actuels), comme on apprend « quel titre a été choisi parmi tous ceux proposables ». Peu de paramètres,
    /// donc utilisable avec moins d'historique.
    /// </summary>
    ChoiceModel,

    /// <summary>
    /// Réseau de neurones (expérimental) : une petite correction non linéaire (10 → 8 → 1) apprise sur les ingrédients
    /// du score et ajoutée au moteur, ramenée vers zéro sans preuve. 96 paramètres : il faut beaucoup d'historique.
    /// </summary>
    Neural
}

/// <summary>
/// Modèle de choix (logit conditionnel / softmax sur les candidats) : à chaque cas, le compte a choisi un titre parmi
/// tous ceux proposables ; on cherche les poids du score qui rendent ce choix le plus probable. Régularisation L2 vers
/// les poids actuels (un a priori : sans preuve, on ne bouge pas), force choisie par validation croisée par comptes.
/// Le garde-fou des bonus dépend du classement par goût : il est figé à chaque itération externe, puis recalculé.
/// </summary>
public static class ChoiceModel
{
    /// <summary>Un poids appris : nom affiché, lecture, écriture, borne haute.</summary>
    public sealed record Weight(string Name, Func<EngineSettings, double> Get, Action<EngineSettings, double> Set, double Hi);

    /// <summary>Poids appris (5 de goût, puis 5 bonus réservés).</summary>
    public static IReadOnlyList<Weight> Weights { get; } = new List<Weight>
    {
        new("Goûts d'ensemble", s => s.WCentroid, (s, v) => s.WCentroid = v, 10),
        new("Titre aimé le plus proche", s => s.WMaxSim, (s, v) => s.WMaxSim = v, 10),
        new("Vus ensemble", s => s.WCollab, (s, v) => s.WCollab = v, 10),
        new("Écart aux abandons", s => s.WNegative, (s, v) => s.WNegative = v, 10),
        new("Goût du moment", s => s.WShortTerm, (s, v) => s.WShortTerm = v, 10),
        new("Nouveauté", s => s.WFresh, (s, v) => s.WFresh = v, EngineSettings.FreshCeiling),
        new("Popularité", s => s.WPopularity, (s, v) => s.WPopularity = v, 10),
        new("Note", s => s.WQuality, (s, v) => s.WQuality = v, 10),
        new("Sortie récente", s => s.WRelease, (s, v) => s.WRelease = v, 10),
        new("Avis du foyer", s => s.WHousehold, (s, v) => s.WHousehold = v, 10),
    };

    /// <summary>Forces de régularisation essayées (× somme des poids des cas).</summary>
    public static IReadOnlyList<double> Kappas { get; } = new[] { 0.003, 0.01, 0.03, 0.1, 0.3 };

    internal const int K = 10;

    internal sealed class CaseData
    {
        public required double[] X { get; init; }   // candidats × K, à plat

        public required int N { get; init; }

        public required int Target { get; init; }

        public required double Weight { get; init; }
    }

    /// <summary>
    /// Caractéristiques déjà calculées pour UN réglage (celui de départ d'un calibrage), par cas : le choix de la
    /// régularisation et la validation croisée réapprennent sur des sous-ensembles de cas, toujours depuis ce réglage.
    /// </summary>
    public sealed class FeatureCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<EvalEvent, CaseData?> _d = new();

        public FeatureCache(EngineSettings s) => Key = System.Text.Json.JsonSerializer.Serialize(s.Sanitized());

        internal string Key { get; }

        internal bool For(EngineSettings s) => System.Text.Json.JsonSerializer.Serialize(s) == Key;

        internal bool TryGet(EvalEvent e, out CaseData? d) => _d.TryGetValue(e, out d);

        internal void Put(EvalEvent e, CaseData? d) => _d[e] = d;
    }

    /// <summary>Caractéristiques de chaque candidat de chaque cas, garde-fou figé selon les réglages <paramref name="s"/>.</summary>
    internal static List<CaseData> Build(Dataset ds, SimilarityIndex sim, IReadOnlyList<Calibrator.Case> cases, EngineSettings s, CancellationToken ct,
        FeatureCache? cache = null)
    {
        var res = new CaseData?[cases.Count];
        var use = cache is not null && cache.For(s) ? cache : null;
        var po = new ParallelOptions { MaxDegreeOfParallelism = Cpu.Degree, CancellationToken = ct };
        Parallel.For(0, cases.Count, po, i =>
        {
            if (use is not null && use.TryGet(cases[i].Ev, out var known))
            {
                res[i] = known is null ? null : new CaseData { X = known.X, N = known.N, Target = known.Target, Weight = cases[i].Weight };
                return;
            }

            var (u, target, t0) = cases[i].Ev;
            var hist = Calibrator.Snapshot(ds, t0 - 1);
            var ctx = new Context(ds, t0 - 1, hist, s);
            var seen = hist[u];
            var cands = Calibrator.Candidates(ds, seen, t0);
            int ti = cands.IndexOf(target);
            if (ti < 0)
            {
                use?.Put(cases[i].Ev, null);
                return;
            }

            var c = Recommender.Compute(ctx, sim.Vectors, u, cands, s, null, sim);
            var taste = new double[cands.Count];
            for (int k = 0; k < taste.Length; k++)
            {
                taste[k] = Recommender.Taste(c, k, s);
            }

            var g = Recommender.Gate(taste, s.GateTop);
            var x = new double[cands.Count * K];
            for (int k = 0; k < cands.Count; k++)
            {
                double b = Math.Max(taste[k], 0.0) * g[k];
                int o = k * K;
                x[o] = c.Cen[k];
                x[o + 1] = c.Ms[k];
                x[o + 2] = c.Col[k];
                x[o + 3] = -c.Neg[k];
                x[o + 4] = c.St[k];
                x[o + 5] = b * c.Fr[k];
                x[o + 6] = b * c.Pop[k];
                x[o + 7] = b * c.Q[k];
                x[o + 8] = b * c.Re[k];
                x[o + 9] = b * c.Hh[k];
            }

            res[i] = new CaseData { X = x, N = cands.Count, Target = ti, Weight = cases[i].Weight };
            use?.Put(cases[i].Ev, res[i]);
        });
        return res.OfType<CaseData>().ToList();
    }

    /// <summary>
    /// Ajuste W (≥ 0, bornés) et la température τ : min Σ wᵢ (logΣ e^{τ s} − τ s_cible) + λ Σ ((W − d)/échelle)².
    /// Alternance : Newton projeté sur W à τ fixé (problème convexe, K petit : hessienne exacte), puis Newton sur log τ.
    /// </summary>
    private static double[] Fit(List<CaseData> data, double[] d, double kappa, CancellationToken ct)
    {
        var hi = Weights.Select(w => w.Hi).ToArray();
        var scale = d.Select(v => Math.Max(v, 0.25)).ToArray();
        double lambda = kappa * Math.Max(1e-9, data.Sum(c => c.Weight));
        var w = d.Select((v, k) => Math.Clamp(v, 0, hi[k])).ToArray();
        double u = 0; // log τ
        for (int round = 0; round < 8; round++)
        {
            ct.ThrowIfCancellationRequested();
            var w0 = (double[])w.Clone();
            double u0 = u;
            w = NewtonW(data, w, Math.Exp(u), d, scale, hi, lambda);
            u = NewtonTau(data, w, u);
            if (Math.Abs(u - u0) < 1e-4 && w.Zip(w0, (x, y) => Math.Abs(x - y)).Max() < 1e-4)
            {
                break;
            }
        }

        return w;
    }

    /// <summary>Objectif, et au besoin gradient et hessienne en W, à τ fixé.</summary>
    private static double Eval(List<CaseData> data, double[] w, double tau, double[] d, double[] scale, double lambda, double[]? g, double[,]? h)
    {
        double f = 0;
        if (g is not null)
        {
            Array.Clear(g);
        }

        if (h is not null)
        {
            Array.Clear(h);
        }

        var ex = new double[K];
        var exx = new double[K, K];
        foreach (var c in data)
        {
            var sc = new double[c.N];
            double mx = double.NegativeInfinity;
            for (int i = 0; i < c.N; i++)
            {
                double s = 0;
                int o = i * K;
                for (int k = 0; k < K; k++)
                {
                    s += w[k] * c.X[o + k];
                }

                sc[i] = tau * s;
                mx = Math.Max(mx, sc[i]);
            }

            double z = 0;
            if (g is not null)
            {
                Array.Clear(ex);
                Array.Clear(exx);
            }

            for (int i = 0; i < c.N; i++)
            {
                double p = Math.Exp(sc[i] - mx);
                z += p;
                if (g is null || p < 1e-300)
                {
                    continue;
                }

                int o = i * K;
                for (int k = 0; k < K; k++)
                {
                    double xk = c.X[o + k];
                    ex[k] += p * xk;
                    if (h is not null)
                    {
                        for (int l = 0; l <= k; l++)
                        {
                            exx[k, l] += p * xk * c.X[o + l];
                        }
                    }
                }
            }

            f += c.Weight * (mx + Math.Log(z) - sc[c.Target]);
            if (g is not null)
            {
                int ot = c.Target * K;
                for (int k = 0; k < K; k++)
                {
                    g[k] += c.Weight * tau * ((ex[k] / z) - c.X[ot + k]);
                    if (h is not null)
                    {
                        for (int l = 0; l <= k; l++)
                        {
                            double cov = (exx[k, l] / z) - (ex[k] / z * (ex[l] / z));
                            h[k, l] += c.Weight * tau * tau * cov;
                        }
                    }
                }
            }
        }

        for (int k = 0; k < K; k++)
        {
            double r = (w[k] - d[k]) / scale[k];
            f += lambda * r * r;
            if (g is not null)
            {
                g[k] += 2 * lambda * r / scale[k];
            }

            if (h is not null)
            {
                h[k, k] += 2 * lambda / (scale[k] * scale[k]);
                for (int l = 0; l < k; l++)
                {
                    h[l, k] = h[k, l];
                }
            }
        }

        return f;
    }

    private static double[] NewtonW(List<CaseData> data, double[] w, double tau, double[] d, double[] scale, double[] hi, double lambda)
    {
        var g = new double[K];
        var h = new double[K, K];
        double f = Eval(data, w, tau, d, scale, lambda, g, h);
        for (int it = 0; it < 30; it++)
        {
            // variables libres : pas collées à une borne vers laquelle le gradient pousse
            var free = Enumerable.Range(0, K).Where(k => !((w[k] <= 0 && g[k] > 0) || (w[k] >= hi[k] && g[k] < 0))).ToArray();
            if (free.Length == 0)
            {
                break;
            }

            var dir = Solve(free.Select(a => free.Select(b => h[a, b]).ToArray()).ToArray(), free.Select(k => -g[k]).ToArray());
            double step = 1;
            bool moved = false;
            for (int bt = 0; bt < 30; bt++)
            {
                var nw = (double[])w.Clone();
                for (int j = 0; j < free.Length; j++)
                {
                    nw[free[j]] = Math.Clamp(w[free[j]] + (step * dir[j]), 0, hi[free[j]]);
                }

                double dec = 0;
                for (int k = 0; k < K; k++)
                {
                    dec += g[k] * (w[k] - nw[k]);
                }

                double fn = Eval(data, nw, tau, d, scale, lambda, null, null);
                if (fn <= f - (1e-4 * Math.Max(dec, 0)) && fn < f)
                {
                    bool small = f - fn < 1e-10 * Math.Max(1, Math.Abs(f));
                    w = nw;
                    f = Eval(data, w, tau, d, scale, lambda, g, h);
                    moved = !small;
                    break;
                }

                step /= 2;
            }

            if (!moved)
            {
                break;
            }
        }

        return w;
    }

    internal static double NewtonTau(List<CaseData> data, double[] w, double u)
    {
        // a = X·W (sans τ) ; f(u) = Σ wᵢ (LSE(e^u a) − e^u a_cible)
        var a = data.Select(c =>
        {
            var r = new double[c.N];
            for (int i = 0; i < c.N; i++)
            {
                double s = 0;
                for (int k = 0; k < K; k++)
                {
                    s += w[k] * c.X[(i * K) + k];
                }

                r[i] = s;
            }

            return r;
        }).ToArray();

        (double F, double G, double H) At(double uu)
        {
            double tau = Math.Exp(uu), f = 0, g = 0, h = 0;
            for (int ci = 0; ci < data.Count; ci++)
            {
                var c = data[ci];
                var ai = a[ci];
                double mx = ai.Max() * tau, z = 0, m1 = 0, m2 = 0;
                foreach (var v in ai)
                {
                    double p = Math.Exp((tau * v) - mx);
                    z += p;
                    m1 += p * v;
                    m2 += p * v * v;
                }

                m1 /= z;
                m2 /= z;
                double at = ai[c.Target];
                f += c.Weight * (mx + Math.Log(z) - (tau * at));
                g += c.Weight * tau * (m1 - at);
                h += c.Weight * ((tau * (m1 - at)) + (tau * tau * (m2 - (m1 * m1))));
            }

            return (f, g, h);
        }

        var cur = At(u);
        for (int it = 0; it < 30; it++)
        {
            double stepU = cur.H > 1e-12 ? -cur.G / cur.H : -Math.Sign(cur.G) * 0.5;
            stepU = Math.Clamp(stepU, -2, 2);
            bool moved = false;
            for (int bt = 0; bt < 20; bt++)
            {
                double nu = Math.Clamp(u + stepU, -6, 6);
                var nx = At(nu);
                if (nx.F < cur.F)
                {
                    moved = cur.F - nx.F > 1e-10 * Math.Max(1, Math.Abs(cur.F));
                    (u, cur) = (nu, nx);
                    break;
                }

                stepU /= 2;
            }

            if (!moved)
            {
                break;
            }
        }

        return u;
    }

    /// <summary>Système linéaire symétrique (petit) : élimination de Gauss avec pivot partiel.</summary>
    private static double[] Solve(double[][] m, double[] b)
    {
        int n = b.Length;
        var a = m.Select(r => (double[])r.Clone()).ToArray();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
            {
                if (Math.Abs(a[r][c]) > Math.Abs(a[p][c]))
                {
                    p = r;
                }
            }

            (a[c], a[p]) = (a[p], a[c]);
            (x[c], x[p]) = (x[p], x[c]);
            if (Math.Abs(a[c][c]) < 1e-14)
            {
                a[c][c] = 1e-14;
            }

            for (int r = c + 1; r < n; r++)
            {
                double f = a[r][c] / a[c][c];
                for (int k = c; k < n; k++)
                {
                    a[r][k] -= f * a[c][k];
                }

                x[r] -= f * x[c];
            }
        }

        for (int c = n - 1; c >= 0; c--)
        {
            for (int k = c + 1; k < n; k++)
            {
                x[c] -= a[c][k] * x[k];
            }

            x[c] /= a[c][c];
        }

        return x;
    }

    private static EngineSettings Apply(EngineSettings s, double[] w)
    {
        var r = s.Clone();
        for (int k = 0; k < K; k++)
        {
            // un écart minime (moins de 0,02 ou de 3 %) n'est que du bruit : la valeur de départ est gardée
            double cur = Weights[k].Get(s);
            Weights[k].Set(r, Math.Abs(w[k] - cur) < Math.Max(0.02, 0.03 * cur) ? cur : Math.Round(w[k], 3));
        }

        return r.Sanitized();
    }

    /// <summary>Apprend les poids du score sur des cas, à force de régularisation donnée (itérations externes : garde-fou recalculé).</summary>
    public static EngineSettings Learn(Dataset ds, SimilarityIndex sim, IReadOnlyList<Calibrator.Case> cases, EngineSettings start, double kappa,
        CancellationToken ct = default, int outer = 3, FeatureCache? cache = null)
    {
        start = start.Sanitized();
        var d = Weights.Select(w => w.Get(start)).ToArray();
        var s = start;
        for (int o = 0; o < outer; o++)
        {
            var data = Build(ds, sim, cases, s, ct, cache);
            if (data.Count == 0)
            {
                return start;
            }

            var next = Apply(start, Fit(data, d, kappa, ct));
            bool same = Weights.All(w => Math.Abs(w.Get(next) - w.Get(s)) < 1e-3);
            s = next;
            if (same)
            {
                break;
            }
        }

        return s;
    }

    /// <summary>
    /// Force de régularisation par validation croisée sur les comptes des cas donnés (k groupes de comptes, voir
    /// <see cref="Calibrator.FoldCount"/>) : celle dont les poids appris sans un groupe classent le mieux ce groupe (mesure
    /// du calibrage : gain de rang). À égalité, la plus forte.
    /// </summary>
    public static double SelectKappa(Dataset ds, SimilarityIndex sim, IReadOnlyList<Calibrator.Case> cases, EngineSettings start,
        Action? tick = null, CancellationToken ct = default, FeatureCache? cache = null, int folds = 0)
        => Calibrator.SelectByCv(cases, Kappas, (train, test, kappa) =>
        {
            var learned = Learn(ds, sim, train, start, kappa, ct, cache: cache);
            tick?.Invoke();
            return Calibrator.EvaluateRanks(ds, sim, test, learned, ct).Gain;
        }, folds);

    /// <summary>Réglages entre <paramref name="from"/> et <paramref name="to"/> : chaque poids appris pris à <paramref name="alpha"/> du chemin.</summary>
    public static EngineSettings Toward(EngineSettings from, EngineSettings to, double alpha)
    {
        var r = from.Clone();
        foreach (var w in Weights)
        {
            w.Set(r, Math.Round(w.Get(from) + (alpha * (w.Get(to) - w.Get(from))), 3));
        }

        return r.Sanitized();
    }
}
