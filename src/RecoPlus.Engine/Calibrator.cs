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

    /// <summary>Gain de rang par cas, 1 / log2(1 + rang) (calibrage v2 ; vide sinon).</summary>
    public double[] PerEventGain { get; init; } = Array.Empty<double>();

    /// <summary>Qualité de classement : gain moyen pondéré (calibrage v2).</summary>
    public double Gain { get; init; }
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

    /// <summary>Écart de qualité en validation croisée par comptes (proposé appris sans le compte − actuel).</summary>
    public double CvDelta { get; init; }

    /// <summary>Méthode retenue.</summary>
    public CalibrationMethod Method { get; init; }

    /// <summary>Résultat de chaque méthode essayée.</summary>
    public List<MethodResult> Methods { get; init; } = new();
}

/// <summary>Résultat d'une méthode de calibrage.</summary>
public sealed class MethodResult
{
    /// <summary>Méthode.</summary>
    public CalibrationMethod Method { get; init; }

    /// <summary>Nom affiché.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Réglage proposé (appris sur tout l'historique).</summary>
    public required EngineSettings Settings { get; init; }

    /// <summary>Score du réglage proposé sur tout l'historique.</summary>
    public required EvalResult Result { get; init; }

    /// <summary>Changements proposés.</summary>
    public List<CalibrationStep> Steps { get; init; } = new();

    /// <summary>Écart en validation croisée par comptes.</summary>
    public double CvDelta { get; init; }

    /// <summary>IC95 de cet écart.</summary>
    public (double Low, double High) Ci { get; init; }

    /// <summary>Historique suffisant, limite ou insuffisant pour cette méthode.</summary>
    public string Sufficiency { get; init; } = string.Empty;

    /// <summary>Gain sûr, généralisable et d'au moins <see cref="Calibrator.MinPracticalGain"/>.</summary>
    public bool Recommended { get; set; }

    /// <summary>La méthode n'a rien changé au réglage actuel.</summary>
    public bool NoChange { get; set; }
}

