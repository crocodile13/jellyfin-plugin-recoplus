using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoPlus.Engine;

/// <summary>Rangées d'accueil demandées (Home Screen Sections).</summary>
public sealed class RowOptions
{
    /// <summary>Titres par rangée.</summary>
    public int Size { get; set; } = 20;

    /// <summary>« Films pour toi ».</summary>
    public bool Movies { get; set; }

    /// <summary>« Séries pour toi ».</summary>
    public bool Series { get; set; }

    /// <summary>« Nouveautés pour toi ».</summary>
    public bool New { get; set; }

    /// <summary>« Nouveautés » : arrivés depuis ce nombre de jours.</summary>
    public int NewDays { get; set; } = 30;

    /// <summary>Rangées « Parce que tu as aimé … » (0 à 3).</summary>
    public int Because { get; set; }

    /// <summary>Règle de saga dans chaque rangée (un volet par saga, le premier pas encore vu).</summary>
    public bool Saga { get; set; } = true;

    /// <summary>Au-delà de cette taille, une collection est un regroupement thématique (voir EngineSettings.SagaMax).</summary>
    public int SagaMax { get; set; } = 12;

    /// <summary>
    /// Rangées tirées d'une liste TMDb (« Tendances », « Populaires », « AVenir ») : titres de la bibliothèque de la liste,
    /// dans l'ordre de la rangée pour ce compte (<see cref="Trending.Order"/>). Rangée absente = pas de rangée.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> Lists { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sortes de titres par rangée (« PourToi », « Tendances », « Populaires », « AVenir », « Nouveautes », « ParceQue ») ;
    /// absente = films et séries ensemble. <see cref="KindFilter.Split"/> : deux rangées, suffixes « Films » et « Series ».
    /// </summary>
    public Dictionary<string, KindFilter> Kinds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Rangées que Home Screen Sections montre au compte (identifiants complets) ; null = toutes.</summary>
    public HashSet<string>? Shown { get; set; }

    /// <summary>Réglage de sortes d'une rangée de base (« ParceQue2 » → celui de « ParceQue »).</summary>
    public KindFilter KindSetting(string baseRow)
        => Kinds.TryGetValue(baseRow.StartsWith(HomeRows.ParceQue, StringComparison.Ordinal) ? HomeRows.ParceQue : baseRow, out var k) ? k : KindFilter.Both;

    /// <summary>Sortes de titres effectivement admises dans une rangée (« TendancesFilms » : films ; « Films » : films).</summary>
    public KindFilter KindOf(string row)
    {
        var (b, sfx) = HomeRows.SplitOf(row);
        return sfx ?? (KindSetting(b) == KindFilter.Split ? KindFilter.Both : KindSetting(b));
    }

    /// <summary>Copie (les listes et sortes sont partagées, à remplacer et non à modifier).</summary>
    public RowOptions Clone() => (RowOptions)MemberwiseClone();
}

/// <summary>Contenu d'une rangée d'accueil.</summary>
public enum RowSource
{
    /// <summary>Titres de la bibliothèque seulement.</summary>
    Local,

    /// <summary>Titres hors bibliothèque (Seerr) seulement.</summary>
    Seerr,

    /// <summary>Les deux, mélangés.</summary>
    Mix
}

/// <summary>Façon de mélanger bibliothèque et hors bibliothèque dans une rangée.</summary>
public sealed class MixOptions
{
    /// <summary>Libre (classement commun du moteur) ; sinon à intervalle fixe.</summary>
    public bool Free { get; set; }

    /// <summary>Intervalle fixe : un titre hors bibliothèque toutes les N cartes.</summary>
    public int Every { get; set; } = 3;

    /// <summary>Libre : nombre minimal de titres hors bibliothèque montrés.</summary>
    public int MinFree { get; set; }

    /// <summary>Libre : nombre maximal de titres hors bibliothèque placés d'eux-mêmes (0 = aucune limite).</summary>
    public int MaxFree { get; set; }

