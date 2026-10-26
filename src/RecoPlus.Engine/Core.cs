using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Vecteur creux normalisé (caractéristique → poids).</summary>
public sealed class SparseVector
{
    /// <summary>Clés triées (ordinal) et valeurs alignées, pour un produit scalaire par fusion.</summary>
    public string[] Keys { get; }

    /// <summary>Valeurs alignées sur Keys.</summary>
    public double[] Values { get; }

    /// <summary>Construit le vecteur.</summary>
    public SparseVector(IEnumerable<KeyValuePair<string, double>> entries)
    {
        var arr = entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
        Keys = arr.Select(e => e.Key).ToArray();
        Values = arr.Select(e => e.Value).ToArray();
    }

    // rangs des clés dans le vocabulaire commun d'un lot de vecteurs (même ordre que l'ordre ordinal des clés) : la
    // fusion compare des entiers au lieu de chaînes, dans le même ordre, donc avec exactement le même résultat
    private int[]? _ranks;
    private object? _vocab;

    /// <summary>Rattache le vecteur au vocabulaire trié d'un lot (rangs croissants comme les clés).</summary>
    internal void UseVocabulary(object vocab, IReadOnlyDictionary<string, int> rank)
    {
        var r = new int[Keys.Length];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = rank[Keys[i]];
        }

        (_ranks, _vocab) = (r, vocab);
    }

    /// <summary>Produit scalaire.</summary>
    public double Dot(SparseVector o)
    {
        double s = 0;
        int i = 0, j = 0;
        if (_vocab is not null && ReferenceEquals(_vocab, o._vocab))
        {
            int[] a = _ranks!, b = o._ranks!;
            double[] va = Values, vb = o.Values;
            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j])
                {
                    s += va[i] * vb[j];
                    i++;
                    j++;
                }
                else if (a[i] < b[j])
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return s;
        }

        while (i < Keys.Length && j < o.Keys.Length)
        {
            int c = string.CompareOrdinal(Keys[i], o.Keys[j]);
            if (c == 0)
            {
                s += Values[i] * o.Values[j];
                i++;
                j++;
            }
            else if (c < 0)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return s;
    }

    /// <summary>Produit scalaire avec un vecteur dense indexé par clé.</summary>
    public double Dot(IReadOnlyDictionary<string, double> dense)
    {
        double s = 0;
        for (int i = 0; i < Keys.Length; i++)
        {
            if (dense.TryGetValue(Keys[i], out var v))
            {
                s += Values[i] * v;
            }
        }

        return s;
    }
}

/// <summary>Fonctions élémentaires du moteur (strictement alignées sur le prototype Python, engine.py).</summary>
public static class Core
{
    /// <summary>Secondes par jour.</summary>
    public const double Day = 86400.0;

    /// <summary>
    /// Un « J'aime » de Jellyfin sur un titre de la liste de suivi (Seerr et Jellyfin Enhanced y reportent la liste) est
    /// une envie de voir, pas un avis — tant que le titre n'est pas vu : film vu ou regardé à moitié, série vue à moitié.
    /// Une fois vu, un « J'aime » laissé en place est un avis (le compte aurait pu le retirer).
    /// </summary>
    public static bool LikeIsWatchlistIntent(Interaction x, TitleKind? kind)
        => x.Watchlisted > 0 && x.Rating is 1.0
           && !(kind == TitleKind.Series ? x.Completion >= 0.5 : x.Played || x.Completion >= 0.5);