/// <summary>
/// Évaluation temporelle et calibrage manuel : chaque fois qu'un compte a commencé un nouveau titre,
/// le titre figurait-il dans les recommandations calculées juste avant, avec seulement ce qui était connu alors ?
/// </summary>
public static class Calibrator
{
    /// <summary>Gain minimal (relatif, en validation croisée) pour recommander un réglage : en dessous, rien de visible.</summary>
    public const double MinPracticalGain = 0.01;

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
            var ad = h.Values.Where(x => !x.Backfill && ds.Titles.TryGetValue(x.Title, out var t) && !t.External && Adopted(x, t.Kind))
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
            kv => kv.Value.Where(e => e.Value.Backfill || (e.Value.Start != 0 && e.Value.Start < now)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
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
            var list = Recommender.Rank(ctx, vec, cands, sc, 20, s, u);
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

    // ------------------------------------------------------------------ calibrage (v2)

    /// <summary>Titres déjà adoptés pour qu'un cas compte (son poids croît ensuite avec cet historique).</summary>
    public const int MinPrior = 2;

    /// <summary>Un cas du calibrage : l'événement, le nombre de titres adoptés avant lui, son poids.</summary>
    public readonly record struct Case(EvalEvent Ev, int Prior, double Weight, bool Adopted = true);

    /// <summary>Poids d'un cas « commencé sans être adopté » (film arrêté, série à 1 ou 2 épisodes) : un clic, pas un choix confirmé.</summary>
    public const double StartedWeight = 0.5;

    private static bool Started(Interaction x, TitleKind k) => k == TitleKind.Movie ? x.Played || x.Completion >= 0.05 : x.Episodes >= 1;

    /// <summary>
    /// Cas du calibrage : chaque titre commencé (date connue, hors marquage en masse) alors que le compte avait déjà
    /// adopté au moins <see cref="MinPrior"/> titres (ceux de date inconnue comptent comme connus depuis toujours).
    /// Poids = confiance dans l'historique (n / (n + 3) : un compte qui démarre compte moins) × équilibre entre comptes
    /// (1 / √cas du compte : un gros spectateur ne décide pas seul) × <see cref="StartedWeight"/> pour un titre commencé
    /// mais pas adopté. Poids ramenés à une moyenne de 1.
    /// </summary>
    public static List<Case> Cases(Dataset ds)
    {
        var raw = new List<(EvalEvent Ev, int Prior, double W)>();
        foreach (var (u, h) in ds.Users)
        {
            int prior = h.Values.Count(x => x.Backfill && ds.Titles.TryGetValue(x.Title, out var t) && !t.External && Adopted(x, t.Kind));
            var started = h.Values.Where(x => !x.Backfill && x.Start != 0 && ds.Titles.TryGetValue(x.Title, out var t) && !t.External && Started(x, t.Kind))
                .OrderBy(x => x.Start).ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
            foreach (var x in started)
            {
                bool adopted = Adopted(x, ds.Titles[x.Title].Kind);
                if (prior >= MinPrior)
                {
                    raw.Add((new EvalEvent(u, x.Title, x.Start), prior, adopted ? 1.0 : StartedWeight));
                }

                if (adopted)
                {
                    prior++;
                }
            }
        }

        var perUser = raw.GroupBy(r => r.Ev.User).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var w = raw.Select(r => r.W * r.Prior / (r.Prior + 3.0) / Math.Sqrt(perUser[r.Ev.User])).ToArray();
        double mean = w.Length == 0 ? 1 : w.Average();
        return raw.Select((r, i) => new Case(r.Ev, r.Prior, w[i] / mean, r.W >= 1.0)).ToList();
    }

    /// <summary>
    /// Évalue un réglage sur des cas, avec une mesure fine : le RANG du titre adopté parmi tous les candidats (liste
    /// finale pour les 20 premiers, score au-delà), gain = 1 / log2(1 + rang). Bien plus de puissance statistique
    /// qu'un simple « dans le top 10 ou non ».
    /// </summary>
    public static EvalResult EvaluateRanks(Dataset ds, SimilarityIndex sim, IReadOnlyList<Case> cases, EngineSettings s, CancellationToken ct = default)
    {
        var vec = sim.Vectors;
        var h10 = new bool[cases.Count];
        var h20 = new bool[cases.Count];
        var gain = new double[cases.Count];
        // cas indépendants : en parallèle, sur la moitié des cœurs (Jellyfin garde de quoi servir les lectures)
        var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct };
        System.Threading.Tasks.Parallel.For(0, cases.Count, po, i =>
        {
            var (u, target, t0) = cases[i].Ev;
            var hist = Snapshot(ds, t0 - 1);
            var ctx = new Context(ds, t0 - 1, hist, s);
            var seen = hist[u];
            var cands = Candidates(ds, seen, t0);
            var sc = Recommender.Score(ctx, vec, u, cands, s, sim: sim);
            var list = Recommender.Rank(ctx, vec, cands, sc, 20, s, u, sim);
            int r = list.IndexOf(target);
            int rank;
            if (r >= 0)
            {
                rank = r + 1;
            }
            else if (sc.TryGetValue(target, out var st))
            {
                var inList = new HashSet<string>(list, StringComparer.Ordinal);
                rank = list.Count + 1 + sc.Count(kv => !inList.Contains(kv.Key) && kv.Value > st);
            }
            else
            {
                rank = cands.Count + 1;
            }

            h10[i] = rank <= 10;
            h20[i] = rank <= 20;
            gain[i] = 1.0 / Math.Log2(1 + rank);
        });

        int n = Math.Max(cases.Count, 1);
        double ws = cases.Sum(c => c.Weight);
        return new EvalResult
        {
            Events = cases.Count,
            Accounts = cases.Select(c => c.Ev.User).Distinct().Count(),
            Hit10 = h10.Count(b => b) / (double)n,
            Hit20 = h20.Count(b => b) / (double)n,
            PerEvent10 = h10,
            PerEvent20 = h20,
            PerEventGain = gain,
            Gain = ws > 0 ? cases.Select((c, k) => c.Weight * gain[k]).Sum() / ws : 0,
        };
    }

    /// <summary>Candidats d'un cas : titres de la bibliothèque pas encore commencés et déjà arrivés à T0.</summary>
    public static List<string> Candidates(Dataset ds, IReadOnlyDictionary<string, Interaction> seen, double t0)
        => ds.Titles.Values.Where(t => !t.External && !seen.ContainsKey(t.Id) && (t.Added ?? 0) <= t0).Select(t => t.Id).ToList();