    /// <summary>Libre : places « découverte » (les mieux notés qui n'ont pas trouvé de place).</summary>
    public int Discovery { get; set; }
}

/// <summary>
/// Répartit les recommandations d'un compte entre ses rangées d'accueil sans qu'un titre apparaisse deux fois :
/// « Pour toi » d'abord, puis Tendances, Nouveautés, « Parce que tu as aimé … » (des titres aimés différents, de sagas
/// différentes), Films, Séries. Chaque rangée peut se limiter aux films ou aux séries (<see cref="RowOptions.Kinds"/>).
/// Les autres rangées de l'accueil (celles de Jellyfin ou d'autres plugins) ne sont pas concernées.
/// </summary>
public static class HomeRows
{
    /// <summary>Identifiants des rangées (suffixes des sections Home Screen Sections).</summary>
    public const string PourToi = "PourToi", Films = "Films", Series = "Series", Nouveautes = "Nouveautes", ParceQue = "ParceQue", Tendances = "Tendances",
        Populaires = "Populaires", AVenir = "AVenir";

    /// <summary>Rangées tirées d'une liste TMDb, dans l'ordre de priorité de la répartition.</summary>
    public static readonly string[] ListRows = { Tendances, Populaires, AVenir };

    /// <summary>Rangées dont la sorte de titres se règle (les autres sont « Films » et « Séries », déjà d'une seule sorte).</summary>
    public static readonly string[] KindRows = { PourToi, Tendances, Populaires, AVenir, Nouveautes, ParceQue };

    /// <summary>Contenus réglables, un par sorte de rangée (« ParceQue » vaut pour les trois rangées « Parce que … »).</summary>
    public static readonly string[] SourceRows = { PourToi, Tendances, Populaires, AVenir, Nouveautes, ParceQue, Films, Series };

    /// <summary>
    /// Rangées de base, dans l'ordre de la répartition (et de la liste de Home Screen Sections) ; les listes TMDb seulement
    /// si elles existent (<paramref name="lists"/>), sinon elles seraient vides.
    /// </summary>
    public static IEnumerable<string> BaseRows(bool lists)
    {
        yield return PourToi;
        if (lists)
        {
            foreach (var l in ListRows)
            {
                yield return l;
            }
        }

        yield return Nouveautes;
        for (int i = 1; i <= 3; i++)
        {
            yield return ParceQue + i;
        }

        yield return Films;
        yield return Series;
    }

    /// <summary>Suffixes des deux rangées d'une rangée séparée (films, séries).</summary>
    public const string MoviesSuffix = "Films", SeriesSuffix = "Series";

    /// <summary>
    /// Rangée de base et sorte imposée par le suffixe : « TendancesFilms » → (« Tendances », films) ; « ParceQue2Series » →
    /// (« ParceQue2 », séries) ; « Films » et « Series » restent elles-mêmes, de leur sorte ; sinon (rangée, null).
    /// </summary>
    public static (string Base, KindFilter? Kind) SplitOf(string row)
    {
        if (row == Films)
        {
            return (row, KindFilter.Movies);
        }

        if (row == Series)
        {
            return (row, KindFilter.Series);
        }

        if (row.Length > MoviesSuffix.Length && row.EndsWith(MoviesSuffix, StringComparison.Ordinal))
        {
            return (row[..^MoviesSuffix.Length], KindFilter.Movies);
        }

        if (row.Length > SeriesSuffix.Length && row.EndsWith(SeriesSuffix, StringComparison.Ordinal))
        {
            return (row[..^SeriesSuffix.Length], KindFilter.Series);
        }

        return (row, null);
    }

    /// <summary>Type de rangée, pour les réglages et les bilans : « ParceQue2Films » → « ParceQue », « TendancesSeries » → « Tendances ».</summary>
    public static string KindOfRow(string row)
    {
        var b = SplitOf(row).Base;
        return b.StartsWith(ParceQue, StringComparison.Ordinal) ? ParceQue : b;
    }