    /// <summary>Poids signé d'une interaction : &gt;0 aimé, &lt;0 abandonné, 0 neutre (en cours, zapping).</summary>
    public static double Signal(Interaction x, TitleKind kind, int nEpisodes, double now, EngineSettings p)
    {
        bool seenHere = x.Played || x.Episodes > 0 || x.Completion >= p.Zap;
        double last = x.Last != 0 ? x.Last : now;
        double idle = (now - last) / Day;
        double b;
        if (kind == TitleKind.Movie)
        {
            if (x.Played || x.Completion >= p.MovieDone)
            {
                b = 1.0;
            }
            else if (x.Completion < p.Zap || idle < p.AbandonDays)
            {
                b = 0.0; // zapping, ou simplement en cours (la marge : on peut regarder un film en deux fois)
            }
            else
            {
                b = -p.AbandonWeight;
            }
        }
        else
        {
            int e = x.Episodes;
            if (e == 0)
            {
                b = 0.0;
            }
            else if (e <= p.SeriesAbandonEpisodes && x.Completion < p.SeriesAbandonFraction
                     && nEpisodes >= p.SeriesMinEpisodes && idle >= p.AbandonDays)
            {
                b = -p.AbandonWeight;
            }
            else if (p.SeriesByTime)
            {
                // temps regardé : 150 épisodes de 15 min ne pèsent pas autant que 150 de 45 min
                double h = x.Hours > 0 ? x.Hours : e * DefaultEpisodeHours;
                b = Math.Min(p.SeriesMaxWeight, Math.Log(1 + h) / Math.Log(1 + p.SeriesFullHours));
            }
            else
            {
                b = Math.Min(p.SeriesMaxWeight, Math.Log(1 + e) / Math.Log(1 + p.SeriesFullEpisodes));
            }
        }

        // Masqué (« pas intéressé ») : refus explicite, il l'emporte sur tout le reste.
        if (x.Hidden)
        {
            return -p.HiddenWeight;
        }

        // Favori : la série entière compte pleinement ; une saison en partie ; des épisodes un peu (un épisode aimé
        // n'est pas une série aimée), à concurrence de 3 épisodes.
        double fav = FavoritePart(x, p);
        if (fav > 0)
        {
            b = Math.Max(b, 0) + (p.FavoriteBoost * fav);
        }

        // Revu : signal fort d'un titre aimé (ne rattrape pas un abandon). Un titre marqué « vu » en masse n'est pas
        // revu : re-marquer fait grimper le compteur de lectures sans visionnage.
        if (x.Rewatch > 0 && b > 0 && !x.Backfill)
        {
            b += p.RewatchBoost * Math.Min(1.0, x.Rewatch);
        }

        // Note explicite du compte : dans les deux sens, elle prime sur ce que le visionnage laisse deviner.
        if (x.Rating is double r && r != 0)
        {
            r = Math.Clamp(r, -1.0, 1.0);
            b = r > 0 ? Math.Max(b, 0) + (p.RatingWeight * r) : Math.Min(b, 0) + (p.RatingWeight * r);
        }

        // Aimé sans être vu ici (vu ailleurs) : un goût, plus léger qu'un visionnage, sans date donc sans oubli.
        if (!seenHere && b > 0)
        {
            return b * p.LikedUnseen;
        }

        // Demande Seerr ou liste de suivi d'un titre jamais commencé : intention déclarée, de COURT terme (demi-vie
        // propre, coupure). Dès que le titre est regardé, c'est ce visionnage (aimé, abandonné…) qui compte.
        if ((x.Requested > 0 || x.Watchlisted > 0) && b == 0 && !x.Played && x.Episodes == 0 && x.Completion < p.Zap && fav == 0)
        {
            return Math.Max(Intent(x.Requested, p.RequestWeight, now, p), Intent(x.Watchlisted, p.WatchlistWeight, now, p));
        }

        if (b > 0)
        {
            double age = Math.Max(0.0, (now - last) / Day);
            double w = p.Decay == DecayKind.Power ? 1.0 / (1.0 + (age / p.HalfLifeDays)) : Math.Pow(0.5, age / p.HalfLifeDays);
            b *= x.Backfill ? p.RecencyFloor : Math.Max(p.RecencyFloor, w); // date inconnue : un goût ancien
        }

        return b;
    }

    /// <summary>Durée supposée d'un épisode dont la durée est inconnue (heures).</summary>
    public const double DefaultEpisodeHours = 0.75;

    /// <summary>Marquage en masse : au moins ce nombre de titres « commencés » à moins de BulkGapSeconds d'écart.</summary>
    public const int BulkMinTitles = 4;

    /// <summary>Écart maximal (secondes) entre deux titres d'un marquage en masse.</summary>
    public const double BulkGapSeconds = 120;