    /// <summary>Deux moitiés par COMPTE (alternées par nombre de cas) : un gain doit tenir sur des personnes différentes.</summary>
    public static Dictionary<string, int> Folds(IReadOnlyList<Case> cases)
    {
        var users = cases.GroupBy(c => c.Ev.User).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        return users.Select((u, k) => (u, k % 2)).ToDictionary(x => x.u, x => x.Item2, StringComparer.Ordinal);
    }

    /// <summary>Un paramètre continu exploré : bornes sûres, échelle (log pour les poids multiplicatifs), zéro essayé ou non.</summary>
    /// <param name="When">Facultatif : le réglage n'a d'effet que si cette condition est vraie (sinon il n'est pas exploré).</param>
    public sealed record Knob(string Name, Func<EngineSettings, double> Get, Action<EngineSettings, double> Set, double Lo, double Hi, bool Log, bool Integer, bool TryZero,
        Func<EngineSettings, bool>? When = null);

    /// <summary>Un choix discret exploré.</summary>
    public sealed record Choice(string Name, object[] Values, Func<EngineSettings, object> Get, Action<EngineSettings, object> Set);

    /// <summary>Paramètres continus explorés (bornés ; le garde-fou n'est pas réglable, la nouveauté est plafonnée).</summary>
    public static IReadOnlyList<Knob> Knobs { get; } = new List<Knob>
    {
        new("Demi-vie (jours)", s => s.HalfLifeDays, (s, v) => s.HalfLifeDays = v, 20, 730, true, false, false),
        new("Poids minimal d'un titre ancien", s => s.RecencyFloor, (s, v) => s.RecencyFloor = v, 0, 0.6, false, false, true),
        new("Titre aimé le plus proche", s => s.WMaxSim, (s, v) => s.WMaxSim = v, 0.25, 6, true, false, false),
        new("Goûts d'ensemble", s => s.WCentroid, (s, v) => s.WCentroid = v, 0.25, 4, true, false, false),
        new("Vus ensemble", s => s.WCollab, (s, v) => s.WCollab = v, 0.05, 2, true, false, true),
        new("Rétrécissement", s => s.Shrink, (s, v) => s.Shrink = v, 1, 60, true, false, true),
        new("Favoris", s => s.FavoriteBoost, (s, v) => s.FavoriteBoost = v, 0.1, 3, true, false, true),
        new("Abandons", s => s.AbandonWeight, (s, v) => s.AbandonWeight = v, 0.05, 1.5, true, false, true),
        new("Écart aux abandons", s => s.WNegative, (s, v) => s.WNegative = v, 0.05, 2, true, false, true),
        new("Revisionnages", s => s.RewatchBoost, (s, v) => s.RewatchBoost = v, 0.05, 1.5, true, false, true),
        new("Notes et avis du compte", s => s.RatingWeight, (s, v) => s.RatingWeight = v, 0.1, 2, true, false, true),
        new("Nouveauté", s => s.WFresh, (s, v) => s.WFresh = v, 0.1, EngineSettings.FreshCeiling, true, false, true),
        new("Échelle de nouveauté (jours)", s => s.FreshDays, (s, v) => s.FreshDays = v, 10, 180, true, false, false, s => s.WFresh > 0),
        new("Popularité", s => s.WPopularity, (s, v) => s.WPopularity = v, 0.05, 2, true, false, true),
        new("Note", s => s.WQuality, (s, v) => s.WQuality = v, 0.02, 1.5, true, false, true),
        new("Sortie récente", s => s.WRelease, (s, v) => s.WRelease = v, 0.05, 2, true, false, true),
        new("Échelle de sortie récente (jours)", s => s.ReleaseDays, (s, v) => s.ReleaseDays = v, 60, 1500, true, false, false, s => s.WRelease > 0),
        new("Bonus réservés aux N premiers", s => s.GateTop, (s, v) => s.GateTop = (int)Math.Round(v), 20, 150, true, true, false),
        new("Diversité", s => s.Mmr, (s, v) => s.Mmr = v, 0.02, 1, true, false, true, s => s.Diversity != DiversityMode.Calibrated),
        new("Force de la diversité calibrée", s => s.CalibrationStrength, (s, v) => s.CalibrationStrength = v, 0.05, 0.8, false, false, false, s => s.Diversity == DiversityMode.Calibrated),
        new("Goût du moment", s => s.WShortTerm, (s, v) => s.WShortTerm = v, 0.05, 2, true, false, true),
        new("Heure de la journée", s => s.WTimeOfDay, (s, v) => s.WTimeOfDay = v, 0.05, 2, true, false, true),
        new("Titres du goût du moment", s => s.ShortTermTitles, (s, v) => s.ShortTermTitles = (int)Math.Round(v), 2, 15, true, true, false, s => s.WShortTerm > 0),
        new("Avis du foyer", s => s.WHousehold, (s, v) => s.WHousehold = v, 0.05, 2, true, false, true),
        new("Genres", s => s.FeatGenre, (s, v) => s.FeatGenre = v, 0.25, 3, true, false, false),
        new("Mots-clés", s => s.FeatTag, (s, v) => s.FeatTag = v, 0.05, 2, true, false, true),
        new("Studios", s => s.FeatStudio, (s, v) => s.FeatStudio = v, 0.05, 2, true, false, true),
        new("Acteurs", s => s.FeatActor, (s, v) => s.FeatActor = v, 0.05, 2, true, false, true),
        new("Réalisateurs", s => s.FeatDirector, (s, v) => s.FeatDirector = v, 0.1, 3, true, false, true),
        new("Décennie", s => s.FeatDecade, (s, v) => s.FeatDecade = v, 0.05, 2, true, false, true),
        new("Film ou série", s => s.FeatKind, (s, v) => s.FeatKind = v, 0.05, 2, true, false, true),
        new("Langue originale", s => s.FeatLanguage, (s, v) => s.FeatLanguage = v, 0.05, 1.5, true, false, true),
        new("Pays de production", s => s.FeatCountry, (s, v) => s.FeatCountry = v, 0.05, 1, true, false, true),
    };

