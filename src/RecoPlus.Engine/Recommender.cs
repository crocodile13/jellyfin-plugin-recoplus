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
public readonly record struct ScoreParts(double Taste, double Fresh, double Popularity, double Quality, double Tmdb, double Release = 0);

/// <summary>Score et liste finale.</summary>
public static class Recommender
{
    /// <summary>
    /// Note sur 10 selon la source choisie ; absente = 6,5 (neutre). Une note nulle compte comme absente. La note de la
    /// communauté est ramenée vers 6,5 d'autant plus qu'elle repose sur peu de votes (moyenne bayésienne, priorVotes).
    /// </summary>
    public static double Quality(Title t, RatingSource src, double priorVotes = 0)
    {
        double? comm = t.Community is double cm && cm != 0 ? cm : null;
        if (comm is double c0 && priorVotes > 0 && t.Votes is int nv && nv >= 0)
        {
            comm = ((nv * c0) + (priorVotes * 6.5)) / (nv + priorVotes);
        }

        double? crit = t.Critic is double cr && cr != 0 ? cr / 10.0 : null;
        double? v = src switch
        {
            RatingSource.Critic => crit ?? comm,
            RatingSource.Best => comm is null ? crit : crit is null ? comm : Math.Max(comm.Value, crit.Value),
            RatingSource.Mean => comm is null ? crit : crit is null ? comm : (comm.Value + crit.Value) / 2.0,
            _ => comm,
        };
        return v ?? 6.5;
    }

    /// <summary>
    /// Avis du foyer sur un titre, sans ceux du compte lui-même, ramenés à -1..+1 : moyenne bayésienne des étoiles avec
    /// <paramref name="prior"/> avis neutres (3) — un seul avis 5 étoiles pèse peu, cinq avis concordants pleinement.
    /// </summary>
    public static double Household(Dataset ds, string title, string user, double prior)
    {
        if (!ds.Reviews.TryGetValue(title, out var by))
        {
            return 0.0;
        }

        double sum = 0;
        int n = 0;
        foreach (var (u, stars) in by)
        {
            if (!string.Equals(u, user, StringComparison.Ordinal))
            {
                sum += stars;
                n++;
            }
        }

        return n == 0 ? 0.0 : Math.Clamp((((sum + (prior * 3.0)) / (n + prior)) - 3.0) / 2.0, -1.0, 1.0);
    }

    /// <summary>
    /// Composantes brutes du score de chaque candidat (avant pondération) : profil, titre aimé le plus proche, vus
    /// ensemble, ressemblance aux abandons, goût du moment, puis les bases des bonus réservés.
    /// </summary>
    public sealed class Components
    {
        internal Components(int n)
        {
            Cen = new double[n];
            Ms = new double[n];
            Col = new double[n];
            Neg = new double[n];
            St = new double[n];
            Fr = new double[n];
            Pop = new double[n];
            Q = new double[n];
            Tm = new double[n];
            Re = new double[n];
            Hh = new double[n];
        }

        /// <summary>Proximité au profil moyen.</summary>
        public double[] Cen { get; }

        /// <summary>Proximité au titre aimé le plus proche.</summary>
        public double[] Ms { get; }

        /// <summary>Vus ensemble.</summary>
        public double[] Col { get; }

        /// <summary>Ressemblance au titre abandonné le plus proche.</summary>
        public double[] Neg { get; }

        /// <summary>Proximité au goût du moment.</summary>
        public double[] St { get; }

        /// <summary>Nouveauté (0..1).</summary>
        public double[] Fr { get; }

        /// <summary>Popularité dans le foyer (0..1).</summary>
        public double[] Pop { get; }

        /// <summary>Note (-1..1).</summary>
        public double[] Q { get; }

        /// <summary>Accord TMDb (0..1).</summary>
        public double[] Tm { get; }

        /// <summary>Sortie récente (0..1).</summary>
        public double[] Re { get; }

        /// <summary>Avis du foyer (-1..1).</summary>
        public double[] Hh { get; }
    }

