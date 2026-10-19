using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RecoPlus.Engine;

/// <summary>Un cas d'évaluation : un compte a adopté un nouveau titre à l'instant T0.</summary>
public readonly record struct EvalEvent(string User, string Target, double T0);

/// <summary>Résultat d'une évaluation.</summary>
public sealed class EvalResult
{
    /// <summary>Nombre de cas.</summary>
    public int Events { get; init; }

    /// <summary>Comptes concernés.</summary>
    public int Accounts { get; init; }

    /// <summary>Part des cas où le titre adopté figurait dans les 10 premiers.</summary>
    public double Hit10 { get; init; }

    /// <summary>Idem, 20 premiers.</summary>
    public double Hit20 { get; init; }

    /// <summary>Succès par cas (top 10), dans l'ordre des événements.</summary>
    public required bool[] PerEvent10 { get; init; }

    /// <summary>Succès par cas (top 20).</summary>
    public required bool[] PerEvent20 { get; init; }
}

/// <summary>Une étape du calibrage, pour l'affichage.</summary>
public sealed record CalibrationStep(string Parameter, string Value, double Hit10, double Hit20, bool Kept);

/// <summary>Résultat du calibrage.</summary>
public sealed class CalibrationReport
{
    /// <summary>Score du réglage actuel.</summary>
    public required EvalResult Current { get; init; }

    /// <summary>Score du réglage proposé.</summary>
    public required EvalResult Proposed { get; init; }

    /// <summary>Réglage proposé.</summary>
    public required EngineSettings ProposedSettings { get; init; }

    /// <summary>Intervalle de confiance à 95 % de l'écart top 10 (proposé − actuel), bootstrap par compte.</summary>
    public (double Low, double High) DeltaCi { get; init; }

    /// <summary>Étapes essayées.</summary>
    public required List<CalibrationStep> Steps { get; init; }

    /// <summary>Vrai si le gain est assez sûr pour être recommandé.</summary>
    public bool Recommended { get; init; }

    /// <summary>Explication en français.</summary>
    public required string Verdict { get; init; }
}

/// <summary>
/// Évaluation temporelle et calibrage manuel : chaque fois qu'un compte a commencé un nouveau titre,
/// le titre figurait-il dans les recommandations calculées juste avant, avec seulement ce qui était connu alors ?
/// </summary>
public static class Calibrator
{
    /// <summary>Titres adoptés avant le premier cas évalué.</summary>
    public const int MinTrain = 4;

    /// <summary>En dessous de ce nombre de cas, aucun réglage n'est proposé.</summary>
    public const int MinEvents = 50;

    private static bool Adopted(Interaction x, TitleKind k) => k == TitleKind.Movie ? x.Played : x.Episodes >= 3;

    /// <summary>Cas d'évaluation (ordre stable : comptes puis dates).</summary>
    public static List<EvalEvent> Events(Dataset ds)
    {
        var ev = new List<EvalEvent>();
        foreach (var (u, h) in ds.Users)
        {
            var ad = h.Values.Where(x => ds.Titles.TryGetValue(x.Title, out var t) && !t.External && Adopted(x, t.Kind))
                .OrderBy(x => x.Start).ToList();
            for (int i = 0; i < ad.Count; i++)
            {
                if (i >= MinTrain && ad[i].Start != 0)
                {
                    ev.Add(new EvalEvent(u, ad[i].Title, ad[i].Start));
                }
            }
        }

        return ev;
    }