    /// <summary>Choix discrets explorés.</summary>
    public static IReadOnlyList<Choice> Choices { get; } = new List<Choice>
    {
        new("Décroissance", new object[] { DecayKind.Exponential, DecayKind.Power }, s => s.Decay, (s, v) => s.Decay = (DecayKind)v),
        new("Pondération du contenu", new object[] { WeightingScheme.TfIdf, WeightingScheme.Bm25 }, s => s.Weighting, (s, v) => s.Weighting = (WeightingScheme)v),
        new("Note utilisée", new object[] { RatingSource.Community, RatingSource.Critic, RatingSource.Best, RatingSource.Mean }, s => s.Rating, (s, v) => s.Rating = (RatingSource)v),
        new("Diversité", new object[] { DiversityMode.Fixed, DiversityMode.Personalized, DiversityMode.Calibrated }, s => s.Diversity, (s, v) => s.Diversity = (DiversityMode)v),
    };

    /// <summary>Clé des vecteurs : seuls ces réglages changent le contenu des vecteurs.</summary>
    private static string VecKey(EngineSettings s) => string.Join('|', s.Weighting, s.Bm25K1, s.Bm25B, s.FeatGenre, s.FeatTag, s.FeatStudio, s.FeatActor,
        s.FeatDirector, s.FeatWriter, s.FeatCreator, s.FeatDecade, s.FeatKind, s.FeatLanguage, s.FeatCountry, s.Actors, s.StudiosKept);

    /// <summary>Optimiseur : recherche par coordonnées, grille puis section dorée par paramètre, garde-fou des deux moitiés.</summary>
    private sealed class Search
    {
        private const double Eps = 1e-4;
        private readonly Dataset _ds;
        private readonly List<Case> _cases;
        private readonly int[][] _halves;
        private readonly Dictionary<string, SimilarityIndex> _sims;
        private readonly Dictionary<string, EvalResult> _memo = new(StringComparer.Ordinal);
        private readonly CancellationToken _ct;
        private readonly Action _tick;