    /// <summary>
    /// Repère les titres dont la date est inconnue : vus sans aucune date, ou marqués « vus » en masse (au moins
    /// BulkMinTitles titres vus « commencés » à moins de BulkGapSeconds les uns des autres — personne ne lance 4 films
    /// en quelques minutes) ; une grappe de dates de fin (« marquer comme vu » sur des titres déjà commencés) ramène la
    /// date au début connu. Renvoie le nombre de titres marqués.
    /// </summary>
    public static int MarkBackfill(IEnumerable<Interaction> history)
    {
        var all = history.ToList();
        int n = 0;
        foreach (var x in all)
        {
            if ((x.Played || x.Episodes > 0) && x.Start == 0 && x.Last == 0 && !x.Backfill)
            {
                x.Backfill = true;
                n++;
            }
        }

        var seen = all.Where(x => (x.Played || x.Episodes > 0) && x.Start != 0).OrderBy(x => x.Start).ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
        int i = 0;
        while (i < seen.Count)
        {
            int j = i;
            while (j + 1 < seen.Count && seen[j + 1].Start - seen[j].Start <= BulkGapSeconds)
            {
                j++;
            }

            if (j - i + 1 >= BulkMinTitles)
            {
                for (int k = i; k <= j; k++)
                {
                    if (!seen[k].Backfill)
                    {
                        seen[k].Backfill = true;
                        n++;
                    }
                }
            }

            i = j + 1;
        }

        // « Marquer comme vu » en masse sur des titres déjà commencés : la date de fin est réécrite, pas le début. Une
        // grappe de fins (même règle) n'est pas une date de visionnage : on garde le début s'il est connu et antérieur,
        // sinon la date est inconnue.
        var ends = all.Where(x => (x.Played || x.Episodes > 0) && x.Last != 0 && !x.Backfill).OrderBy(x => x.Last).ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
        i = 0;
        while (i < ends.Count)
        {
            int j = i;
            while (j + 1 < ends.Count && ends[j + 1].Last - ends[j].Last <= BulkGapSeconds)
            {
                j++;
            }

            if (j - i + 1 >= BulkMinTitles)
            {
                for (int k = i; k <= j; k++)
                {
                    var x = ends[k];
                    if (x.Start != 0 && x.Start < x.Last - BulkGapSeconds)
                    {
                        x.Last = x.Start;
                    }
                    else
                    {
                        x.Backfill = true;
                        n++;
                    }
                }
            }

            i = j + 1;
        }

        return n;
    }

    /// <summary>Part du bonus de favori (0..1) : série = 1, saison = FavSeasonShare, épisodes = FavEpisodeShare × min(1, n/3).</summary>
    public static double FavoritePart(Interaction x, EngineSettings p)
    {
        if (x.Favorite)
        {
            return 1.0;
        }

        double s = (x.FavSeasons > 0 ? p.FavSeasonShare : 0.0) + (p.FavEpisodeShare * Math.Min(1.0, x.FavEpisodes / 3.0));
        return Math.Min(1.0, s);
    }

    private static double Intent(double at, double weight, double now, EngineSettings p)
    {
        if (at <= 0)
        {
            return 0.0;
        }

        double age = Math.Max(0.0, (now - at) / Day);
        return age > p.RequestMaxDays ? 0.0 : weight * Math.Pow(0.5, age / p.RequestHalfLifeDays);
    }

    /// <summary>Caractéristiques brutes d'un titre.</summary>
    public static Dictionary<string, double> Tokens(Title t, EngineSettings p)
    {
        var f = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var g in t.Genres)
        {
            f["g:" + g] = p.FeatGenre;
        }

        foreach (var x in t.Tags)
        {
            f["t:" + x.ToLowerInvariant()] = p.FeatTag;
        }

        foreach (var x in t.Studios.Take(p.StudiosKept))
        {
            f["s:" + x] = p.FeatStudio;
        }

        foreach (var x in Role(t, "Actor").Take(p.Actors))
        {
            f["a:" + x] = p.FeatActor;
        }

        foreach (var x in Role(t, "Director"))
        {
            f["d:" + x] = p.FeatDirector;
        }

        foreach (var x in Role(t, "Writer"))
        {
            f["w:" + x] = p.FeatWriter;
        }

        foreach (var x in Role(t, "Creator"))
        {
            f["c:" + x] = p.FeatCreator;
        }

        if (t.Year is int y && y != 0)
        {
            f["y:" + (FloorDiv(y, 10) * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)] = p.FeatDecade;
        }

        f["k:" + t.Kind] = p.FeatKind;

        // facultatifs : absents du vecteur quand leur poids est nul (vecteurs identiques à ceux d'avant)
        if (p.FeatCountry > 0)
        {
            foreach (var x in t.Countries.Take(3))
            {
                f["n:" + x] = p.FeatCountry;
            }
        }

        if (p.FeatLanguage > 0 && !string.IsNullOrWhiteSpace(t.Language))
        {
            f["l:" + t.Language.Trim().ToLowerInvariant()] = p.FeatLanguage;
        }