    /// <summary>Identifiants des rangées produites par une rangée de base selon son réglage de sortes.</summary>
    public static IEnumerable<string> Expand(string baseRow, KindFilter k)
        => k == KindFilter.Split ? new[] { baseRow + MoviesSuffix, baseRow + SeriesSuffix } : new[] { baseRow };

    /// <summary>Titres de réserve par rangée (droits d'accès revérifiés à l'affichage).</summary>
    public const int Spare = 6;

    /// <summary>Ressemblance minimale absolue avec le titre aimé pour « Parce que tu as aimé … » (plancher).</summary>
    public const double MinSimilarity = 0.05;

    /// <summary>
    /// Ressemblance minimale relative : part de la ressemblance du voisin le plus proche encore libre. Les ressemblances
    /// dépendent de la bibliothèque (nombre et finesse des métadonnées) : un seuil fixe vide les rangées des unes et
    /// remplit celles des autres de titres sans rapport.
    /// </summary>
    public const double RelativeSimilarity = 0.4;

    /// <summary>
    /// Rangée « Nouveautés » : seulement les titres arrivés récemment qui figurent dans cette part du classement par goût
    /// (le premier quart). Sans cela, une rangée de nouveautés se remplit de tout ce qui vient d'arriver, même sans aucun
    /// rapport avec le compte (peu de titres arrivent en un mois).
    /// </summary>
    public const double NewTopShare = 0.25;

    /// <summary>Titres aimés examinés comme points de départ des rangées « Parce que … » (les plus forts d'abord).</summary>
    public const int AnchorCandidates = 40;

    /// <summary>Une rangée « Parce que tu as aimé … » n'est montrée qu'avec au moins ce nombre de titres.</summary>
    public const int MinBecause = 6;

    /// <summary>Résultat : titres par rangée, et titre aimé (nom, identifiant) de chaque rangée « Parce que … ».</summary>
    public sealed record Result(Dictionary<string, List<string>> Rows, Dictionary<string, string> Anchors)
    {
        /// <summary>Identifiant du titre aimé de chaque rangée « Parce que … ».</summary>
        public Dictionary<string, string> AnchorIds { get; init; } = new(StringComparer.Ordinal);
    }