        public Search(Dataset ds, List<Case> cases, Dictionary<string, SimilarityIndex> sims, Action tick, CancellationToken ct)
        {
            _ds = ds;
            _cases = cases;
            _sims = sims;
            _tick = tick;
            _ct = ct;
            var side = Folds(cases);
            _halves = new[]
            {
                Enumerable.Range(0, cases.Count).Where(i => side[cases[i].Ev.User] == 0).ToArray(),
                Enumerable.Range(0, cases.Count).Where(i => side[cases[i].Ev.User] == 1).ToArray(),
            };
        }

        public List<CalibrationStep> Steps { get; } = new();

        public EvalResult Eval(EngineSettings s)
        {
            s = s.Sanitized();
            var key = System.Text.Json.JsonSerializer.Serialize(s);
            if (_memo.TryGetValue(key, out var r))
            {
                return r;
            }

            var vk = VecKey(s);
            if (!_sims.TryGetValue(vk, out var sim))
            {
                _sims[vk] = sim = new SimilarityIndex(Core.BuildVectors(_ds.Titles, s));
            }

            _tick();
            return _memo[key] = EvaluateRanks(_ds, sim, _cases, s, _ct);
        }

        public double Half(EvalResult r, int h)
        {
            double sw = 0, sg = 0;
            foreach (var i in _halves[h])
            {
                sw += _cases[i].Weight;
                sg += _cases[i].Weight * r.PerEventGain[i];
            }

            return sw > 0 ? sg / sw : 0;
        }

        private bool Better(EvalResult cand, EvalResult best)
            => cand.Gain > best.Gain + Eps && Half(cand, 0) >= Half(best, 0) - 1e-12 && Half(cand, 1) >= Half(best, 1) - 1e-12;

