using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Tout ce qui est connu à l'instant Now (aucune information postérieure).</summary>
public sealed class Context
{
    /// <summary>Construit le contexte : signaux de chaque utilisateur, titres aimés, « vus ensemble ».</summary>
    public Context(Dataset ds, double now, IReadOnlyDictionary<string, Dictionary<string, Interaction>> hist, EngineSettings p)
    {
        Ds = ds;
        Now = now;
        P = p;
        Hist = hist;
        Sig = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (u, h) in hist)
        {
            var s = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (tid, x) in h)
            {
                var t = ds.Titles[tid];
                s[tid] = Core.Signal(x, t.Kind, t.EpisodeCount, now, p);
            }

            Sig[u] = s;
        }

        Pos = Sig.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value.Where(e => e.Value > 0).Select(e => e.Key).ToList(), StringComparer.Ordinal);
        Cooc = Core.ItemCooc(Pos.Values, p.Shrink, p.MinCommon);
    }

    /// <summary>Jeu de données.</summary>
    public Dataset Ds { get; }

    /// <summary>Instant de référence.</summary>
    public double Now { get; }

    /// <summary>Réglages.</summary>
    public EngineSettings P { get; }

    /// <summary>Historique connu.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, Interaction>> Hist { get; }

    /// <summary>Signaux signés par utilisateur.</summary>
    public Dictionary<string, Dictionary<string, double>> Sig { get; }

    /// <summary>Titres aimés par utilisateur.</summary>
    public Dictionary<string, IReadOnlyCollection<string>> Pos { get; }

    /// <summary>Similarité « vus ensemble ».</summary>
    public Dictionary<string, Dictionary<string, double>> Cooc { get; }
}

/// <summary>Détail d'un score (pour l'aperçu et le diagnostic).</summary>
public readonly record struct ScoreParts(double Taste, double Fresh, double Popularity, double Quality, double Tmdb);

/// <summary>Score et liste finale.</summary>
public static class Recommender
{
    /// <summary>
    /// Score de chaque candidat. Le goût (profil, titre aimé le plus proche, vus ensemble, ressemblance aux abandons)
    /// décide seul de l'ordre ; nouveauté, popularité, qualité et accord TMDb sont des bonus multiplicatifs
    /// réservés aux GateTop premiers par goût (garde-fou toujours actif).
    /// </summary>
    /// <param name="tmdbAgreement">Facultatif : candidat → part des titres aimés dont TMDb le recommande (mode étendu).</param>
    public static Dictionary<string, double> Score(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user,
        IReadOnlyList<string> cands, EngineSettings w, IReadOnlyDictionary<string, double>? tmdbAgreement = null,
        Dictionary<string, ScoreParts>? parts = null)
    {
        var T = ctx.Ds.Titles;
        var sig = ctx.Sig.TryGetValue(user, out var su) ? su : new Dictionary<string, double>();
        var pos = sig.Where(e => e.Value > 0).ToList();
        var neg = sig.Where(e => e.Value < 0).Select(e => new KeyValuePair<string, double>(e.Key, -e.Value)).ToList();

        var prof = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (t, s) in pos)
        {
            if (!vec.TryGetValue(t, out var v))
            {
                continue;
            }

            for (int i = 0; i < v.Keys.Length; i++)
            {
                prof[v.Keys[i]] = prof.GetValueOrDefault(v.Keys[i]) + (s * v.Values[i]);
            }
        }

        foreach (var (t, s) in neg)
        {
            if (!vec.TryGetValue(t, out var v))
            {
                continue;
            }

            for (int i = 0; i < v.Keys.Length; i++)
            {
                prof[v.Keys[i]] = prof.GetValueOrDefault(v.Keys[i]) - (w.WNegative * s * v.Values[i]);
            }
        }

        double pn = Math.Sqrt(prof.Values.Sum(x => x * x));
        if (pn == 0)
        {
            pn = 1.0;
        }