    /// <param name="ctx">Contexte du calcul.</param>
    /// <param name="vec">Vecteurs de contenu.</param>
    /// <param name="user">Compte.</param>
    /// <param name="pourToi">« Pour toi » tel qu'il sera affiché (demandes en tête, puis la liste présentée).</param>
    /// <param name="ordered">Ordre de présentation des titres de la bibliothèque (liste présentée puis le reste par score).</param>
    /// <param name="sc">Score de chaque candidat.</param>
    /// <param name="o">Rangées demandées.</param>
    /// <param name="fit">
    /// Titres hors bibliothèque : appelé pour chaque rangée remplie (identifiant, titre aimé des rangées « Parce que … »,
    /// ses titres de la bibliothèque dans la limite de sa taille), renvoie combien de ces titres restent (les premiers) ;
    /// une valeur négative ou ≥ au nombre reçu ne change rien. Les titres qui sortent redeviennent libres pour les rangées
    /// suivantes ; quelques-uns restent en réserve à la fin de la rangée, sans y être réservés (secours de l'affichage).
    /// </param>
    public static Result Allocate(Context ctx, IReadOnlyDictionary<string, SparseVector> vec, string user, IReadOnlyList<string> pourToi,
        IReadOnlyList<string> ordered, IReadOnlyDictionary<string, double> sc, RowOptions o, Func<string, string?, IReadOnlyList<string>, int>? fit = null)
    {
        var T = ctx.Ds.Titles;
        int size = Math.Clamp(o.Size, 5, 60);
        int keep = size + Spare;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchorIds = new Dictionary<string, string>(StringComparer.Ordinal);

        List<string> Take(IEnumerable<string> src, int n)
        {
            var l = new List<string>();
            foreach (var id in src)
            {
                if (l.Count >= n)
                {
                    break;
                }

                if (used.Add(id))
                {
                    l.Add(id);
                }
            }

            return l;
        }

        List<string> Row(string id, string? anchor, IEnumerable<string> src)
        {
            var l = Take(src, keep);
            if (fit is null || l.Count == 0)
            {
                return l;
            }

            var shown = l.Take(size).ToList();
            int k = fit(id, anchor, shown);
            if (k < 0 || k >= shown.Count)
            {
                return l;
            }

            foreach (var x in l.Skip(k))
            {
                used.Remove(x);
            }

            return l.Take(k).Concat(l.Skip(k).Take(Spare)).ToList();
        }

        // règle de saga d'une rangée : un seul volet par saga, et jamais un volet dont un précédent n'est pas encore vu —
        // le premier volet non vu le remplace s'il est libre et va dans la rangée (substitute), sinon le titre est écarté ;
        // une suite dont le début manque à la bibliothèque passe après le reste
        var candidates = new HashSet<string>(ordered, StringComparer.Ordinal);
        IEnumerable<string> Sagaed(IEnumerable<string> src, Func<string, bool>? substitute)
        {
            if (!o.Saga)
            {
                foreach (var id in src)
                {
                    yield return id;
                }

                yield break;
            }

            var sagas = new HashSet<string>(StringComparer.Ordinal);
            var later = new List<string>();
            foreach (var id in src)
            {
                if (Sagas.IsOrphanSequel(ctx.Ds, id))
                {
                    later.Add(id);
                    continue;
                }

                if (Recommender.Franchise(ctx.Ds, id) is { } f && Sagas.IsSaga(ctx.Ds, f, o.SagaMax))
                {
                    if (sagas.Contains(f.Id))
                    {
                        continue;
                    }

                    string? first = null;
                    foreach (var p in ctx.Ds.Collections[f.Id])
                    {
                        if (candidates.Contains(p) && (first is null || Sagas.Order(T[p], T[first]) < 0))
                        {
                            first = p;
                        }
                    }

                    if (first is not null && first != id)
                    {
                        // volet plus loin : remplacé par le premier non vu s'il peut aller ici, écarté sinon (le premier
                        // pourra encore venir plus bas dans la rangée)
                        if (substitute is not null && !used.Contains(first) && substitute(first))
                        {
                            sagas.Add(f.Id);
                            yield return first;
                        }

                        continue;
                    }

                    sagas.Add(f.Id);
                }

                yield return id;
            }

            foreach (var id in later)
            {
                yield return id;
            }
        }

        bool Kind(KindFilter k, string id) => T.TryGetValue(id, out var t) && Trending.Allows(k, t.Kind);
        bool Visible(string id) => o.Shown is null || o.Shown.Contains(id);

        // une rangée de base donne une rangée, ou deux (films, séries) si elle est séparée ; src(sorte) = ses titres possibles
        void Emit(string baseRow, string? anchor, Func<KindFilter, IEnumerable<string>> src, bool always = false)
        {
            foreach (var id in Expand(baseRow, o.KindSetting(baseRow)))
            {
                if (always || Visible(id))
                {
                    rows[id] = Row(id, anchor, src(o.KindOf(id)));
                }
            }
        }

        // « Pour toi » : ce que HSS affichera, réserves comprises (aucune rangée ne les reprend)
        Emit(PourToi, null, k => pourToi.Where(id => Kind(k, id)), always: true);

        // rangées des listes TMDb (tendances, populaires, à venir) : juste après « Pour toi », pour garder leur identité
        foreach (var lr in ListRows)
        {
            if (o.Lists.TryGetValue(lr, out var list))
            {
                Emit(lr, null, k => Sagaed(list.Where(id => candidates.Contains(id) && !used.Contains(id) && Kind(k, id)), id => Kind(k, id)));
            }
        }

        if (o.New)
        {
            double cut = ctx.Now - (Math.Clamp(o.NewDays, 1, 365) * Core.Day);
            var desc = sc.Values.OrderByDescending(v => v).ToList();
            double bar = desc.Count > 0 ? desc[Math.Min(desc.Count - 1, (int)(NewTopShare * desc.Count))] : double.NegativeInfinity;
            foreach (var id in Expand(Nouveautes, o.KindSetting(Nouveautes)).Where(Visible))
            {
                var k = o.KindOf(id);
                var fresh = ordered.Where(x => Kind(k, x) && (T[x].Added ?? 0) >= cut && !used.Contains(x) && sc.GetValueOrDefault(x, double.NegativeInfinity) >= bar).ToList();
                var freshRow = Sagaed(fresh, null).ToList();
                rows[id] = freshRow.Count >= 4 ? Row(id, null, freshRow) : new List<string>();
            }
        }

        int nb = Math.Clamp(o.Because, 0, 3);
        if (nb > 0)
        {
            double min = sc.Count > 0 ? sc.Values.Min() : 0, span = sc.Count > 0 ? sc.Values.Max() - min : 0;
            var sig = ctx.Sig.TryGetValue(user, out var su) ? su : new Dictionary<string, double>();
            var hist = ctx.Hist.TryGetValue(user, out var hu) ? hu : new Dictionary<string, Interaction>();
            var sagas = new HashSet<string>(StringComparer.Ordinal);
            var kb = o.KindSetting(ParceQue) == KindFilter.Split ? KindFilter.Both : o.KindSetting(ParceQue);
            int k = 0;
            // point de départ : un titre réellement regardé (pas seulement mis en favori, liké ou demandé)
            foreach (var (a, _) in sig.Where(e => e.Value > 0 && T.TryGetValue(e.Key, out var t) && !t.External && vec.ContainsKey(e.Key)
                                                   && hist.TryGetValue(e.Key, out var ax) && Watched(ax, t))
                         .OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).Take(AnchorCandidates))
            {
                if (k >= nb)
                {
                    break;
                }

                var fr = Recommender.Franchise(ctx.Ds, a);
                if (fr is { } f && sagas.Contains(f.Id))
                {
                    continue; // un seul titre de départ par saga
                }

                var av = vec[a];
                var free = ordered.Where(id => id != a && !used.Contains(id) && Kind(kb, id))     // jamais le titre aimé dans sa propre rangée
                    .Select(id => (Id: id, Sim: av.Dot(vec[id]))).ToList();
                double best = free.Count > 0 ? free.Max(x => x.Sim) : 0;
                double floor = Math.Max(MinSimilarity, RelativeSimilarity * best);
                var near = Sagaed(free
                    .Where(x => x.Sim >= floor)
                    .OrderByDescending(x => (0.7 * x.Sim) + (0.3 * (span > 0 ? (sc[x.Id] - min) / span : 0)))
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => x.Id), null).ToList();
                if (near.Count < MinBecause)
                {
                    continue;
                }

                k++;
                if (fr is { } f2)
                {
                    sagas.Add(f2.Id); // la saga n'est prise que si la rangée existe : un autre volet peut servir sinon
                }

                Emit(ParceQue + k, a, kk => near.Where(id => Kind(kk, id)), always: true);
                foreach (var id in Expand(ParceQue + k, o.KindSetting(ParceQue)))
                {
                    anchors[id] = T[a].Name;
                    anchorIds[id] = a;
                }
            }