    /// <summary>Historique connu juste avant `now`.</summary>
    public static Dictionary<string, Dictionary<string, Interaction>> Snapshot(Dataset ds, double now)
        => ds.Users.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(e => e.Value.Start != 0 && e.Value.Start < now).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    /// <summary>Évalue un réglage.</summary>
    public static EvalResult Evaluate(Dataset ds, IReadOnlyDictionary<string, SparseVector> vec, IReadOnlyList<EvalEvent> ev,
        EngineSettings s, CancellationToken ct = default)
    {
        var h10 = new bool[ev.Count];
        var h20 = new bool[ev.Count];
        for (int i = 0; i < ev.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (u, target, t0) = ev[i];
            var hist = Snapshot(ds, t0 - 1);
            var ctx = new Context(ds, t0 - 1, hist, s);
            var seen = hist[u];
            var cands = ds.Titles.Values.Where(t => !t.External && !seen.ContainsKey(t.Id) && (t.Added ?? 0) <= t0).Select(t => t.Id).ToList();
            var sc = Recommender.Score(ctx, vec, u, cands, s);
            var list = Recommender.Rank(ctx, vec, cands, sc, 20, s);
            int r = list.IndexOf(target);
            h10[i] = r >= 0 && r < 10;
            h20[i] = r >= 0 && r < 20;
        }

        int n = Math.Max(ev.Count, 1);
        return new EvalResult
        {
            Events = ev.Count,
            Accounts = ev.Select(e => e.User).Distinct().Count(),
            Hit10 = h10.Count(b => b) / (double)n,
            Hit20 = h20.Count(b => b) / (double)n,
            PerEvent10 = h10,
            PerEvent20 = h20,
        };
    }

    /// <summary>Intervalle de confiance à 95 % de l'écart (a − b) sur le top 10, bootstrap en tirant des comptes.</summary>
    public static (double Low, double High) BootstrapCi(IReadOnlyList<EvalEvent> ev, bool[] a, bool[] b, int rounds = 2000, int seed = 0)
    {
        var users = ev.Select(e => e.User).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (users.Count == 0)
        {
            return (0, 0);
        }

        var by = users.ToDictionary(u => u, u => Enumerable.Range(0, ev.Count).Where(i => ev[i].User == u).ToArray(), StringComparer.Ordinal);
        var rng = new Random(seed);
        var d = new double[rounds];
        for (int r = 0; r < rounds; r++)
        {
            int cnt = 0;
            int diff = 0;
            for (int k = 0; k < users.Count; k++)
            {
                foreach (var i in by[users[rng.Next(users.Count)]])
                {
                    diff += (a[i] ? 1 : 0) - (b[i] ? 1 : 0);
                    cnt++;
                }
            }

            d[r] = cnt == 0 ? 0 : diff / (double)cnt;
        }

        Array.Sort(d);
        return (d[(int)(rounds * 0.025)], d[(int)(rounds * 0.975)]);
    }

    /// <summary>Paramètres explorés (domaines bornés ; le garde-fou n'est pas réglable, la nouveauté est plafonnée).</summary>
    public static IReadOnlyList<(string Name, object[] Values, Action<EngineSettings, object> Set)> Space { get; } = new List<(string, object[], Action<EngineSettings, object>)>
    {
        ("Demi-vie (jours)", new object[] { 60.0, 90.0, 180.0, 365.0 }, (s, v) => s.HalfLifeDays = (double)v),
        ("Décroissance", new object[] { DecayKind.Exponential, DecayKind.Power }, (s, v) => s.Decay = (DecayKind)v),
        ("Titre aimé le plus proche", new object[] { 1.0, 2.0, 3.0 }, (s, v) => s.WMaxSim = (double)v),
        ("Profil moyen", new object[] { 0.5, 1.0, 2.0 }, (s, v) => s.WCentroid = (double)v),
        ("Vus ensemble", new object[] { 0.0, 0.3, 0.6, 1.0 }, (s, v) => s.WCollab = (double)v),
        ("Rétrécissement", new object[] { 0.0, 10.0, 30.0 }, (s, v) => s.Shrink = (double)v),
        ("Nouveauté", new object[] { 0.0, 1.0, 2.0, 4.0 }, (s, v) => s.WFresh = (double)v),
        ("Popularité", new object[] { 0.0, 0.5, 1.0 }, (s, v) => s.WPopularity = (double)v),
        ("Bonus réservés aux N premiers", new object[] { 30, 60, 100 }, (s, v) => s.GateTop = (int)v),
        ("Diversité", new object[] { 0.15, 0.3, 0.5 }, (s, v) => s.Mmr = (double)v),
        ("Pondération du contenu", new object[] { WeightingScheme.TfIdf, WeightingScheme.Bm25 }, (s, v) => s.Weighting = (WeightingScheme)v),
    };