        public (EngineSettings S, EvalResult R) Run(EngineSettings start, int passes, int refine)
        {
            var best = start.Sanitized();
            var bestRes = Eval(best);
            for (int pass = 0; pass < passes; pass++)
            {
                bool changed = false;
                foreach (var c in Choices)
                {
                    foreach (var v in c.Values)
                    {
                        var cand = best.Clone();
                        c.Set(cand, v);
                        var r = Eval(cand);
                        if (Better(r, bestRes))
                        {
                            (best, bestRes, changed) = (cand.Sanitized(), r, true);
                            Steps.Add(new CalibrationStep(c.Name, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, r.Hit10, r.Hit20, true));
                        }
                    }
                }

                foreach (var k in Knobs)
                {
                    if (k.When is not null && !k.When(best))
                    {
                        continue;
                    }

                    var (s, r, moved) = Tune(k, best, bestRes, refine);
                    if (moved)
                    {
                        (best, bestRes, changed) = (s, r, true);
                        Steps.Add(new CalibrationStep(k.Name, k.Get(s).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), r.Hit10, r.Hit20, true));
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            return (best, bestRes);
        }

        // grille de 5 points (+ valeur actuelle, + 0 si permis), puis section dorée autour du meilleur point
        private (EngineSettings, EvalResult, bool) Tune(Knob k, EngineSettings best, EvalResult bestRes, int refine)
        {
            double To(double v) => k.Log ? Math.Log(v) : v;
            double From(double x) => k.Log ? Math.Exp(x) : x;
            double Fix(double v) => k.Integer ? Math.Round(Math.Clamp(v, k.Lo, k.Hi)) : Math.Clamp(v, k.Lo, k.Hi);
            EngineSettings With(double v)
            {
                var c = best.Clone();
                k.Set(c, v);
                return c;
            }

            double cur = k.Get(best);
            var tried = new SortedDictionary<double, EvalResult>();
            EvalResult At(double v)
            {
                v = v == 0 ? 0 : Fix(v);
                if (!tried.TryGetValue(v, out var r))
                {
                    tried[v] = r = Eval(With(v));
                }

                return r;
            }

            double a = To(k.Lo), b = To(k.Hi);
            for (int g = 0; g < 5; g++)
            {
                At(From(a + ((b - a) * g / 4.0)));
            }

            if (cur > 0)
            {
                At(cur);
            }

            if (k.TryZero)
            {
                At(0);
            }

            // section dorée entre les voisins du meilleur point de la grille (hors 0)
            var pos = tried.Where(kv => kv.Key > 0).OrderBy(kv => kv.Key).ToList();
            int bi = pos.Select((kv, i) => (kv.Value.Gain, i)).OrderByDescending(x => x.Gain).First().i;
            double lo = To(pos[Math.Max(0, bi - 1)].Key), hi = To(pos[Math.Min(pos.Count - 1, bi + 1)].Key);
            const double Phi = 0.6180339887498949;
            double x1 = hi - (Phi * (hi - lo)), x2 = lo + (Phi * (hi - lo));
            for (int it = 0; it < refine && hi - lo > 1e-6; it++)
            {
                var f1 = At(From(x1)).Gain;
                var f2 = At(From(x2)).Gain;
                if (f1 >= f2)
                {
                    hi = x2;
                    x2 = x1;
                    x1 = hi - (Phi * (hi - lo));
                }
                else
                {
                    lo = x1;
                    x1 = x2;
                    x2 = lo + (Phi * (hi - lo));
                }
            }

            var winner = tried.OrderByDescending(kv => kv.Value.Gain).First();
            if (winner.Key != cur && Better(winner.Value, bestRes))
            {
                var s = With(winner.Key).Sanitized();
                return (s, winner.Value, true);
            }

            return (best, bestRes, false);
        }
    }

    /// <summary>Historique nécessaire à une méthode : niveau (« suffisant », « limite », « insuffisant ») et explication.</summary>
    public static (string Level, string Message) Sufficiency(CalibrationMethod m, int cases)
    {
        // règle usuelle : 10 à 20 cas par paramètre réglé
        (int Params, int Good, int Min) need = m switch
        {
            CalibrationMethod.ChoiceModel => (ChoiceModel.Weights.Count, ChoiceModel.Weights.Count * 10, ChoiceModel.Weights.Count * 5),
            CalibrationMethod.Coordinate => (Knobs.Count + Choices.Count, (Knobs.Count + Choices.Count) * 20, (Knobs.Count + Choices.Count) * 6),
            CalibrationMethod.Neural => (NeuralNet.Size, NeuralNet.Size * 10, NeuralNet.Size * 3),
            _ => (ChoiceModel.Weights.Count, ChoiceModel.Weights.Count * 10, MinEvents),
        };
        if (cases < MinEvents || cases < need.Min)
        {
            return ("insuffisant", $"{cases} cas pour {need.Params} réglages : pas assez d'historique, le calibrage ne proposera rien de fiable (il faut au moins {Math.Max(MinEvents, need.Min)} cas, idéalement {need.Good}).");
        }

        return cases < need.Good
            ? ("limite", $"{cases} cas pour {need.Params} réglages : juste assez pour essayer, mais un gain devra être net pour être recommandé (idéalement {need.Good} cas).")
            : ("suffisant", $"{cases} cas pour {need.Params} réglages : historique suffisant.");
    }

    /// <summary>
    /// Calibrage : chaque méthode retenue propose un réglage appris sur tout l'historique, puis est jugée en validation
    /// croisée par comptes (apprise sur une moitié des comptes, mesurée sur l'autre). En automatique, on garde la méthode
    /// qui se généralise le mieux. Un réglage n'est recommandé que si ce gain hors échantillon est sûr (IC95 &gt; 0).
    /// Rien n'est appliqué ici.
    /// </summary>
    public static CalibrationReport Calibrate(Dataset ds, EngineSettings current, IProgress<double>? progress = null, CancellationToken ct = default,
        int passes = 2, int refine = 4, CalibrationMethod method = CalibrationMethod.Auto)
    {
        current = current.Sanitized();
        var cases = Cases(ds);
        var sims = new Dictionary<string, SimilarityIndex>(StringComparer.Ordinal);
        bool coord = method is CalibrationMethod.Auto or CalibrationMethod.Coordinate;
        bool choice = method is CalibrationMethod.Auto or CalibrationMethod.ChoiceModel;
        bool neural = method is CalibrationMethod.Auto or CalibrationMethod.Neural;
        int perRun = (Choices.Sum(c => c.Values.Length) + (Knobs.Count * (7 + refine * 2))) * passes;
        int perChoice = (ChoiceModel.Kappas.Count * 2) + 2;
        int perNeural = (NeuralNet.Kappas.Count * 2) + 2;
        int budget = (coord ? perRun * 2 : 0) + (choice ? perChoice * 3 : 0) + (neural ? perNeural * 3 : 0) + 1, done = 0;
        void Tick() => progress?.Report(Math.Min(99.0, 100.0 * ++done / budget));

        var full = new Search(ds, cases, sims, Tick, ct);
        var cur = full.Eval(current);
        if (cases.Count < MinEvents)
        {
            return new CalibrationReport
            {
                Current = cur,
                Proposed = cur,
                ProposedSettings = current,
                Steps = new List<CalibrationStep>(),
                Recommended = false,
                Method = method,
                Verdict = $"Seulement {cases.Count} cas exploitables (minimum {MinEvents}) : pas assez d'historique pour proposer un réglage fiable.",
            };
        }

        var fold = Folds(cases);
        var baseG = cur.PerEventGain;
        var results = new List<MethodResult>();

        // une méthode = (réglage proposé sur tout l'historique, réglage appris sur une partie des cas)
        MethodResult Judge(CalibrationMethod m, string name, EngineSettings proposed, List<CalibrationStep> steps, Func<List<Case>, EngineSettings> learnOn)
        {
            var res = full.Eval(proposed);
            var oof = new double[cases.Count];
            Array.Copy(baseG, oof, oof.Length);
            for (int f = 0; f < 2; f++)
            {
                var train = cases.Where(c => fold[c.Ev.User] == f).ToList();
                var testIdx = Enumerable.Range(0, cases.Count).Where(i => fold[cases[i].Ev.User] != f).ToList();
                if (train.Count < 10 || testIdx.Count == 0)
                {
                    continue;
                }

                var learned = learnOn(train);
                var r = new Search(ds, testIdx.Select(i => cases[i]).ToList(), sims, Tick, ct).Eval(learned);
                for (int k = 0; k < testIdx.Count; k++)
                {
                    oof[testIdx[k]] = r.PerEventGain[k];
                }
            }

            var all = Enumerable.Range(0, cases.Count).ToList();
            return new MethodResult
            {
                Method = m,
                Name = name,
                Settings = proposed,
                Result = res,
                Steps = steps,
                CvDelta = Weighted(cases, all, i => oof[i] - baseG[i]),
                Ci = BootstrapDelta(cases, oof, baseG),
                Sufficiency = Sufficiency(m, cases.Count).Level,
            };
        }

        if (coord)
        {
            var (best, _) = full.Run(current, passes, refine);
            results.Add(Judge(CalibrationMethod.Coordinate, "Recherche par curseurs", best, full.Steps.ToList(),
                train => new Search(ds, train, sims, Tick, ct).Run(current, passes, refine).S));
        }

        if (choice)
        {
            // un seul jeu de vecteurs : le modèle de choix n'apprend que les poids du score, pas le contenu
            var vk = VecKey(current);
            if (!sims.TryGetValue(vk, out var sim))
            {
                sims[vk] = sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, current));
            }

            var kappa = ChoiceModel.SelectKappa(ds, sim, cases, current, Tick, ct);
            var learned = ChoiceModel.Learn(ds, sim, cases, current, kappa, ct);
            Tick();
            var steps = ChoiceModel.Weights.Where(w => Math.Abs(w.Get(learned) - w.Get(current)) >= 1e-3)
                .Select(w => new CalibrationStep(w.Name, w.Get(learned).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), 0, 0, true)).ToList();
            results.Add(Judge(CalibrationMethod.ChoiceModel, "Modèle de choix appris", learned, steps,
                train => ChoiceModel.Learn(ds, sim, train, current, ChoiceModel.SelectKappa(ds, sim, train, current, Tick, ct), ct)));
        }

