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
/// <summary>Jusqu'où une méthode peut s'éloigner du réglage actuel, selon la validation croisée.</summary>
public enum CalibrationPrudence
{
    /// <summary>Automatique : prudent.</summary>
    Auto,

    /// <summary>L'étape la meilleure en validation croisée, même de peu.</summary>
    Best,

    /// <summary>La plus courte à moins d'une erreur type de la meilleure (règle 1-SE).</summary>
    Prudent,

    /// <summary>La plus courte à moins de deux erreurs types de la meilleure.</summary>
    VeryPrudent
}

/// <summary>Réglages du calibrage lui-même ; 0 (ou Auto) = valeur automatique.</summary>
public sealed class CalibrationOptions
{
    /// <summary>Groupes de comptes de la validation croisée (2 à 10) ; 0 = auto (√comptes, voir <see cref="Calibrator.FoldCount(int)"/>).</summary>
    public int Folds { get; set; }

    /// <summary>Groupes pour la recherche par curseurs et le réseau (longs) ; 0 = auto (√(2 × groupes), voir <see cref="Calibrator.SlowFoldCount"/>).</summary>
    public int SlowFolds { get; set; }

    /// <summary>Groupes pour choisir la régularisation à l'intérieur de chaque groupe ; 0 = auto (2).</summary>
    public int InnerFolds { get; set; }

    /// <summary>Choix de l'étape du chemin.</summary>
    public CalibrationPrudence Prudence { get; set; }

    /// <summary>Passes de la recherche par curseurs ; 0 = auto (2).</summary>
    public int Passes { get; set; }

    /// <summary>Affinages (section dorée) par réglage ; 0 = auto (4).</summary>
    public int Refine { get; set; }

    /// <summary>Gain minimal en validation croisée pour recommander (%) ; 0 = auto (1 %).</summary>
    public double MinGainPercent { get; set; }

    /// <summary>Groupes effectifs pour ces cas.</summary>
    public int FoldsFor(IReadOnlyList<Calibrator.Case> cases)
    {
        int accounts = cases.Select(c => c.Ev.User).Distinct(StringComparer.Ordinal).Count();
        int k = Folds >= 2 ? Folds : Calibrator.FoldCount(cases);
        return Math.Clamp(k, 2, Math.Max(2, Math.Min(10, accounts)));
    }

    /// <summary>Groupes effectifs des méthodes longues.</summary>
    public int SlowFoldsFor(IReadOnlyList<Calibrator.Case> cases)
    {
        int k = FoldsFor(cases);
        return SlowFolds >= 2 ? Math.Min(SlowFolds, k) : Calibrator.SlowFoldCount(k);
    }

    /// <summary>Groupes effectifs du choix de régularisation imbriqué.</summary>
    public int InnerFoldsEff => InnerFolds >= 2 ? Math.Min(InnerFolds, 10) : 2;

    /// <summary>Erreurs types tolérées sous la meilleure étape.</summary>
    public double SeMultiplier => Prudence switch { CalibrationPrudence.Best => 0, CalibrationPrudence.VeryPrudent => 2, _ => 1 };

    /// <summary>Passes effectives.</summary>
    public int PassesEff => Passes >= 1 ? Math.Min(Passes, 5) : 2;

    /// <summary>Affinages effectifs.</summary>
    public int RefineEff => Refine >= 1 ? Math.Min(Refine, 12) : 4;

    /// <summary>Gain minimal effectif (part, pas %).</summary>
    public double MinGain => MinGainPercent > 0 ? Math.Min(MinGainPercent, 50) / 100.0 : Calibrator.MinPracticalGain;
}

public sealed class CalibrationReport
{
    /// <summary>Groupes de comptes de la validation croisée (effectifs).</summary>
    public int Folds { get; init; }

    /// <summary>Gain minimal pour recommander (part).</summary>
    public double MinGain { get; init; } = Calibrator.MinPracticalGain;

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

    /// <summary>Chemin de la méthode : écart de qualité avec l'actuel, sur tout l'historique, à chaque étape (0 = actuel).</summary>
    public double[] FitPath { get; init; } = Array.Empty<double>();

    /// <summary>Même chose en validation croisée : appris sans chaque groupe de comptes, mesuré sur lui.</summary>
    public double[] CvPath { get; init; } = Array.Empty<double>();