            for (int i = k + 1; i <= nb; i++)
            {
                foreach (var id in Expand(ParceQue + i, o.KindSetting(ParceQue)))
                {
                    rows[id] = new List<string>();
                }
            }
        }

        if (o.Movies)
        {
            rows[Films] = Row(Films, null, Sagaed(ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Movie), id => T[id].Kind == TitleKind.Movie));
        }

        if (o.Series)
        {
            rows[Series] = Row(Series, null, Sagaed(ordered.Where(id => T.TryGetValue(id, out var t) && t.Kind == TitleKind.Series), id => T[id].Kind == TitleKind.Series));
        }

        return new Result(rows, anchors) { AnchorIds = anchorIds };
    }

    /// <summary>
    /// Regardé pour de bon, de quoi dire « tu as aimé » : film vu ou regardé au moins à moitié, série dont au moins un
    /// épisode a été vu.
    /// </summary>
    public static bool Watched(Interaction x, Title t)
        => t.Kind == TitleKind.Movie ? x.Played || x.Completion >= 0.5 : x.Episodes > 0 || x.Played || x.Completion > 0;

    /// <summary>
    /// Mélange libre : place de chaque titre hors bibliothèque dans la rangée « Pour toi » = nombre de titres de la
    /// bibliothèque mieux notés que lui (une place égale au nombre de titres montrés = derrière toute la rangée, non montré).
    /// Si moins de <paramref name="minShown"/> titres hors bibliothèque trouvent une place, les mieux classés de ceux qui
    /// n'en ont pas sont ajoutés quand même, répartis régulièrement dans la rangée (jamais plus loin que leur propre place).
    /// </summary>
    /// <param name="lib">Score commun de chaque titre de la bibliothèque montré, dans l'ordre de la rangée.</param>
    /// <param name="ext">Score commun de chaque titre hors bibliothèque, du mieux au moins bien classé.</param>
    /// <param name="minShown">Nombre minimal de titres hors bibliothèque montrés (0 = classement seul).</param>
    public static int[] FreePlaces(IReadOnlyList<double> lib, IReadOnlyList<double> ext, int minShown)
    {
        int n = lib.Count;
        var after = ext.Select(e => lib.Count(x => x > e)).ToArray();
        if (n == 0)
        {
            return after;
        }

        int missing = Math.Min(Math.Max(0, minShown), ext.Count) - after.Count(a => a < n);
        if (missing <= 0)
        {
            return after;
        }

        return ForcePlaces(after, n, Enumerable.Range(0, ext.Count), missing);
    }

    /// <summary>
    /// Rangée finale d'un mélange libre, qui garde sa taille (<paramref name="libCount"/> cartes) : chaque titre hors
    /// bibliothèque placé (place &lt; libCount) s'insère après ses <c>after</c> premiers titres de la bibliothèque, puis la
    /// rangée est coupée à sa taille. Les titres hors bibliothèque coupés repassent à « non placé » (libCount) ; renvoie
    /// aussi le nombre de titres de la bibliothèque qui restent (les premiers, dans l'ordre).
    /// </summary>
    public static (int[] After, int KeptLibrary) FitRow(int[] after, int libCount) => FitRow(after, libCount, libCount);

    /// <summary>Comme <see cref="FitRow(int[], int)"/>, pour une rangée de <paramref name="size"/> cartes.</summary>
    public static (int[] After, int KeptLibrary) FitRow(int[] after, int libCount, int size)
    {
        var res = (int[])after.Clone();
        var order = Enumerable.Range(0, after.Length).Where(i => after[i] >= 0 && after[i] < libCount)
            .OrderBy(i => after[i]).ThenBy(i => i).ToList();
        var kept = new HashSet<int>();
        int total = 0, lib = 0, k = 0;
        for (int l = 0; l <= libCount && total < size; l++)
        {
            while (k < order.Count && after[order[k]] == l && total < size)
            {
                kept.Add(order[k++]);
                total++;
            }

            if (l < libCount && total < size)
            {
                lib++;
                total++;
            }
        }

        for (int i = 0; i < res.Length; i++)
        {
            if (res[i] >= libCount || (res[i] >= 0 && !kept.Contains(i)))
            {
                res[i] = libCount;
            }
        }

        return (res, lib);
    }

    /// <summary>
    /// Place les titres hors bibliothèque d'une rangée. Renvoie, pour chacun, le nombre de titres de la bibliothèque qui le
    /// précèdent (≥ <c>lib.Count</c> = pas dans la rangée), et le nombre de titres de la bibliothèque qui restent (les
    /// premiers) ; la rangée garde sa taille (<paramref name="size"/> cartes au plus).
    /// Seerr : les titres hors bibliothèque seuls, dans l'ordre. Mélange à intervalle fixe : un toutes les N cartes. Mélange
    /// libre : chacun après les titres de la bibliothèque mieux classés que lui (<see cref="FreePlaces"/>), minimum,
    /// maximum et places découverte compris.
    /// </summary>
    /// <param name="lib">Clé de classement de chaque titre de la bibliothèque de la rangée, dans l'ordre.</param>
    /// <param name="ext">Clé de classement de chaque titre hors bibliothèque, du mieux au moins bien classé.</param>
    /// <param name="source">Contenu de la rangée (Local : rien n'est placé).</param>
    /// <param name="o">Façon de mélanger.</param>
    /// <param name="discovery">Mélange libre : ordre de préférence pour les places découverte (indices de <paramref name="ext"/>).</param>
    /// <param name="size">Cartes de la rangée.</param>
    public static (int[] After, int KeptLibrary) PlaceRow(IReadOnlyList<double> lib, IReadOnlyList<double> ext, RowSource source,
        MixOptions o, IEnumerable<int>? discovery, int size)
    {
        int n = lib.Count;
        size = Math.Max(0, size);
        if (source == RowSource.Local || ext.Count == 0 || size == 0)
        {
            return (Enumerable.Repeat(n, ext.Count).ToArray(), Math.Min(n, size));
        }

        if (source == RowSource.Seerr)
        {
            // les titres hors bibliothèque seuls (aucun titre de la bibliothèque devant eux)
            return (Enumerable.Range(0, ext.Count).Select(i => i < size ? 0 : n).ToArray(), 0);
        }

        int[] after;
        if (o.Free)
        {
            after = FreePlaces(lib, ext, Math.Clamp(o.MinFree, 0, 60));
            if (o.MaxFree > 0)
            {
                after = CapPlaces(after, n, Math.Min(o.MaxFree, 60));
            }

            after = ForcePlaces(after, n, discovery ?? Enumerable.Empty<int>(), Math.Clamp(o.Discovery, 0, 10));
        }
        else
        {
            // intervalle fixe : la carte p (1, 2, …) est hors bibliothèque si p est un multiple de N
            int every = Math.Clamp(o.Every, 2, 20), libSeen = 0, e = 0;
            after = Enumerable.Repeat(n, ext.Count).ToArray();
            for (int p = 1; p <= size && libSeen < n; p++)
            {
                if (p % every == 0 && e < ext.Count)
                {
                    after[e++] = libSeen;
                }
                else
                {
                    libSeen++;
                }
            }
        }

        return FitRow(after, n, size);
    }

    /// <summary>
    /// Au plus <paramref name="max"/> titres hors bibliothèque dans la rangée : les mieux classés (ordre des indices)
    /// gardent leur place, les autres passent derrière la rangée. Garde la bibliothèque présente même quand
    /// l'exploration trouve beaucoup de titres mieux classés.
    /// </summary>
    public static int[] CapPlaces(int[] after, int libCount, int max)
    {
        int kept = 0;
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] >= 0 && after[i] < libCount && kept++ >= Math.Max(0, max))
            {
                after[i] = libCount;
            }
        }

        return after;
    }

    /// <summary>
    /// Place de force jusqu'à <paramref name="count"/> titres hors bibliothèque qui n'ont pas de place (place ≥
    /// <paramref name="libCount"/>), pris dans l'ordre <paramref name="order"/>, répartis régulièrement dans la rangée.
    /// </summary>
    public static int[] ForcePlaces(int[] after, int libCount, IEnumerable<int> order, int count)
    {
        if (libCount <= 0 || count <= 0)
        {
            return after;
        }

        var forced = order.Where(i => i >= 0 && i < after.Length && after[i] >= libCount).Distinct().Take(count).ToList();
        for (int j = 0; j < forced.Count; j++)
        {
            after[forced[j]] = Math.Clamp((int)Math.Round((j + 1.0) * libCount / (forced.Count + 1), MidpointRounding.AwayFromZero), 0, libCount - 1);
        }

        return after;
    }
}