    /// <summary>
    /// Descente par coordonnées : un paramètre à la fois, une valeur n'est gardée que si elle améliore l'ensemble
    /// sans dégrader aucune des deux moitiés (événements pairs / impairs). Rien n'est appliqué ici.
    /// </summary>
    public static CalibrationReport Calibrate(Dataset ds, EngineSettings current, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        current = current.Sanitized();
        var ev = Events(ds);
        var steps = new List<CalibrationStep>();
        var vecCache = new Dictionary<WeightingScheme, Dictionary<string, SparseVector>>();
        Dictionary<string, SparseVector> Vec(EngineSettings s)
        {
            if (!vecCache.TryGetValue(s.Weighting, out var v))
            {
                vecCache[s.Weighting] = v = Core.BuildVectors(ds.Titles, s);
            }

            return v;
        }

        var cur = Evaluate(ds, Vec(current), ev, current, ct);
        if (ev.Count < MinEvents)
        {
            return new CalibrationReport
            {
                Current = cur,
                Proposed = cur,
                ProposedSettings = current,
                Steps = steps,
                Recommended = false,
                Verdict = $"Seulement {ev.Count} cas exploitables (minimum {MinEvents}) : pas assez d'historique pour proposer un réglage fiable.",
            };
        }

        var even = Enumerable.Range(0, ev.Count).Where(i => i % 2 == 0).ToArray();
        var odd = Enumerable.Range(0, ev.Count).Where(i => i % 2 == 1).ToArray();
        double Fold(EvalResult r, int[] idx) => idx.Average(i => (r.PerEvent10[i] ? 1.0 : 0.0) + (r.PerEvent20[i] ? 1.0 : 0.0));
        double Tot(EvalResult r) => r.Hit10 + r.Hit20;

        var best = current.Clone();
        var bestRes = cur;
        int total = Space.Sum(p => p.Values.Length) * 2;
        int done = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool changed = false;
            foreach (var (name, values, set) in Space)
            {
                foreach (var v in values)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(100.0 * ++done / total);
                    var cand = best.Clone();
                    set(cand, v);
                    cand = cand.Sanitized();
                    var r = Evaluate(ds, Vec(cand), ev, cand, ct);
                    bool keep = Tot(r) > Tot(bestRes) + 1e-12
                                && Fold(r, even) >= Fold(bestRes, even) - 1e-12
                                && Fold(r, odd) >= Fold(bestRes, odd) - 1e-12;
                    steps.Add(new CalibrationStep(name, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, r.Hit10, r.Hit20, keep));
                    if (keep)
                    {
                        best = cand;
                        bestRes = r;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        progress?.Report(100);
        var ci = BootstrapCi(ev, bestRes.PerEvent10, cur.PerEvent10);
        bool recommended = bestRes.Hit10 > cur.Hit10 && ci.Low > 0;
        string verdict = recommended
            ? $"Gain probable : top 10 {cur.Hit10:P1} → {bestRes.Hit10:P1} (écart IC95 [{ci.Low:+0.0%;-0.0%};{ci.High:+0.0%;-0.0%}])."
            : ReferenceEquals(bestRes, cur)
                ? "Aucun réglage ne fait mieux que l'actuel sur les deux moitiés de l'historique : on garde l'actuel."
                : $"Écart dans la marge d'incertitude (top 10 {cur.Hit10:P1} → {bestRes.Hit10:P1}, IC95 [{ci.Low:+0.0%;-0.0%};{ci.High:+0.0%;-0.0%}]) : application déconseillée.";
        return new CalibrationReport
        {
            Current = cur,
            Proposed = bestRes,
            ProposedSettings = best,
            DeltaCi = ci,
            Steps = steps,
            Recommended = recommended,
            Verdict = verdict,
        };
    }
}