        foreach (var k in prof.Keys.ToList())
        {
            prof[k] /= pn;
        }

        double tot = pos.Sum(e => e.Value);
        if (tot == 0)
        {
            tot = 1.0;
        }

        double maxPos = pos.Count > 0 ? pos.Max(e => e.Value) : 1.0;
        var othersPop = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (u, s) in ctx.Pos)
        {
            if (string.Equals(u, user, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var t in s)
            {
                othersPop[t] = othersPop.GetValueOrDefault(t) + 1;
            }
        }

        int maxPop = othersPop.Count > 0 ? othersPop.Values.Max() : 1;
        var posVec = pos.Where(e => vec.ContainsKey(e.Key)).Select(e => (S: e.Value, V: vec[e.Key])).ToList();
        var negVec = neg.Where(e => vec.ContainsKey(e.Key)).Select(e => (S: e.Value, V: vec[e.Key])).ToList();

        var taste = new double[cands.Count];
        var fr = new double[cands.Count];
        var pop = new double[cands.Count];
        var q = new double[cands.Count];
        var tm = new double[cands.Count];
        for (int ci = 0; ci < cands.Count; ci++)
        {
            var c = cands[ci];
            var v = vec[c];
            double cen = v.Dot(prof);
            double ms = 0;
            if (posVec.Count > 0)
            {
                ms = double.NegativeInfinity;
                foreach (var (s, pv) in posVec)
                {
                    ms = Math.Max(ms, s * pv.Dot(v));
                }

                ms /= maxPos;
            }

            double col = 0;
            foreach (var (t, s) in pos)
            {
                if (ctx.Cooc.TryGetValue(t, out var row) && row.TryGetValue(c, out var cv))
                {
                    col += s * cv;
                }
            }

            col /= tot;
            double negPen = 0;
            if (negVec.Count > 0)
            {
                negPen = double.NegativeInfinity;
                foreach (var (s, nv) in negVec)
                {
                    negPen = Math.Max(negPen, s * nv.Dot(v));
                }
            }

            var tc = T[c];
            pop[ci] = Math.Log(1 + othersPop.GetValueOrDefault(c)) / Math.Log(1 + maxPop);
            double comm = tc.Community is double cm && cm != 0 ? cm : 6.5;
            q[ci] = Math.Clamp((comm - 6.5) / 2.0, -1.0, 1.0);
            double added = tc.Added ?? 0;
            fr[ci] = added != 0 ? Math.Exp(-Math.Max(0.0, (ctx.Now - added) / Core.Day) / w.FreshDays) : 0.0;
            tm[ci] = tmdbAgreement is not null && tmdbAgreement.TryGetValue(c, out var a) ? a : 0.0;
            taste[ci] = (w.WCentroid * cen) + (w.WMaxSim * ms) + (w.WCollab * col) - (w.WNegative * negPen);
        }

        // garde-fou : bonus réservés au haut du classement par goût (rang stable, ordre des candidats en cas d'égalité)
        var order = Enumerable.Range(0, cands.Count).OrderByDescending(i => taste[i]).ToArray();
        var rank = new int[cands.Count];
        for (int r = 0; r < order.Length; r++)
        {
            rank[order[r]] = r;
        }

        int top = Math.Max(1, w.GateTop);
        double wf = Math.Min(w.WFresh, EngineSettings.FreshCeiling);
        var outp = new Dictionary<string, double>(cands.Count, StringComparer.Ordinal);
        for (int ci = 0; ci < cands.Count; ci++)
        {
            double g = Math.Max(0.0, 1.0 - ((double)rank[ci] / top));
            double tp = Math.Max(taste[ci], 0.0);
            outp[cands[ci]] = taste[ci] + (tp * g * ((wf * fr[ci]) + (w.WPopularity * pop[ci]) + (w.WQuality * q[ci]) + (w.WTmdbAgreement * tm[ci])));
            parts?.TryAdd(cands[ci], new ScoreParts(taste[ci], tp * g * wf * fr[ci], tp * g * w.WPopularity * pop[ci], tp * g * w.WQuality * q[ci], tp * g * w.WTmdbAgreement * tm[ci]));
        }

        return outp;
    }

    /// <summary>Collection la plus précise (la plus petite, au moins 2 titres) contenant t.</summary>
    public static (int Size, string Id)? Franchise(Dataset ds, string t)
    {
        (int, string)? best = null;
        foreach (var c in ds.Titles[t].Collections)
        {
            int n = ds.Collections.TryGetValue(c, out var l) ? l.Count : 0;
            if (n >= 2 && (best is null || n < best.Value.Item1))
            {
                best = (n, c);
            }
        }

        return best;
    }

    /// <summary>
    /// Classement + diversité (MMR) + règle de saga : une seule entrée par saga (deux par regroupement thématique),
    /// et pour une saga le premier volet pas encore vu plutôt qu'un volet au milieu.
    /// </summary>
    public static List<string> Rank(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, IReadOnlyList<string> cands,
        IReadOnlyDictionary<string, double> sc, int k, EngineSettings w)
    {
        var T = ctx.Ds.Titles;
        var ranked = Enumerable.Range(0, cands.Count).OrderByDescending(i => sc[cands[i]]).Select(i => cands[i]).ToList();
        if (w.Mmr <= 0 && !w.Saga)
        {
            return ranked.Take(k).ToList();
        }

        var cset = new HashSet<string>(cands, StringComparer.Ordinal);
        var pool = ranked.Take(k * 8).ToList();
        var outp = new List<string>();
        var outSet = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        // pénalité de redondance courante par élément du pool (max des similarités avec la sortie)
        var pen = new Dictionary<string, double>(StringComparer.Ordinal);
        while (pool.Count > 0 && outp.Count < k)
        {
            int bi = 0;
            double bv = double.NegativeInfinity;
            for (int i = 0; i < pool.Count; i++)
            {
                double val = sc[pool[i]] - (w.Mmr * (pen.TryGetValue(pool[i], out var pv) ? pv : 0.0));
                if (val > bv)
                {
                    bv = val;
                    bi = i;
                }
            }

            var best = pool[bi];
            pool.RemoveAt(bi);
            if (w.Saga)
            {
                var fr = Franchise(ctx.Ds, best);
                if (fr is { } f)
                {
                    if (used.GetValueOrDefault(f.Id) >= (f.Size <= w.SagaMax ? 1 : 2))
                    {
                        continue;
                    }

                    if (f.Size <= w.SagaMax)
                    {
                        var kind = T[best].Kind;
                        string? first = null;
                        (int, string)? fk = null;
                        foreach (var t in ctx.Ds.Collections[f.Id])
                        {
                            if (!cset.Contains(t) || T[t].Kind != kind)
                            {
                                continue;
                            }

                            var key = (T[t].Year ?? 9999, T[t].Name);
                            if (fk is null || key.Item1 < fk.Value.Item1 || (key.Item1 == fk.Value.Item1 && string.CompareOrdinal(key.Name, fk.Value.Item2) < 0))
                            {
                                fk = key;
                                first = t;
                            }
                        }

                        if (first is not null && !outSet.Contains(first))
                        {
                            best = first;
                        }
                    }

                    used[f.Id] = used.GetValueOrDefault(f.Id) + 1;
                }
            }

            if (outSet.Add(best))
            {
                outp.Add(best);
                if (w.Mmr > 0)
                {
                    var bvVec = vec[best];
                    foreach (var c in pool)
                    {
                        double s = vec[c].Dot(bvVec);
                        if (!pen.TryGetValue(c, out var cur) || s > cur)
                        {
                            pen[c] = s;
                        }
                    }
                }
            }
        }

        return outp;
    }
}
