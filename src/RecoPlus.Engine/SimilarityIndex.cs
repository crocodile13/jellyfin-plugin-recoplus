using System;
using System.Collections.Generic;

namespace RecoPlus.Engine;

/// <summary>
/// Produits scalaires « titre du profil · titre candidat », calculés une fois par ligne puis réutilisés : le calibrage
/// évalue des centaines de réglages sur les mêmes vecteurs, et le score se ramène alors à des lectures de tableau.
/// Les lignes ne sont calculées qu'à la demande (titres effectivement présents dans un historique).
/// </summary>
public sealed class SimilarityIndex
{
    private readonly IReadOnlyDictionary<string, SparseVector> _vec;
    private readonly Dictionary<string, int> _col;
    private readonly SparseVector[] _cols;
    private readonly Dictionary<string, double[]> _rows = new(StringComparer.Ordinal);
    private readonly long _maxCells;

    /// <summary>Index sur tous les titres vectorisés.</summary>
    /// <param name="maxCells">Budget mémoire, en nombre de produits gardés (8 octets chacun) ; au-delà, Row renvoie null.</param>
    public SimilarityIndex(IReadOnlyDictionary<string, SparseVector> vec, long maxCells = 16_000_000)
    {
        _vec = vec;
        _maxCells = maxCells;
        _col = new Dictionary<string, int>(vec.Count, StringComparer.Ordinal);
        _cols = new SparseVector[vec.Count];
        int i = 0;
        foreach (var (k, v) in vec)
        {
            _col[k] = i;
            _cols[i++] = v;
        }
    }

    /// <summary>Vecteurs indexés.</summary>
    public IReadOnlyDictionary<string, SparseVector> Vectors => _vec;

    /// <summary>Colonne d'un titre (-1 s'il n'est pas vectorisé).</summary>
    public int Column(string id) => _col.TryGetValue(id, out var c) ? c : -1;

    /// <summary>Ligne « ce titre · chaque titre » (null si le titre n'est pas vectorisé).</summary>
    public double[]? Row(string id)
    {
        lock (_rows)
        {
            return RowLocked(id);
        }
    }

    private double[]? RowLocked(string id)
    {
        if (_rows.TryGetValue(id, out var r))
        {
            return r;
        }

        if (!_vec.TryGetValue(id, out var v) || (long)(_rows.Count + 1) * _cols.Length > _maxCells)
        {
            return null;
        }

        r = new double[_cols.Length];
        for (int j = 0; j < _cols.Length; j++)
        {
            r[j] = v.Dot(_cols[j]);
        }

        _rows[id] = r;
        return r;
    }
}
