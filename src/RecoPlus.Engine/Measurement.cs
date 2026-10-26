using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>
/// Mesure de l'effet des recommandations, sans rien demander aux comptes : chaque calcul note, pour chaque compte, les
/// titres de la bibliothèque proposés dans ses rangées (rangée et place à la première proposition, première et dernière
/// proposition) ; l'historique de lecture dit ensuite ce qu'il en a fait.
/// <list type="bullet">
/// <item>« commencé sous 7 j » : première lecture dans les 7 jours qui suivent la première proposition ;</item>
/// <item>« fini sous 30 j » : vu en entier (film vu, série vue à 90 %) dans les 30 jours ;</item>
/// <item>« part des lectures venues d'une proposition » : parmi les titres commencés ces 30 derniers jours, ceux qui
/// étaient proposés au compte (dans les 14 jours précédant la lecture).</item>
/// </list>
/// Un titre proposé depuis moins de 7 (ou 30) jours n'entre pas encore dans le taux correspondant : il n'a pas eu le temps.
/// Fonctions pures : le plugin tient le registre, le moteur calcule le bilan.
/// </summary>
public static class Measurement
{
    /// <summary>Délai pour « commencé » (jours).</summary>
    public const double StartDays = 7;

    /// <summary>Délai pour « fini » (jours).</summary>
    public const double FinishDays = 30;

    /// <summary>Fenêtre de la part des lectures venues d'une proposition (jours).</summary>
    public const double RecentDays = 30;

    /// <summary>Une proposition compte pour une lecture si elle date de moins de … jours avant celle-ci.</summary>
    public const double CreditDays = 14;

    /// <summary>Une proposition plus revue depuis … jours est oubliée.</summary>
    public const double ForgetDays = 120;

    /// <summary>Tranches de places (bornes hautes incluses ; la dernière ouverte).</summary>
    public static readonly int[] Buckets = { 5, 10, 20 };

    /// <summary>Une proposition : rangée et place (1 = première) à la première fois, première et dernière fois (secondes Unix).</summary>
    public sealed class Entry
    {
        /// <summary>Rangée (« PourToi », « Films », « Series », « Nouveautes », « ParceQue »).</summary>
        public string Row { get; set; } = string.Empty;

        /// <summary>Place dans la rangée (1 = première).</summary>
        public int Position { get; set; }

        /// <summary>Première proposition.</summary>
        public double First { get; set; }

        /// <summary>Dernière proposition.</summary>
        public double Last { get; set; }
    }

    /// <summary>Compteurs d'un groupe (rangée ou tranche de places).</summary>
    public sealed class Counts
    {
        /// <summary>Titres proposés depuis au moins 7 j.</summary>
        public int Proposed7 { get; set; }

        /// <summary>… dont commencés dans les 7 j.</summary>
        public int Started7 { get; set; }

        /// <summary>Titres proposés depuis au moins 30 j.</summary>
        public int Proposed30 { get; set; }

        /// <summary>… dont finis dans les 30 j.</summary>
        public int Finished30 { get; set; }

        /// <summary>Taux de « commencé sous 7 j » (null sans recul).</summary>
        public double? StartRate => Proposed7 > 0 ? Math.Round((double)Started7 / Proposed7, 4) : null;

        /// <summary>Taux de « fini sous 30 j » (null sans recul).</summary>
        public double? FinishRate => Proposed30 > 0 ? Math.Round((double)Finished30 / Proposed30, 4) : null;
    }

    /// <summary>Bilan.</summary>
    public sealed class Report
    {
        /// <summary>Par rangée.</summary>
        public SortedDictionary<string, Counts> ByRow { get; } = new(StringComparer.Ordinal);

        /// <summary>Par tranche de places (« 1-5 », « 6-10 », « 11-20 », « 21+ »).</summary>
        public SortedDictionary<string, Counts> ByPosition { get; } = new(StringComparer.Ordinal);

        /// <summary>Toutes rangées.</summary>
        public Counts All { get; } = new();

        /// <summary>Titres commencés ces 30 derniers jours (tous comptes).</summary>
        public int RecentStarts { get; set; }

        /// <summary>… dont proposés au compte peu avant.</summary>
        public int RecentFromReco { get; set; }

        /// <summary>Part des lectures venues d'une proposition (null sans lecture récente).</summary>
        public double? RecentShare => RecentStarts > 0 ? Math.Round((double)RecentFromReco / RecentStarts, 4) : null;

        /// <summary>Recul : jours depuis la plus ancienne proposition notée.</summary>
        public double Days { get; set; }
    }

    /// <summary>Rangée ramenée à son type (« ParceQue2 » → « ParceQue »).</summary>
    public static string RowKind(string row)
        => row.StartsWith(HomeRows.ParceQue, StringComparison.Ordinal) ? HomeRows.ParceQue : row;

    /// <summary>Tranche de places.</summary>
    public static string Bucket(int position)
    {
        int lo = 1;
        foreach (var b in Buckets)
        {
            if (position <= b)
            {
                return lo + "-" + b;
            }

            lo = b + 1;
        }

        return lo + "+";
    }