        if (neural)
        {
            var vk = VecKey(current);
            if (!sims.TryGetValue(vk, out var sim))
            {
                sims[vk] = sim = new SimilarityIndex(Core.BuildVectors(ds.Titles, current));
            }

            var kappa = NeuralNet.SelectKappa(ds, sim, cases, current, Tick, ct);
            var learned = NeuralNet.With(current, NeuralNet.Train(ds, sim, cases, current, kappa, ct));
            Tick();
            var steps = new List<CalibrationStep>
            {
                new("Correction apprise (réseau de neurones)", $"{NeuralNet.Size} paramètres, régularisation {kappa.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 0, 0, true),
            };
            results.Add(Judge(CalibrationMethod.Neural, "Réseau de neurones (expérimental)", learned, steps,
                train => NeuralNet.With(current, NeuralNet.Train(ds, sim, train, current, NeuralNet.SelectKappa(ds, sim, train, current, Tick, ct), ct))));
        }

        progress?.Report(100);
        foreach (var r in results)
        {
            r.NoChange = System.Text.Json.JsonSerializer.Serialize(r.Settings.Sanitized()) == System.Text.Json.JsonSerializer.Serialize(current);
            r.Recommended = !r.NoChange && r.Result.Gain > cur.Gain + 1e-9 && r.CvDelta > 0 && r.Ci.Low > 0
                            && r.CvDelta / Math.Max(cur.Gain, 1e-9) >= MinPracticalGain;
        }

        // la méthode retenue : recommandée d'abord, puis meilleure généralisation (validation croisée)
        var pick = results.OrderByDescending(r => r.Recommended).ThenByDescending(r => r.CvDelta).First();
        string Pct(double x) => (x * 100).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture) + " %";
        double rel(double x) => x / Math.Max(cur.Gain, 1e-9);
        bool improved = pick.Result.Gain > cur.Gain + 1e-9;
        string who = results.Count > 1 ? $"{pick.Name} (la meilleure des {results.Count} méthodes) : " : string.Empty;
        bool sureButTiny = !pick.Recommended && improved && pick.CvDelta > 0 && pick.Ci.Low > 0;
        string verdict = pick.NoChange || !improved
            ? who + "aucun réglage ne fait mieux que l'actuel : on garde l'actuel."
            : sureButTiny
                ? who + $"gain réel mais négligeable ({Pct(rel(pick.CvDelta))} en validation croisée, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}], moins de {Pct(MinPracticalGain).TrimStart('+')}) : rien de visible, inutile de l'appliquer."
            : pick.Recommended
                ? who + $"gain probable et qui se généralise : appris sur une moitié des comptes, il fait mieux sur l'autre ({Pct(rel(pick.CvDelta))} de qualité de classement, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}])."
                : who + $"gain sur cet historique ({Pct(rel(pick.Result.Gain - cur.Gain))}), mais il ne se généralise pas d'une moitié des comptes à l'autre (validation croisée {Pct(rel(pick.CvDelta))}, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}]) : application déconseillée.";
        return new CalibrationReport
        {
            Current = cur,
            Proposed = pick.Result,
            ProposedSettings = pick.Settings,
            DeltaCi = pick.Ci,
            Steps = pick.Steps,
            Recommended = pick.Recommended,
            Verdict = verdict,
            CvDelta = pick.CvDelta,
            Method = pick.Method,
            Methods = results,
        };
    }

    private static double Weighted(IReadOnlyList<Case> cases, IEnumerable<int> idx, Func<int, double> f)
    {
        double sw = 0, s = 0;
        foreach (var i in idx)
        {
            sw += cases[i].Weight;
            s += cases[i].Weight * f(i);
        }

        return sw > 0 ? s / sw : 0;
    }

    /// <summary>IC95 de l'écart pondéré moyen (a − b), bootstrap en tirant des comptes.</summary>
    public static (double Low, double High) BootstrapDelta(IReadOnlyList<Case> cases, double[] a, double[] b, int rounds = 2000, int seed = 0)
    {
        var users = cases.Select(c => c.Ev.User).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (users.Count == 0)
        {
            return (0, 0);
        }

        var by = users.ToDictionary(u => u, u => Enumerable.Range(0, cases.Count).Where(i => cases[i].Ev.User == u).ToArray(), StringComparer.Ordinal);
        var rng = new Random(seed);
        var d = new double[rounds];
        for (int r = 0; r < rounds; r++)
        {
            double sw = 0, s = 0;
            for (int k = 0; k < users.Count; k++)
            {
                foreach (var i in by[users[rng.Next(users.Count)]])
                {
                    sw += cases[i].Weight;
                    s += cases[i].Weight * (a[i] - b[i]);
                }
            }

            d[r] = sw > 0 ? s / sw : 0;
        }

        Array.Sort(d);
        return (d[(int)(rounds * 0.025)], d[(int)(rounds * 0.975)]);
    }
}