        return f;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    private static IEnumerable<string> Role(Title t, string role)
        => t.People.TryGetValue(role, out var l) ? l : Enumerable.Empty<string>();

    /// <summary>Vecteurs de contenu normalisés (TF-IDF ou BM25), caractéristiques présentes dans plus de la moitié des titres ignorées.</summary>
    public static Dictionary<string, SparseVector> BuildVectors(IReadOnlyDictionary<string, Title> titles, EngineSettings p)
    {
        var raw = titles.ToDictionary(kv => kv.Key, kv => Tokens(kv.Value, p), StringComparer.Ordinal);
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in raw.Values)
        {
            foreach (var k in f.Keys)
            {
                df[k] = df.GetValueOrDefault(k) + 1;
            }
        }

        int n = raw.Count;
        double avg = n == 0 ? 0 : raw.Values.Sum(f => (double)f.Count) / n;
        var vec = new Dictionary<string, SparseVector>(StringComparer.Ordinal);
        foreach (var (tid, f) in raw)
        {
            var v = new List<KeyValuePair<string, double>>();
            double tf = p.Weighting == WeightingScheme.Bm25
                ? (p.Bm25K1 + 1) / (1 + (p.Bm25K1 * (1 - p.Bm25B + (p.Bm25B * f.Count / avg))))
                : 1.0;
            foreach (var (k, w) in f)
            {
                int d = df[k];
                if (!(d < n * 0.5 || k.StartsWith("k:", StringComparison.Ordinal)))
                {
                    continue;
                }

                double idf = p.Weighting == WeightingScheme.Bm25
                    ? Math.Log(1 + ((n - d + 0.5) / (d + 0.5)))
                    : Math.Log((n + 1) / (d + 0.5));
                v.Add(new(k, w * tf * idf));
            }

            double norm = Math.Sqrt(v.Sum(e => e.Value * e.Value));
            if (norm == 0)
            {
                norm = 1.0;
            }

            vec[tid] = new SparseVector(v.Select(e => new KeyValuePair<string, double>(e.Key, e.Value / norm)));
        }

        // vocabulaire du lot, trié comme les clés : produits scalaires sur des entiers (même ordre, même résultat)
        var vocab = vec.Values.SelectMany(x => x.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
        var tag = new object();
        foreach (var x in vec.Values)
        {
            x.UseVocabulary(tag, vocab);
        }

        return vec;
    }

    /// <summary>Cosinus item-item sur les titres aimés, avec rétrécissement et minimum de spectateurs communs.</summary>
    public static Dictionary<string, Dictionary<string, double>> ItemCooc(IEnumerable<IReadOnlyCollection<string>> userPositives, double shrink, int minCommon)
    {
        var cnt = new Dictionary<string, int>(StringComparer.Ordinal);
        var pair = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var items in userPositives)
        {
            foreach (var i in items)
            {
                cnt[i] = cnt.GetValueOrDefault(i) + 1;
            }

            foreach (var i in items)
            {
                if (!pair.TryGetValue(i, out var row))
                {
                    pair[i] = row = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                foreach (var j in items)
                {
                    if (!string.Equals(i, j, StringComparison.Ordinal))
                    {
                        row[j] = row.GetValueOrDefault(j) + 1;
                    }
                }
            }
        }

        var res = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (i, row) in pair)
        {
            var r = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (j, c) in row)
            {
                if (c >= minCommon)
                {
                    r[j] = c / (Math.Sqrt((double)cnt[i] * cnt[j]) + shrink);
                }
            }

            res[i] = r;
        }

        return res;
    }

    /// <summary>T0 = écart médian (jours) entre deux débuts de titres consécutifs d'un même compte (RecSys 2015).</summary>
    public static double AutoHalfLife(IEnumerable<IEnumerable<Interaction>> hist, double floor = 30, double fallback = 90, int minGaps = 20)
    {
        var gaps = new List<double>();
        foreach (var h in hist)
        {
            var st = h.Where(x => x.Start != 0).Select(x => x.Start).OrderBy(v => v).ToList();
            for (int i = 1; i < st.Count; i++)
            {
                if (st[i] > st[i - 1])
                {
                    gaps.Add((st[i] - st[i - 1]) / Day);
                }
            }
        }

        if (gaps.Count < minGaps)
        {
            return fallback;
        }

        gaps.Sort();
        return Math.Max(floor, gaps[gaps.Count / 2]);
    }
}