    /// <summary>Calcule les composantes brutes (le profil moyen dépend de WNegative, le reste non).</summary>
    public static Components Compute(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user,
        IReadOnlyList<string> cands, EngineSettings w, IReadOnlyDictionary<string, double>? tmdbAgreement = null, SimilarityIndex? sim = null)
    {
        var T = ctx.Ds.Titles;
        var sig = ctx.Sig.TryGetValue(user, out var su) ? su : new Dictionary<string, double>();
        var pos = sig.Where(e => e.Value > 0).ToList();
        var neg = sig.Where(e => e.Value < 0).Select(e => new KeyValuePair<string, double>(e.Key, -e.Value)).ToList();

        static (Dictionary<string, double> V, double N) Profile(IEnumerable<(double S, SparseVector V)> plus, IEnumerable<(double S, SparseVector V)> minus)
        {
            var p = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (s, v) in plus)
            {
                for (int i = 0; i < v.Keys.Length; i++)
                {
                    p[v.Keys[i]] = p.GetValueOrDefault(v.Keys[i]) + (s * v.Values[i]);
                }
            }

            foreach (var (s, v) in minus)
            {
                for (int i = 0; i < v.Keys.Length; i++)
                {
                    p[v.Keys[i]] = p.GetValueOrDefault(v.Keys[i]) - (s * v.Values[i]);
                }
            }

            double n = Math.Sqrt(p.Values.Sum(x => x * x));
            if (n == 0)
            {
                n = 1.0;
            }

            foreach (var k in p.Keys.ToList())
            {
                p[k] /= n;
            }

            return (p, n);
        }

        var posVec = pos.Where(e => vec.ContainsKey(e.Key)).Select(e => (S: e.Value, V: vec[e.Key])).ToList();
        var negVec = neg.Where(e => vec.ContainsKey(e.Key)).Select(e => (S: e.Value, V: vec[e.Key])).ToList();
        var (prof, pn) = Profile(posVec, negVec.Select(e => (w.WNegative * e.S, e.V)));

        // goût du moment : les ShortTermTitles derniers titres aimés (dernière activité), profil des seuls aimés
        var hist = ctx.Hist.TryGetValue(user, out var hu) ? hu : null;
        var shortIds = w.WShortTerm > 0
            ? pos.Where(e => vec.ContainsKey(e.Key))
                .OrderByDescending(e => hist is not null && hist.TryGetValue(e.Key, out var x) ? x.Last : 0)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(w.ShortTermTitles).ToList()
            : new List<KeyValuePair<string, double>>();
        var (stProf, stn) = Profile(shortIds.Select(e => (e.Value, vec[e.Key])), Enumerable.Empty<(double, SparseVector)>());

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

        // avec l'index : mêmes calculs, produits scalaires lus dans des lignes précalculées
        (double S, double[] R)[]? posRows = null, negRows = null, stRows = null;
        if (sim is not null && ReferenceEquals(sim.Vectors, vec))
        {
            posRows = pos.Where(e => vec.ContainsKey(e.Key)).Select(e => (e.Value, sim.Row(e.Key)!)).ToArray();
            negRows = neg.Where(e => vec.ContainsKey(e.Key)).Select(e => (e.Value, sim.Row(e.Key)!)).ToArray();
            stRows = shortIds.Select(e => (e.Value, sim.Row(e.Key)!)).ToArray();
            if (posRows.Any(r => r.R is null) || negRows.Any(r => r.R is null) || stRows.Any(r => r.R is null))
            {
                posRows = negRows = stRows = null; // budget mémoire de l'index atteint : calcul direct
            }
        }