    /// <summary>
    /// Note les rangées d'un calcul. Un titre déjà noté garde sa première rangée et sa première place (la proposition
    /// qui l'a fait connaître), sa dernière proposition avance. Les propositions trop anciennes sont oubliées.
    /// </summary>
    /// <param name="ledger">Registre du compte (modifié).</param>
    /// <param name="rows">Rangées calculées (titres dans l'ordre).</param>
    /// <param name="keep">Titres à noter (bibliothèque) ; les autres sont ignorés.</param>
    /// <param name="now">Maintenant (secondes Unix).</param>
    /// <param name="max">Taille maximale du registre (les plus anciennes dernières propositions partent d'abord).</param>
    public static void Record(Dictionary<string, Entry> ledger, IReadOnlyDictionary<string, List<string>> rows, Func<string, bool> keep, double now, int max = 5000)
    {
        // la meilleure place du calcul pour chaque titre (à égalité : Pour toi, puis les rangées dans l'ordre alphabétique)
        var best = new Dictionary<string, (string Row, int Pos)>(StringComparer.Ordinal);
        foreach (var (row, ids) in rows.OrderBy(kv => kv.Key == HomeRows.PourToi ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (keep(ids[i]) && (!best.TryGetValue(ids[i], out var b) || i + 1 < b.Pos))
                {
                    best[ids[i]] = (RowKind(row), i + 1);
                }
            }
        }

        foreach (var (id, (row, pos)) in best)
        {
            if (ledger.TryGetValue(id, out var e))
            {
                e.Last = Math.Max(e.Last, now);
            }
            else
            {
                ledger[id] = new Entry { Row = row, Position = pos, First = now, Last = now };
            }
        }

        foreach (var old in ledger.Where(kv => now - kv.Value.Last > ForgetDays * Core.Day).Select(kv => kv.Key).ToList())
        {
            ledger.Remove(old);
        }

        if (ledger.Count > max)
        {
            foreach (var old in ledger.OrderBy(kv => kv.Value.Last).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(ledger.Count - max).Select(kv => kv.Key).ToList())
            {
                ledger.Remove(old);
            }
        }
    }

    /// <summary>Lecture commencée (film entamé ou vu, au moins un épisode) : un « J'aime » ou une liste de suivi ne comptent pas.</summary>
    public static bool Began(Interaction x) => x.Played || x.Completion > 0 || x.Episodes > 0;

    /// <summary>Vu en entier : film vu ou lu à 90 % ; série vue à 90 %.</summary>
    public static bool Finished(Interaction x, Title? t)
        => t?.Kind == TitleKind.Series ? x.Completion >= 0.9 : x.Played || x.Completion >= 0.9;

    /// <summary>Bilan de tous les comptes.</summary>
    /// <param name="ledgers">Registre de chaque compte.</param>
    /// <param name="history">Interactions de chaque compte (Start = première lecture).</param>
    /// <param name="titles">Titres (type film / série), peut être incomplet.</param>
    /// <param name="now">Maintenant (secondes Unix).</param>
    public static Report Compute(IReadOnlyDictionary<string, Dictionary<string, Entry>> ledgers,
        IReadOnlyDictionary<string, Dictionary<string, Interaction>> history, IReadOnlyDictionary<string, Title> titles, double now)
    {
        var rep = new Report();
        double oldest = now;
        foreach (var (user, ledger) in ledgers)
        {
            var h = history.GetValueOrDefault(user);
            foreach (var (id, e) in ledger)
            {
                oldest = Math.Min(oldest, e.First);
                var x = h?.GetValueOrDefault(id);
                double start = x is not null && x.Start > 0 && Began(x) ? x.Start : double.NaN;
                // une lecture antérieure à la proposition ne lui doit rien
                bool after = !double.IsNaN(start) && start >= e.First;
                bool started = after && start - e.First <= StartDays * Core.Day;
                bool finished = after && x is not null && Finished(x, titles.GetValueOrDefault(id))
                                && Math.Max(x.Last, start) - e.First <= FinishDays * Core.Day;

                var groups = new[]
                {
                    rep.All,
                    Get(rep.ByRow, e.Row),
                    Get(rep.ByPosition, Bucket(e.Position)),
                };
                foreach (var c in groups)
                {
                    if (now - e.First >= StartDays * Core.Day || started)
                    {
                        c.Proposed7++;
                        c.Started7 += started ? 1 : 0;
                    }

                    if (now - e.First >= FinishDays * Core.Day || finished)
                    {
                        c.Proposed30++;
                        c.Finished30 += finished ? 1 : 0;
                    }
                }
            }
        }

        foreach (var (user, h) in history)
        {
            var ledger = ledgers.GetValueOrDefault(user);
            foreach (var (id, x) in h)
            {
                if (!Began(x) || x.Start <= 0 || now - x.Start > RecentDays * Core.Day || x.Start > now)
                {
                    continue;
                }

                rep.RecentStarts++;
                if (ledger is not null && ledger.TryGetValue(id, out var e) && e.First <= x.Start && x.Start - e.Last <= CreditDays * Core.Day)
                {
                    rep.RecentFromReco++;
                }
            }
        }

        rep.Days = Math.Round(Math.Max(0, now - oldest) / Core.Day, 1);
        return rep;
    }

    private static Counts Get(SortedDictionary<string, Counts> d, string k)
    {
        if (!d.TryGetValue(k, out var c))
        {
            d[k] = c = new Counts();
        }

        return c;
    }
}