    /// <summary>Étape retenue (règle 1-SE ; 0 = garder l'actuel).</summary>
    public int Picked { get; init; }
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

    /// <summary>
    /// Titre déjà voulu avant d'être commencé (demandé ou en liste de suivi) : le compte l'avait trouvé seul ; en vrai, il
    /// n'est pas proposé (déjà connu) mais placé en tête à son arrivée. Ce n'est pas un choix de classement : pas un cas.
    /// </summary>
    private static bool WantedBefore(Interaction x) => (x.Requested > 0 && x.Requested < x.Start) || (x.Watchlisted > 0 && x.Watchlisted < x.Start);

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
                if (i >= MinTrain && ad[i].Start != 0 && !WantedBefore(ad[i]))
                {
                    ev.Add(new EvalEvent(u, ad[i].Title, ad[i].Start));
                }
            }
        }

        return ev;
    }

    /// <summary>
    /// Historique connu juste avant <paramref name="now"/>, sans rien de postérieur : un titre commencé avant mais
    /// continué après est vu tel qu'il était (en cours : dernière activité ramenée à <paramref name="now"/>, film pas
    /// encore fini) ; une demande ou une liste de suivi compte dès sa date, même sans visionnage ; plus tard, elle
    /// n'existe pas encore. (Favoris et notes n'ont pas de date : gardés tels quels pour un titre déjà commencé.)
    /// </summary>
    public static Dictionary<string, Dictionary<string, Interaction>> Snapshot(Dataset ds, double now)
    {
        var res = new Dictionary<string, Dictionary<string, Interaction>>(ds.Users.Count, StringComparer.Ordinal);
        foreach (var (u, h) in ds.Users)
        {
            var m = new Dictionary<string, Interaction>(StringComparer.Ordinal);
            foreach (var (k, x) in h)
            {
                bool started = x.Backfill || (x.Start != 0 && x.Start < now);
                bool asked = x.Requested > 0 && x.Requested < now, listed = x.Watchlisted > 0 && x.Watchlisted < now;
                if (!started && !asked && !listed)
                {
                    continue;
                }

                bool ongoing = started && !x.Backfill && x.Last > now;
                if (started && !ongoing && (x.Requested == 0 || asked) && (x.Watchlisted == 0 || listed))
                {
                    m[k] = x; // rien de postérieur : tel quel
                    continue;
                }

                var c = x.Copy();
                if (!started)
                {
                    // seulement voulu à cette date : rien du visionnage ni de l'avis qui viendront
                    (c.Start, c.Last, c.Completion, c.Played, c.Episodes, c.Plays, c.Rewatch, c.Hours) = (0, 0, 0, false, 0, 0, 0, 0);
                    (c.Favorite, c.FavSeasons, c.FavEpisodes, c.Rating, c.Hidden) = (false, 0, 0, null, false);
                }
                else if (ongoing)
                {
                    c.Last = now;
                    if (ds.Titles.TryGetValue(k, out var t) && t.Kind == TitleKind.Movie)
                    {
                        (c.Played, c.Completion, c.Rewatch) = (false, 0, 0); // film en cours à cette date
                    }
                }

                c.Requested = asked ? x.Requested : 0;
                c.Watchlisted = listed ? x.Watchlisted : 0;
                m[k] = c;
            }

            res[u] = m;
        }

        return res;
    }

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
                if (prior >= MinPrior && !WantedBefore(x))
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
    /// Contexte de chaque cas (historique à cette date, signaux, « vus ensemble », candidats), gardé tant que les réglages
    /// qui le changent ne bougent pas : la recherche essaie surtout des poids du score, qui n'y touchent pas. Une seule
    /// version gardée à la fois (mémoire bornée).
    /// </summary>
    public sealed class ContextCache
    {
        // réglages qui ne servent qu'au score ou au classement (jamais aux signaux ni aux « vus ensemble ») : ignorés dans
        // la clé ; en oublier un ne fait que manquer le cache
        private static readonly System.Reflection.PropertyInfo[] ScoreOnly = new[]
        {
            nameof(EngineSettings.WCentroid), nameof(EngineSettings.WMaxSim), nameof(EngineSettings.WCollab), nameof(EngineSettings.WNegative),
            nameof(EngineSettings.WPopularity), nameof(EngineSettings.WQuality), nameof(EngineSettings.WFresh), nameof(EngineSettings.WTmdbAgreement),
            nameof(EngineSettings.WRelease), nameof(EngineSettings.WHousehold), nameof(EngineSettings.WShortTerm), nameof(EngineSettings.WTimeOfDay),
            nameof(EngineSettings.WImpression), nameof(EngineSettings.WNeural), nameof(EngineSettings.Neural), nameof(EngineSettings.Mmr),
            nameof(EngineSettings.Diversity), nameof(EngineSettings.CalibrationStrength), nameof(EngineSettings.GateTop), nameof(EngineSettings.SagaMax),
            nameof(EngineSettings.FreshDays), nameof(EngineSettings.ReleaseDays), nameof(EngineSettings.Rating), nameof(EngineSettings.RatingPriorVotes),
            nameof(EngineSettings.HouseholdPrior), nameof(EngineSettings.ShortTermTitles),
        }.Select(n => typeof(EngineSettings).GetProperty(n)!).ToArray();

        private static readonly EngineSettings Defaults = new();
        private readonly object _lock = new();
        private string? _key;
        private System.Collections.Concurrent.ConcurrentDictionary<EvalEvent, (Context Ctx, List<string> Cands)> _m = new();

        internal static string KeyOf(EngineSettings s)
        {
            var c = s.Clone();
            foreach (var p in ScoreOnly)
            {
                p.SetValue(c, p.GetValue(Defaults));
            }

            return System.Text.Json.JsonSerializer.Serialize(c);
        }

        internal (Context Ctx, List<string> Cands) Get(Dataset ds, EvalEvent e, EngineSettings s, string key)
        {
            System.Collections.Concurrent.ConcurrentDictionary<EvalEvent, (Context, List<string>)> m;
            lock (_lock)
            {
                if (_key != key)
                {
                    (_key, _m) = (key, new());
                }

                m = _m;
            }

            return m.GetOrAdd(e, ev =>
            {
                var hist = Snapshot(ds, ev.T0 - 1);
                return (new Context(ds, ev.T0 - 1, hist, s), Candidates(ds, hist[ev.User], ev.T0));
            });
        }
    }

    /// <summary>
    /// Évalue un réglage sur des cas, avec une mesure fine : le RANG du titre adopté parmi tous les candidats (liste
    /// finale pour les 20 premiers, score au-delà), gain = 1 / log2(1 + rang). Bien plus de puissance statistique
    /// qu'un simple « dans le top 10 ou non ».
    /// </summary>
    public static EvalResult EvaluateRanks(Dataset ds, SimilarityIndex sim, IReadOnlyList<Case> cases, EngineSettings s, CancellationToken ct = default,
        ContextCache? contexts = null)
    {
        string? ckey = contexts is null ? null : ContextCache.KeyOf(s);
        var vec = sim.Vectors;
        var h10 = new bool[cases.Count];
        var h20 = new bool[cases.Count];
        var gain = new double[cases.Count];
        // cas indépendants : en parallèle (réglage « Threads », par défaut la moitié des cœurs)
        var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Cpu.Degree, CancellationToken = ct };
        System.Threading.Tasks.Parallel.For(0, cases.Count, po, i =>
        {
            var (u, target, t0) = cases[i].Ev;
            Context ctx;
            List<string> cands;
            if (contexts is not null)
            {
                (ctx, cands) = contexts.Get(ds, cases[i].Ev, s, ckey!);
            }
            else
            {
                var hist = Snapshot(ds, t0 - 1);
                ctx = new Context(ds, t0 - 1, hist, s);
                cands = Candidates(ds, hist[u], t0);
            }

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
    public static Dictionary<string, int> Folds(IReadOnlyList<Case> cases) => Folds(cases, 2);

    /// <summary>Groupes de comptes pour la validation croisée (attribués à tour de rôle, des comptes les plus actifs aux moins actifs).</summary>
    public static Dictionary<string, int> Folds(IReadOnlyList<Case> cases, int k)
    {
        var users = cases.GroupBy(c => c.Ev.User).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        return users.Select((u, i) => (u, i % Math.Max(2, k))).ToDictionary(x => x.u, x => x.Item2, StringComparer.Ordinal);
    }

    /// <summary>
    /// Nombre de groupes de comptes de la validation croisée, fonction continue du nombre de comptes n : k = √n arrondi
    /// (autant de groupes que de comptes par groupe : chaque apprentissage voit 1 − 1/√n des comptes, et chaque mesure
    /// porte sur √n comptes), entre 2 et 10, jamais plus que n. 4 comptes → 2, 9 → 3, 16 → 4, 25 → 5, 100 → 10.
    /// </summary>
    public static int FoldCount(IReadOnlyList<Case> cases)
        => FoldCount(cases.Select(c => c.Ev.User).Distinct(StringComparer.Ordinal).Count());

    /// <summary>Voir <see cref="FoldCount(IReadOnlyList{Case})"/>.</summary>
    public static int FoldCount(int accounts)
        => Math.Clamp((int)Math.Round(Math.Sqrt(Math.Max(0, accounts)), MidpointRounding.AwayFromZero), 2, Math.Max(2, Math.Min(10, accounts)));

    /// <summary>
    /// Groupes des méthodes longues (recherche par curseurs, réseau : tout l'apprentissage refait par groupe), fonction
    /// continue des groupes k de la validation croisée : √(2k) arrondi, entre 2 et k. k = 2 → 2, 5 → 3, 10 → 4.
    /// </summary>
    public static int SlowFoldCount(int folds)
        => Math.Clamp((int)Math.Round(Math.Sqrt(2.0 * Math.Max(2, folds)), MidpointRounding.AwayFromZero), 2, Math.Max(2, folds));

    /// <summary>
    /// Choisit une valeur par validation croisée par comptes : pour chaque valeur, apprise sans un groupe et mesurée sur
    /// lui (gain moyen pondéré). À égalité ou sans assez de cas, la dernière de la liste (la plus prudente).
    /// </summary>
    public static T SelectByCv<T>(IReadOnlyList<Case> cases, IReadOnlyList<T> values, Func<List<Case>, List<Case>, T, double> trainTest, int folds = 0)
    {
        int k = folds >= 2 ? folds : FoldCount(cases);
        var fold = Folds(cases, k);
        var parts = Enumerable.Range(0, k).Select(f => cases.Where(c => fold[c.Ev.User] == f).ToList()).ToArray();
        if (parts.Any(p => p.Count < 5) || cases.Count - parts.Max(p => p.Count) < 10)
        {
            return values[^1];
        }

        T best = values[^1];
        double bestG = double.NegativeInfinity;
        foreach (var v in values.Reverse())
        {
            double sw = 0, sg = 0;
            for (int f = 0; f < k; f++)
            {
                var train = cases.Where(c => fold[c.Ev.User] != f).ToList();
                double w = parts[f].Sum(c => c.Weight);
                sw += w;
                sg += trainTest(train, parts[f], v) * w;
            }

            double g = sw > 0 ? sg / sw : 0;
            if (g > bestG + 1e-9)
            {
                (bestG, best) = (g, v);
            }
        }

        return best;
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

    /// <summary>
    /// Index des produits scalaires par jeu de vecteurs, en nombre borné : chaque valeur essayée d'un réglage de contenu
    /// donne d'autres vecteurs, et garder tous les index fait exploser la mémoire. On garde celui des réglages actuels
    /// (toujours resservi) et les quelques derniers utilisés ; un index écarté est simplement recalculé s'il revient.
    /// </summary>
    public sealed class SimCache
    {
        private const int Keep = 3;
        private readonly string _pinned;
        private readonly Dictionary<string, (SimilarityIndex Sim, long Used)> _m = new(StringComparer.Ordinal);
        private long _clock;

        public SimCache(string pinned) => _pinned = pinned;

        public SimilarityIndex Get(string key, Func<Dictionary<string, SparseVector>> vectors)
        {
            lock (_m)
            {
                if (_m.TryGetValue(key, out var e))
                {
                    _m[key] = (e.Sim, ++_clock);
                    return e.Sim;
                }
            }

            var sim = new SimilarityIndex(vectors());
            lock (_m)
            {
                _m[key] = (sim, ++_clock);
                while (_m.Count > Keep + 1)
                {
                    var old = _m.Where(kv => kv.Key != _pinned && kv.Key != key).OrderBy(kv => kv.Value.Used).First().Key;
                    _m.Remove(old);
                }
            }

            return sim;
        }
    }

    /// <summary>Optimiseur : recherche par coordonnées, grille puis section dorée par paramètre, garde-fou des deux moitiés.</summary>
    private sealed class Search
    {
        private const double Eps = 1e-4;
        private readonly Dataset _ds;
        private readonly List<Case> _cases;
        private readonly int[][] _halves;
        private readonly SimCache _sims;
        private readonly ContextCache? _contexts;
        private readonly Dictionary<string, EvalResult> _memo = new(StringComparer.Ordinal);
        private readonly CancellationToken _ct;
        private readonly Action _tick;

        public Search(Dataset ds, List<Case> cases, SimCache sims, Action tick, CancellationToken ct, ContextCache? contexts = null)
        {
            _contexts = contexts;
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

        /// <summary>Réglage après chaque changement gardé, dans l'ordre (le chemin de la recherche).</summary>
        public List<EngineSettings> Path { get; } = new();

        public EvalResult Eval(EngineSettings s)
        {
            s = s.Sanitized();
            var key = System.Text.Json.JsonSerializer.Serialize(s);
            if (_memo.TryGetValue(key, out var r))
            {
                return r;
            }

            var vk = VecKey(s);
            var sim = _sims.Get(vk, () => Core.BuildVectors(_ds.Titles, s));

            _tick();
            return _memo[key] = EvaluateRanks(_ds, sim, _cases, s, _ct, _contexts);
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
                            Path.Add(best);
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
                        Path.Add(best);
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
    /// croisée par comptes (apprise sans un groupe de comptes, mesurée sur lui, pour chaque groupe). En automatique, on garde la méthode
    /// qui se généralise le mieux. Un réglage n'est recommandé que si ce gain hors échantillon est sûr (IC95 &gt; 0).
    /// Rien n'est appliqué ici.
    /// </summary>
    public static CalibrationReport Calibrate(Dataset ds, EngineSettings current, IProgress<double>? progress = null, CancellationToken ct = default,
        int passes = 0, int refine = 0, CalibrationMethod method = CalibrationMethod.Auto, CalibrationOptions? options = null)
    {
        var opt = options ?? new CalibrationOptions();
        passes = passes > 0 ? passes : opt.PassesEff;
        refine = refine > 0 ? refine : opt.RefineEff;
        double minGain = opt.MinGain;
        current = current.Sanitized();
        var cases = Cases(ds);
        var sims = new SimCache(VecKey(current));
        bool coord = method is CalibrationMethod.Auto or CalibrationMethod.Coordinate;
        bool choice = method is CalibrationMethod.Auto or CalibrationMethod.ChoiceModel;
        bool neural = method is CalibrationMethod.Auto or CalibrationMethod.Neural;
        int kf = opt.FoldsFor(cases), kc = opt.SlowFoldsFor(cases), ki = opt.InnerFoldsEff;
        // avancement : nombre d'évaluations prévues (la recherche s'arrête souvent avant sa deuxième passe : estimation haute)
        int perRun = (Choices.Sum(c => c.Values.Length) + (Knobs.Count * (7 + refine * 2))) * passes;
        int perChoice = (ChoiceModel.Kappas.Count * kf) + 1 + (kf * ((ChoiceModel.Kappas.Count * ki) + 4));
        int perNeural = (NeuralNet.Kappas.Count * kc) + 1 + (kc * ((NeuralNet.Kappas.Count * ki) + 4));
        // une évaluation = 1 ; un apprentissage du modèle de choix ≈ 10 ; un entraînement du réseau ≈ 50 (durées mesurées)
        const double ChoiceUnit = 10, NeuralUnit = 50;
        double budget = (coord ? perRun * (1 + kc) : 0) + (choice ? perChoice * ChoiceUnit : 0) + (neural ? perNeural * NeuralUnit : 0) + 1, done = 0;
        void Report(double units)
        {
            done += units;
            progress?.Report(Math.Min(99.0, 100.0 * done / budget));
        }

        void Tick() => Report(1);
        void TickChoice() => Report(ChoiceUnit);
        void TickNeural() => Report(NeuralUnit);

        var contexts = new ContextCache();
        var full = new Search(ds, cases, sims, Tick, ct, contexts);
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

        var baseG = cur.PerEventGain;
        var results = new List<MethodResult>();

        // Une méthode = un chemin de réglages de plus en plus éloignés de l'actuel (recherche : chaque changement gardé ;
        // modèles appris : un quart, la moitié, tout le chemin vers le réglage appris). La validation croisée par comptes
        // (k groupes) mesure chaque étape du chemin appris sans le groupe, sur ce groupe ; on retient la plus courte
        // étape dont le gain est à moins d'une erreur type du meilleur (règle « 1-SE » : sans preuve nette, on bouge
        // moins), 0 = rester à l'actuel. Le réglage proposé est cette étape du chemin appris sur tout l'historique.
        MethodResult Judge(CalibrationMethod m, string name, IReadOnlyList<EngineSettings> fullPath, Func<int, List<CalibrationStep>> stepsUpTo,
            Func<List<Case>, IReadOnlyList<EngineSettings>> learnPath, int k)
        {
            var fold = Folds(cases, k);
            int n = fullPath.Count;
            var oof = Enumerable.Range(0, n + 1).Select(_ => (double[])baseG.Clone()).ToArray();
            for (int f = 0; f < k; f++)
            {
                var train = cases.Where(c => fold[c.Ev.User] != f).ToList();
                var testIdx = Enumerable.Range(0, cases.Count).Where(i => fold[cases[i].Ev.User] == f).ToList();
                if (train.Count < 10 || testIdx.Count == 0)
                {
                    continue;
                }

                var path = learnPath(train);
                var test = new Search(ds, testIdx.Select(i => cases[i]).ToList(), sims, Tick, ct, contexts);
                for (int step = 1; step <= n && path.Count > 0; step++)
                {
                    var r = test.Eval(path[Math.Min(step, path.Count) - 1]);   // mémorisé : une étape absente coûte rien
                    for (int t = 0; t < testIdx.Count; t++)
                    {
                        oof[step][testIdx[t]] = r.PerEventGain[t];
                    }
                }
            }

            var all = Enumerable.Range(0, cases.Count).ToList();
            var delta = oof.Select(o => Weighted(cases, all, i => o[i] - baseG[i])).ToArray();
            int best = Enumerable.Range(0, n + 1).OrderByDescending(x => delta[x]).ThenBy(x => x).First();
            var bestCi = BootstrapDelta(cases, oof[best], baseG);
            double se = opt.SeMultiplier * Math.Max(0, (bestCi.High - bestCi.Low) / (2 * 1.96));
            int pick = Enumerable.Range(0, n + 1).First(x => delta[x] >= delta[best] - se);
            var proposed = pick == 0 ? current : fullPath[pick - 1];
            var fit = new double[n + 1];
            for (int step = 1; step <= n; step++)
            {
                fit[step] = full.Eval(fullPath[step - 1]).Gain - cur.Gain;   // déjà mesuré pendant la recherche : mémorisé
            }

            return new MethodResult
            {
                FitPath = fit,
                CvPath = delta,
                Picked = pick,
                Method = m,
                Name = name,
                Settings = proposed,
                Result = full.Eval(proposed),
                Steps = pick == 0 ? new List<CalibrationStep>() : stepsUpTo(pick),
                CvDelta = delta[pick],
                Ci = pick == 0 ? (0, 0) : BootstrapDelta(cases, oof[pick], baseG),
                Sufficiency = Sufficiency(m, cases.Count).Level,
            };
        }

        // chemin d'un modèle appris : un quart, la moitié, puis tout le chemin de l'actuel vers le réglage appris
        static IReadOnlyList<EngineSettings> Shrunk(Func<double, EngineSettings> at) => new[] { at(0.25), at(0.5), at(1.0) };

        if (coord)
        {
            full.Run(current, passes, refine);
            var path = full.Path.ToList();
            var steps = full.Steps.ToList();
            results.Add(Judge(CalibrationMethod.Coordinate, "Recherche par curseurs", path, upTo => steps.Take(upTo).ToList(),
                train =>
                {
                    var sr = new Search(ds, train, sims, Tick, ct, contexts);
                    sr.Run(current, passes, refine);
                    return sr.Path;
                }, kc));   // une recherche complète par groupe : 3 groupes au plus (durée)
        }

        if (choice || neural)
        {
            // un seul jeu de vecteurs : modèle de choix et réseau n'apprennent que le score, pas le contenu
            var vk = VecKey(current);
            var sim = sims.Get(vk, () => Core.BuildVectors(ds.Titles, current));

            var cache = new ChoiceModel.FeatureCache(current);
            if (choice)
            {
                var kappa = ChoiceModel.SelectKappa(ds, sim, cases, current, TickChoice, ct, cache, folds: kf);
                var learned = ChoiceModel.Learn(ds, sim, cases, current, kappa, ct, cache: cache);
                TickChoice();
                List<CalibrationStep> StepsOf(EngineSettings s) => ChoiceModel.Weights.Where(w => Math.Abs(w.Get(s) - w.Get(current)) >= 1e-3)
                    .Select(w => new CalibrationStep(w.Name, w.Get(s).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), 0, 0, true)).ToList();
                var path = Shrunk(a => ChoiceModel.Toward(current, learned, a));
                results.Add(Judge(CalibrationMethod.ChoiceModel, "Modèle de choix appris", path, upTo => StepsOf(path[upTo - 1]),
                    train =>
                    {
                        var l = ChoiceModel.Learn(ds, sim, train, current, ChoiceModel.SelectKappa(ds, sim, train, current, TickChoice, ct, cache, folds: ki), ct, cache: cache);
                        return Shrunk(a => ChoiceModel.Toward(current, l, a));
                    }, kf));
            }

            if (neural)
            {
                var kappa = NeuralNet.SelectKappa(ds, sim, cases, current, TickNeural, ct, cache, folds: kc);
                var net = NeuralNet.Train(ds, sim, cases, current, kappa, ct, cache: cache);
                TickNeural();
                EngineSettings Scaled(double[] w, double a)
                {
                    var r = NeuralNet.With(current, w);
                    r.WNeural = a;
                    return r.Sanitized();
                }

                var path = Shrunk(a => Scaled(net, a));
                results.Add(Judge(CalibrationMethod.Neural, "Réseau de neurones (expérimental)", path,
                    upTo => new List<CalibrationStep>
                    {
                        new("Correction apprise (réseau de neurones)", $"{NeuralNet.Size} paramètres, régularisation {kappa.ToString(System.Globalization.CultureInfo.InvariantCulture)}, force {path[upTo - 1].WNeural.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}", 0, 0, true),
                    },
                    train =>
                    {
                        var w = NeuralNet.Train(ds, sim, train, current, NeuralNet.SelectKappa(ds, sim, train, current, TickNeural, ct, cache, folds: ki), ct, cache: cache);
                        return Shrunk(a => Scaled(w, a));
                    }, kc));   // entraînement long : 3 groupes au plus
            }
        }

        progress?.Report(100);
        foreach (var r in results)
        {
            r.NoChange = System.Text.Json.JsonSerializer.Serialize(r.Settings.Sanitized()) == System.Text.Json.JsonSerializer.Serialize(current);
            r.Recommended = !r.NoChange && r.Result.Gain > cur.Gain + 1e-9 && r.CvDelta > 0 && r.Ci.Low > 0
                            && r.CvDelta / Math.Max(cur.Gain, 1e-9) >= minGain;
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
                ? who + $"gain réel mais négligeable ({Pct(rel(pick.CvDelta))} en validation croisée, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}], moins de {Pct(minGain).TrimStart('+')}) : rien de visible, inutile de l'appliquer."
            : pick.Recommended
                ? who + $"gain probable et qui se généralise : appris sans chaque groupe de comptes, il fait mieux sur ce groupe ({Pct(rel(pick.CvDelta))} de qualité de classement, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}])."
                : who + $"gain sur cet historique ({Pct(rel(pick.Result.Gain - cur.Gain))}), mais il ne se généralise pas à des comptes qui n'ont pas servi à l'apprendre (validation croisée {Pct(rel(pick.CvDelta))}, IC95 [{Pct(rel(pick.Ci.Low))} ; {Pct(rel(pick.Ci.High))}]) : application déconseillée.";
        return new CalibrationReport
        {
            Folds = kf,
            MinGain = minGain,
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