        var c = new Components(cands.Count);
        for (int ci = 0; ci < cands.Count; ci++)
        {
            var id = cands[ci];
            var v = vec[id];
            int col0 = sim?.Column(id) ?? -1;
            double cen, ms = 0, negPen = 0, st = 0;
            if (posRows is not null && negRows is not null && stRows is not null && col0 >= 0)
            {
                // prof·v = (Σ s·(t·v) − WNeg·Σ s·(n·v)) / |prof|
                double dotv = 0;
                foreach (var (s, r) in posRows)
                {
                    dotv += s * r[col0];
                }

                foreach (var (s, r) in negRows)
                {
                    dotv -= w.WNegative * s * r[col0];
                }

                cen = dotv / pn;
                if (posRows.Length > 0)
                {
                    ms = double.NegativeInfinity;
                    foreach (var (s, r) in posRows)
                    {
                        ms = Math.Max(ms, s * r[col0]);
                    }

                    ms /= maxPos;
                }

                if (negRows.Length > 0)
                {
                    negPen = double.NegativeInfinity;
                    foreach (var (s, r) in negRows)
                    {
                        negPen = Math.Max(negPen, s * r[col0]);
                    }
                }

                if (stRows.Length > 0)
                {
                    double d = 0;
                    foreach (var (s, r) in stRows)
                    {
                        d += s * r[col0];
                    }

                    st = d / stn;
                }
            }
            else
            {
                cen = v.Dot(prof);
                if (posVec.Count > 0)
                {
                    ms = double.NegativeInfinity;
                    foreach (var (s, pv) in posVec)
                    {
                        ms = Math.Max(ms, s * pv.Dot(v));
                    }

                    ms /= maxPos;
                }

                if (negVec.Count > 0)
                {
                    negPen = double.NegativeInfinity;
                    foreach (var (s, nv) in negVec)
                    {
                        negPen = Math.Max(negPen, s * nv.Dot(v));
                    }
                }

                if (shortIds.Count > 0)
                {
                    st = v.Dot(stProf);
                }
            }

            double col = 0;
            foreach (var (t, s) in pos)
            {
                if (ctx.Cooc.TryGetValue(t, out var row) && row.TryGetValue(id, out var cv))
                {
                    col += s * cv;
                }
            }

            var tc = T[id];
            c.Cen[ci] = cen;
            c.Ms[ci] = ms;
            c.Col[ci] = col / tot;
            c.Neg[ci] = negPen;
            c.St[ci] = st;
            c.Pop[ci] = Math.Log(1 + othersPop.GetValueOrDefault(id)) / Math.Log(1 + maxPop);
            c.Q[ci] = Math.Clamp((Quality(tc, w.Rating, w.RatingPriorVotes) - 6.5) / 2.0, -1.0, 1.0);
            c.Hh[ci] = Household(ctx.Ds, id, user, w.HouseholdPrior);
            double rel = tc.Released ?? 0;
            c.Re[ci] = rel != 0 ? Math.Exp(-Math.Max(0.0, (ctx.Now - rel) / Core.Day) / w.ReleaseDays) : 0.0;
            double added = tc.Added ?? 0;
            c.Fr[ci] = added != 0 ? Math.Exp(-Math.Max(0.0, (ctx.Now - added) / Core.Day) / w.FreshDays) : 0.0;
            c.Tm[ci] = tmdbAgreement is not null && tmdbAgreement.TryGetValue(id, out var a) ? a : 0.0;
        }

