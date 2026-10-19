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

    /// <summary>Produit scalaire.</summary>
    public double Dot(SparseVector o)
    {
        double s = 0;
        int i = 0, j = 0;
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

    /// <summary>Poids signé d'une interaction : &gt;0 aimé, &lt;0 abandonné, 0 neutre (en cours, zapping).</summary>
    public static double Signal(Interaction x, TitleKind kind, int nEpisodes, double now, EngineSettings p)
    {
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
            else
            {
                b = Math.Min(1.0, Math.Log(1 + e) / Math.Log(1 + p.SeriesFullEpisodes));
            }
        }

        if (x.Favorite)
        {
            b = Math.Max(b, 0) + p.FavoriteBoost;
        }

        // Demande Seerr d'un titre jamais commencé : intention déclarée, de COURT terme (demi-vie propre, coupure).
        // Dès que le titre est regardé, c'est ce visionnage (aimé, abandonné…) qui compte, plus la demande.
        if (x.Requested > 0 && b == 0 && !x.Played && x.Episodes == 0 && x.Completion < p.Zap && !x.Favorite)
        {
            double rage = Math.Max(0.0, (now - x.Requested) / Day);
            return rage > p.RequestMaxDays ? 0.0 : p.RequestWeight * Math.Pow(0.5, rage / p.RequestHalfLifeDays);
        }

        if (b > 0)
        {
            double age = Math.Max(0.0, (now - last) / Day);
            double w = p.Decay == DecayKind.Power ? 1.0 / (1.0 + (age / p.HalfLifeDays)) : Math.Pow(0.5, age / p.HalfLifeDays);
            b *= Math.Max(p.RecencyFloor, w);
        }

        return b;
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