        return c;
    }

    /// <summary>Goût d'un candidat : seul juge de l'ordre avant les bonus.</summary>
    public static double Taste(Components c, int i, EngineSettings w)
        => (w.WCentroid * c.Cen[i]) + (w.WMaxSim * c.Ms[i]) + (w.WCollab * c.Col[i]) - (w.WNegative * c.Neg[i]) + (w.WShortTerm * c.St[i]);

    /// <summary>Rang de chaque candidat par goût (stable : ordre des candidats en cas d'égalité) et facteur de garde-fou.</summary>
    public static double[] Gate(double[] taste, int gateTop)
    {
        var order = Enumerable.Range(0, taste.Length).OrderByDescending(i => taste[i]).ToArray();
        var g = new double[taste.Length];
        int top = Math.Max(1, gateTop);
        for (int r = 0; r < order.Length; r++)
        {
            g[order[r]] = Math.Max(0.0, 1.0 - ((double)r / top));
        }

        return g;
    }

    /// <summary>
    /// Part retirée du score d'un titre montré sans être ouvert : 1 − e^(−jours/ImpressionDays), effacée de moitié
    /// toutes les ImpressionRecoveryDays depuis le dernier affichage ; × WImpression.
    /// </summary>
    public static double ImpressionPenalty(Exposure e, double now, EngineSettings w)
    {
        if (w.WImpression <= 0 || e.Days <= 0)
        {
            return 0.0;
        }

        double age = Math.Max(0.0, (now - e.Last) / Core.Day);
        return w.WImpression * (1.0 - Math.Exp(-e.Days / w.ImpressionDays)) * Math.Pow(0.5, age / w.ImpressionRecoveryDays);
    }

    /// <summary>
    /// Score de chaque candidat. Le goût (profil, titre aimé le plus proche, vus ensemble, ressemblance aux abandons,
    /// goût du moment) décide seul de l'ordre ; nouveauté, popularité, qualité, accord TMDb, sortie récente et avis du
    /// foyer sont des bonus multiplicatifs réservés aux GateTop premiers par goût (garde-fou toujours actif). Enfin, un
    /// titre déjà montré sans être ouvert perd une part de son score.
    /// </summary>
    /// <param name="tmdbAgreement">Facultatif : candidat → part des titres aimés dont TMDb le recommande (mode étendu).</param>
    /// <param name="exposure">Facultatif : affichages sans ouverture, par titre.</param>
    public static Dictionary<string, double> Score(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user,
        IReadOnlyList<string> cands, EngineSettings w, IReadOnlyDictionary<string, double>? tmdbAgreement = null,
        Dictionary<string, ScoreParts>? parts = null, SimilarityIndex? sim = null, IReadOnlyDictionary<string, Exposure>? exposure = null)
    {
        var c = Compute(ctx, vec, user, cands, w, tmdbAgreement, sim);
        var taste = new double[cands.Count];
        for (int i = 0; i < taste.Length; i++)
        {
            taste[i] = Taste(c, i, w);
        }

        var gate = Gate(taste, w.GateTop);
        double wf = Math.Min(w.WFresh, EngineSettings.FreshCeiling);
        var outp = new Dictionary<string, double>(cands.Count, StringComparer.Ordinal);
        for (int ci = 0; ci < cands.Count; ci++)
        {
            double g = gate[ci];
            double tp = Math.Max(taste[ci], 0.0);
            double sc = taste[ci] + (tp * g * ((wf * c.Fr[ci]) + (w.WPopularity * c.Pop[ci]) + (w.WQuality * c.Q[ci]) + (w.WTmdbAgreement * c.Tm[ci]) + (w.WRelease * c.Re[ci]) + (w.WHousehold * c.Hh[ci])));
            if (exposure is not null && exposure.TryGetValue(cands[ci], out var ex))
            {
                sc -= ImpressionPenalty(ex, ctx.Now, w) * Math.Abs(sc);
            }

            outp[cands[ci]] = sc;
            parts?.TryAdd(cands[ci], new ScoreParts(taste[ci], tp * g * wf * c.Fr[ci], tp * g * w.WPopularity * c.Pop[ci], tp * g * w.WQuality * c.Q[ci], tp * g * w.WTmdbAgreement * c.Tm[ci], tp * g * w.WRelease * c.Re[ci]));
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
    /// Classement + diversité + règle de saga : une seule entrée par saga (deux par regroupement thématique), et pour une
    /// saga le premier volet pas encore vu plutôt qu'un volet au milieu. La diversité suit <see cref="EngineSettings.Diversity"/>
    /// (fixe, personnalisée ou calibrée) quand le compte est donné, sinon elle est fixe.
    /// </summary>
    /// <param name="sim">Facultatif : index des produits scalaires (mêmes vecteurs), pour aller plus vite.</param>
    public static List<string> Rank(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, IReadOnlyList<string> cands,
        IReadOnlyDictionary<string, double> sc, int k, EngineSettings w, string? user = null, SimilarityIndex? sim = null)
    {
        var dot = Dotter(vec, sim);
        var ranked = Enumerable.Range(0, cands.Count).OrderByDescending(i => sc[cands[i]]).Select(i => cands[i]).ToList();
        var mode = user is null ? DiversityMode.Fixed : w.Diversity;
        if (mode == DiversityMode.Fixed && w.Mmr <= 0 && !w.Saga)
        {
            return ranked.Take(k).ToList();
        }

        var pool = ranked.Take(k * 8).ToList();
        if (mode == DiversityMode.Personalized && HistorySimilarity(ctx, vec, user!) is double target)
        {
            // plusieurs forces de diversité ; on garde la liste dont la ressemblance interne est la plus proche de celle
            // de l'historique du compte (à égalité, la force de base)
            List<string>? best = null;
            double bestGap = double.PositiveInfinity;
            foreach (var f in PersonalFactors)
            {
                var l = Greedy(ctx, cands, pool, k, w, new MmrPicker(sc, dot, w.Mmr * f));
                double gap = Math.Abs(ListSimilarity(dot, l.Take(ShownForVariety).ToList()) - target);
                if (gap < bestGap - 1e-12)
                {
                    (best, bestGap) = (l, gap);
                }
            }

            return best!;
        }

        if (mode == DiversityMode.Calibrated && GenreDistribution(ctx, user!) is { Count: > 0 } p)
        {
            return Greedy(ctx, cands, pool, k, w, new CalibratedPicker(sc, ctx.Ds.Titles, p, w.CalibrationStrength, pool));
        }

        return Greedy(ctx, cands, pool, k, w, new MmrPicker(sc, dot, w.Mmr));
    }

    /// <summary>Produit scalaire entre deux titres : lu dans l'index s'il porte sur ces vecteurs, calculé sinon (même valeur).</summary>
    private static Func<string, string, double> Dotter(IReadOnlyDictionary<string, SparseVector> vec, SimilarityIndex? sim)
    {
        if (sim is null || !ReferenceEquals(sim.Vectors, vec))
        {
            return (a, b) => vec[a].Dot(vec[b]);
        }

        return (a, b) =>
        {
            int c = sim.Column(b);
            return c >= 0 && sim.Row(a) is { } r ? r[c] : vec[a].Dot(vec[b]);
        };
    }

    /// <summary>Diversité personnalisée : la variété se juge sur le début de la liste, celui qui est affiché.</summary>
    public const int ShownForVariety = 20;

    /// <summary>Forces essayées par la diversité personnalisée (multiples de Mmr), la force de base d'abord.</summary>
    internal static readonly double[] PersonalFactors = { 1.0, 0.5, 2.0, 0.0, 4.0 };

    /// <summary>
    /// Ressemblance moyenne entre les titres aimés du compte (les 30 plus forts), pondérée par leurs signaux ; null s'il
    /// en a moins de 3 (pas de quoi juger de sa variété).
    /// </summary>
    public static double? HistorySimilarity(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user)
    {
        if (!ctx.Sig.TryGetValue(user, out var sig))
        {
            return null;
        }

        var pos = sig.Where(e => e.Value > 0 && vec.ContainsKey(e.Key)).OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(30).Select(e => (S: e.Value, V: vec[e.Key])).ToList();
        if (pos.Count < 3)
        {
            return null;
        }

        double num = 0, den = 0;
        for (int i = 0; i < pos.Count; i++)
        {
            for (int j = i + 1; j < pos.Count; j++)
            {
                double ww = pos[i].S * pos[j].S;
                num += ww * pos[i].V.Dot(pos[j].V);
                den += ww;
            }
        }

        return den > 0 ? num / den : null;
    }

    /// <summary>Ressemblance moyenne entre les titres d'une liste.</summary>
    public static double ListSimilarity(IReadOnlyDictionary<string, SparseVector> vec, IReadOnlyList<string> l)
        => ListSimilarity((a, b) => vec[a].Dot(vec[b]), l);

    private static double ListSimilarity(Func<string, string, double> dot, IReadOnlyList<string> l)
    {
        double s = 0;
        int n = 0;
        for (int i = 0; i < l.Count; i++)
        {
            for (int j = i + 1; j < l.Count; j++)
            {
                s += dot(l[i], l[j]);
                n++;
            }
        }

        return n == 0 ? 0 : s / n;
    }

    /// <summary>Répartition des genres de ce que le compte aime (chaque titre réparti entre ses genres, pondéré par son signal).</summary>
    public static Dictionary<string, double> GenreDistribution(Context ctx, string user)
    {
        var p = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!ctx.Sig.TryGetValue(user, out var sig))
        {
            return p;
        }

        double tot = 0;
        foreach (var (t, s) in sig.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (s <= 0 || !ctx.Ds.Titles.TryGetValue(t, out var tt) || tt.Genres.Count == 0)
            {
                continue;
            }

            var gs = tt.Genres.Distinct(StringComparer.Ordinal).ToList();
            foreach (var g in gs)
            {
                p[g] = p.GetValueOrDefault(g) + (s / gs.Count);
            }

            tot += s;
        }

        foreach (var g in p.Keys.ToList())
        {
            p[g] /= tot;
        }

        return p;
    }

    /// <summary>Choix glouton du titre suivant d'une liste.</summary>
    private interface IPicker
    {
        int Pick(List<string> pool);

        void Added(string id, List<string> pool);
    }

    /// <summary>MMR : score − λ × ressemblance maximale avec la liste déjà formée.</summary>
    private sealed class MmrPicker : IPicker
    {
        private readonly IReadOnlyDictionary<string, double> _sc;
        private readonly Func<string, string, double> _dot;
        private readonly double _l;
        private readonly Dictionary<string, double> _pen = new(StringComparer.Ordinal);

        public MmrPicker(IReadOnlyDictionary<string, double> sc, Func<string, string, double> dot, double lambda)
        {
            _sc = sc;
            _dot = dot;
            _l = lambda;
        }

        public int Pick(List<string> pool)
        {
            int bi = 0;
            double bv = double.NegativeInfinity;
            for (int i = 0; i < pool.Count; i++)
            {
                double val = _sc[pool[i]] - (_l * (_pen.TryGetValue(pool[i], out var pv) ? pv : 0.0));
                if (val > bv)
                {
                    bv = val;
                    bi = i;
                }
            }

            return bi;
        }

        public void Added(string id, List<string> pool)
        {
            if (_l <= 0)
            {
                return;
            }

            foreach (var c in pool)
            {
                double s = _dot(id, c);
                if (!_pen.TryGetValue(c, out var cur) || s > cur)
                {
                    _pen[c] = s;
                }
            }
        }
    }

    /// <summary>
    /// Calibrée (Steck, RecSys 2018) : (1 − λ) × score normalisé − λ × KL(p ‖ q̃), p = genres de l'historique,
    /// q̃ = (1 − α) q(liste) + α p, α = 0,01.
    /// </summary>
    private sealed class CalibratedPicker : IPicker
    {
        private const double Alpha = 0.01;
        private readonly IReadOnlyDictionary<string, double> _sc;
        private readonly IReadOnlyDictionary<string, Title> _titles;
        private readonly Dictionary<string, double> _p;
        private readonly string[] _genres;
        private readonly double _l;
        private readonly double _min;
        private readonly double _span;
        private readonly Dictionary<string, double> _q = new(StringComparer.Ordinal);
        private int _n;

        public CalibratedPicker(IReadOnlyDictionary<string, double> sc, IReadOnlyDictionary<string, Title> titles, Dictionary<string, double> p, double lambda, List<string> pool)
        {
            _sc = sc;
            _titles = titles;
            _p = p;
            _genres = p.Keys.OrderBy(g => g, StringComparer.Ordinal).ToArray();
            _l = lambda;
            _min = pool.Count > 0 ? pool.Min(c => sc[c]) : 0;
            double max = pool.Count > 0 ? pool.Max(c => sc[c]) : 0;
            _span = max - _min;
        }

        private double Kl(Title? extra)
        {
            var eg = extra is null ? null : extra.Genres.Distinct(StringComparer.Ordinal).ToList();
            int n = _n + (eg is { Count: > 0 } ? 1 : 0);
            double kl = 0;
            foreach (var g in _genres)
            {
                double q = _q.GetValueOrDefault(g);
                if (eg is { Count: > 0 } && eg.Contains(g, StringComparer.Ordinal))
                {
                    q += 1.0 / eg.Count;
                }

                q = n > 0 ? q / n : 0;
                double pg = _p[g];
                kl += pg * Math.Log(pg / (((1 - Alpha) * q) + (Alpha * pg)));
            }

            return kl;
        }

        public int Pick(List<string> pool)
        {
            int bi = 0;
            double bv = double.NegativeInfinity;
            for (int i = 0; i < pool.Count; i++)
            {
                double sn = _span > 0 ? (_sc[pool[i]] - _min) / _span : 0;
                double val = ((1 - _l) * sn) - (_l * Kl(_titles[pool[i]]));
                if (val > bv)
                {
                    bv = val;
                    bi = i;
                }
            }

            return bi;
        }

        public void Added(string id, List<string> pool)
        {
            var gs = _titles[id].Genres.Distinct(StringComparer.Ordinal).ToList();
            if (gs.Count == 0)
            {
                return;
            }

            _n++;
            foreach (var g in gs)
            {
                _q[g] = _q.GetValueOrDefault(g) + (1.0 / gs.Count);
            }
        }
    }

    private static List<string> Greedy(Context ctx, IReadOnlyList<string> cands, List<string> poolIn, int k, EngineSettings w, IPicker picker)
    {
        var T = ctx.Ds.Titles;
        var cset = new HashSet<string>(cands, StringComparer.Ordinal);
        var pool = poolIn.ToList();
        var outp = new List<string>();
        var outSet = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        while (pool.Count > 0 && outp.Count < k)
        {
            int bi = picker.Pick(pool);
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
                picker.Added(best, pool);
            }
        }

        return outp;
    }
}
