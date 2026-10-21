"""Moteur de recommandation (prototype) + références (hasard, popularité, Jellyfin 12)."""
from __future__ import annotations

import math
import random
from collections import Counter, defaultdict

from data import DAY, Dataset, Interaction

# ---------------------------------------------------------------- signaux
DEFAULTS = dict(
    half_life=90.0, recency_floor=0.25, fav_boost=1.0,
    movie_done=0.70, zap=0.05, abandon_days=30.0, abandon_weight=0.5,
    series_full_eps=10, series_abandon_eps=2, series_abandon_frac=0.10, series_min_eps=6,
    decay="exp",                 # exp : 0,5^(âge/T0) ; power : (1 + âge/T0)^-1 (Larrain, Parra et al., RecSys 2015)
    shrink=10.0, min_common=2,   # collaboratif : rétrécissement et minimum de spectateurs communs
    request_weight=0.6, request_half_life=21.0, request_max_days=90.0, watchlist_weight=0.4,
    fav_season_share=0.6, fav_episode_share=0.3, rewatch_boost=0.0, rating_weight=1.0, hidden_weight=1.0,
    series_by_time=True, series_full_hours=7.5, series_max_weight=1.0,   # séries : poids selon le temps regardé
    liked_unseen=0.5,            # aimé sans être vu ici (vu ailleurs) : part du poids, sans oubli
)

DEFAULT_EPISODE_HOURS = 0.75     # durée supposée d'un épisode de durée inconnue
BULK_MIN_TITLES, BULK_GAP = 4, 120.0


def mark_backfill(h: dict) -> int:
    """Date inconnue : vu sans aucune date, ou marqué « vu » en masse (≥ 4 titres à moins de 2 min d'écart)."""
    n = 0
    xs = list(h.values())
    for x in xs:
        if (x.played or x.episodes > 0) and not x.start and not x.last and not x.backfill:
            x.backfill = True
            n += 1
    seen = sorted((x for x in xs if (x.played or x.episodes > 0) and x.start), key=lambda x: (x.start, x.title))
    i = 0
    while i < len(seen):
        j = i
        while j + 1 < len(seen) and seen[j + 1].start - seen[j].start <= BULK_GAP:
            j += 1
        if j - i + 1 >= BULK_MIN_TITLES:
            for k in range(i, j + 1):
                if not seen[k].backfill:
                    seen[k].backfill = True
                    n += 1
        i = j + 1
    # grappe de dates de fin (« marquer comme vu » sur des titres commencés) : le début connu, sinon date inconnue
    ends = sorted((x for x in xs if (x.played or x.episodes > 0) and x.last and not x.backfill), key=lambda x: (x.last, x.title))
    i = 0
    while i < len(ends):
        j = i
        while j + 1 < len(ends) and ends[j + 1].last - ends[j].last <= BULK_GAP:
            j += 1
        if j - i + 1 >= BULK_MIN_TITLES:
            for k in range(i, j + 1):
                x = ends[k]
                if x.start and x.start < x.last - BULK_GAP:
                    x.last = x.start
                else:
                    x.backfill = True
                    n += 1
        i = j + 1
    return n


def auto_half_life(hist: dict, floor=30.0, default=90.0, min_gaps=20) -> float:
    """T0 = écart médian entre deux débuts de titres consécutifs d'un même compte (RecSys 2015)."""
    gaps = []
    for h in hist.values():
        st = sorted(x.start for x in h.values() if x.start)
        gaps += [(b - a) / DAY for a, b in zip(st, st[1:]) if b > a]
    if len(gaps) < min_gaps:
        return default
    gaps.sort()
    return max(floor, gaps[len(gaps) // 2])


def signal(x: Interaction, kind: str, n_eps: int, now: float, P=DEFAULTS) -> float:
    """Poids signé d'une interaction : >0 aimé, <0 abandonné, 0 neutre (en cours, zapping)."""
    seen_here = x.played or x.episodes > 0 or x.completion >= P["zap"]
    idle = (now - (x.last or now)) / DAY
    if kind == "Movie":
        if x.played or x.completion >= P["movie_done"]:
            base = 1.0
        elif x.completion < P["zap"] or idle < P["abandon_days"]:
            base = 0.0                                   # zapping ou simplement en cours
        else:
            base = -P["abandon_weight"]                  # arrêté entre 5 et 70 %, jamais repris
    else:
        e = x.episodes
        if e == 0:
            base = 0.0
        elif (e <= P["series_abandon_eps"] and x.completion < P["series_abandon_frac"]
              and n_eps >= P["series_min_eps"] and idle >= P["abandon_days"]):
            base = -P["abandon_weight"]
        elif P.get("series_by_time", True):
            h = x.hours if x.hours > 0 else e * DEFAULT_EPISODE_HOURS
            base = min(P.get("series_max_weight", 1.0), math.log1p(h) / math.log1p(P.get("series_full_hours", 7.5)))
        else:
            base = min(P.get("series_max_weight", 1.0), math.log1p(e) / math.log1p(P["series_full_eps"]))
    g = lambda k, d: P.get(k, DEFAULTS.get(k, d))
    if x.hidden:                                         # « pas intéressé » : refus explicite
        return -g("hidden_weight", 1.0)
    fav = fav_part(x, P)
    if fav > 0:
        base = max(base, 0) + P["fav_boost"] * fav
    if x.rewatch > 0 and base > 0 and not x.backfill:    # revu : ne rattrape pas un abandon ; pas un marquage en masse
        base += g("rewatch_boost", 0.0) * min(1.0, x.rewatch)
    if x.rating is not None and x.rating != 0:           # note explicite, dans les deux sens
        r = max(-1.0, min(1.0, x.rating))
        base = max(base, 0) + g("rating_weight", 1.0) * r if r > 0 else min(base, 0) + g("rating_weight", 1.0) * r
    if not seen_here and base > 0:                       # aimé sans être vu ici : goût plus léger, sans oubli
        return base * g("liked_unseen", 0.5)
    if (x.requested > 0 or x.watchlisted > 0) and base == 0 and not x.played and x.episodes == 0 \
            and x.completion < P["zap"] and fav == 0:
        return max(_intent(x.requested, g("request_weight", 0.6), now, P), _intent(x.watchlisted, g("watchlist_weight", 0.4), now, P))
    if base > 0:
        age = max(0.0, (now - (x.last or now)) / DAY)
        if P.get("decay") == "power":
            w = 1.0 / (1.0 + age / P["half_life"])
        else:
            w = 0.5 ** (age / P["half_life"])
        base *= P["recency_floor"] if x.backfill else max(P["recency_floor"], w)   # date inconnue : goût ancien
    return base


def fav_part(x, P=DEFAULTS) -> float:
    """Part du bonus de favori : série 1 ; saison fav_season_share ; épisodes fav_episode_share × min(1, n/3)."""
    if x.fav:
        return 1.0
    s = (P.get("fav_season_share", DEFAULTS["fav_season_share"]) if x.fav_seasons > 0 else 0.0) \
        + P.get("fav_episode_share", DEFAULTS["fav_episode_share"]) * min(1.0, x.fav_episodes / 3.0)
    return min(1.0, s)


def _intent(at, weight, now, P):
    if at <= 0:
        return 0.0
    age = max(0.0, (now - at) / DAY)
    hl = P.get("request_half_life", DEFAULTS["request_half_life"])
    return 0.0 if age > P.get("request_max_days", DEFAULTS["request_max_days"]) else weight * 0.5 ** (age / hl)


# ---------------------------------------------------------------- contenu
FEAT_W = dict(g=1.0, t=0.5, s=0.35, a=0.7, d=1.3, w=0.6, c=1.0, y=0.5, k=0.6, n=0.0, l=0.0)
ACTORS = 8


def tokens(t) -> dict:
    f = {}
    for g in t.genres: f["g:" + g] = FEAT_W["g"]
    for x in t.tags: f["t:" + x.lower()] = FEAT_W["t"]
    for x in t.studios[:3]: f["s:" + x] = FEAT_W["s"]
    for x in (t.people.get("Actor") or [])[:ACTORS]: f["a:" + x] = FEAT_W["a"]
    for x in t.people.get("Director") or []: f["d:" + x] = FEAT_W["d"]
    for x in t.people.get("Writer") or []: f["w:" + x] = FEAT_W["w"]
    for x in t.people.get("Creator") or []: f["c:" + x] = FEAT_W["c"]
    if t.year: f["y:%d" % (t.year // 10 * 10)] = FEAT_W["y"]
    f["k:" + t.kind] = FEAT_W["k"]
    if FEAT_W["n"] > 0:                                   # facultatifs : absents quand leur poids est nul
        for x in t.countries[:3]: f["n:" + x] = FEAT_W["n"]
    if FEAT_W["l"] > 0 and t.language and t.language.strip():
        f["l:" + t.language.strip().lower()] = FEAT_W["l"]
    return f


WEIGHTING = dict(scheme="tfidf", k1=1.2, b=0.75)


def build_vectors(titles: dict) -> dict:
    raw = {tid: tokens(t) for tid, t in titles.items()}
    df = Counter(k for f in raw.values() for k in f)
    n = len(raw)
    avg = sum(len(f) for f in raw.values()) / max(n, 1)
    vec = {}
    for tid, f in raw.items():
        if WEIGHTING["scheme"] == "bm25":
            k1, b = WEIGHTING["k1"], WEIGHTING["b"]
            tf = (k1 + 1) / (1 + k1 * (1 - b + b * len(f) / avg))       # BM25 sur traits binaires
            v = {k: w * tf * math.log(1 + (n - df[k] + 0.5) / (df[k] + 0.5)) for k, w in f.items()
                 if df[k] < n * 0.5 or k.startswith("k:")}
        else:
            v = {k: w * math.log((n + 1) / (df[k] + 0.5)) for k, w in f.items() if df[k] < n * 0.5 or k.startswith("k:")}
        norm = math.sqrt(sum(x * x for x in v.values())) or 1.0
        vec[tid] = {k: x / norm for k, x in v.items()}
    return vec


def dot(a: dict, b: dict) -> float:
    if len(a) > len(b):
        a, b = b, a
    return sum(x * b.get(k, 0.0) for k, x in a.items())


# ---------------------------------------------------------------- collaboratif
def item_cooc(user_pos: dict, shrink: float = 0.0, min_common: int = 1) -> dict:
    """Cosinus item-item sur la matrice binaire utilisateur × titre (titres aimés), avec rétrécissement
    c / (√(ni·nj) + shrink) et un minimum de spectateurs communs (Ferrari Dacrema et al., 2019)."""
    cnt = Counter()
    pair = defaultdict(Counter)
    for items in user_pos.values():
        items = list(items)
        for i in items:
            cnt[i] += 1
        for i in items:
            for j in items:
                if i != j:
                    pair[i][j] += 1
    return {i: {j: c / (math.sqrt(cnt[i] * cnt[j]) + shrink) for j, c in row.items() if c >= min_common}
            for i, row in pair.items()}


# ---------------------------------------------------------------- recommandeurs
class Context:
    """Tout ce qui est connu à l'instant `now` (aucune information postérieure)."""

    def __init__(self, ds: Dataset, now: float, user_hist: dict, P=DEFAULTS):
        self.ds, self.now, self.P = ds, now, P
        self.hist = user_hist                            # user -> {tid: Interaction} (avant now)
        self.sig = {u: {tid: signal(x, ds.titles[tid].kind, ds.titles[tid].n_episodes, now, P)
                        for tid, x in h.items()} for u, h in user_hist.items()}
        self.pos = {u: {tid for tid, s in s_.items() if s > 0} for u, s_ in self.sig.items()}
        self.pop = Counter(t for s in self.pos.values() for t in s)
        self.cooc = item_cooc(self.pos, P.get("shrink", 0.0), P.get("min_common", 1))


def rec_random(ctx, user, cands, k, seed=0):
    c = sorted(cands)
    random.Random(seed).shuffle(c)
    return c[:k]


def rec_popular(ctx, user, cands, k):
    others = Counter(t for u, s in ctx.pos.items() if u != user for t in s)
    q = lambda t: ctx.ds.titles[t].community or 0
    return sorted(cands, key=lambda t: (-others[t], -q(t), t))[:k]


# --- réplique de Jellyfin 12 : MovieSimilarItemsProvider + SeriesSimilarItemsProvider + GetMovieRecommendations
JF_W = dict(g=10, t=5, s=5, d=50, a=15)


def jf_movie_score(a, b) -> int:
    s = JF_W["g"] * len(set(a.genres) & set(b.genres)) + JF_W["t"] * len(set(a.tags) & set(b.tags))
    s += JF_W["s"] * len(set(a.studios) & set(b.studios))
    s += JF_W["d"] * len(set(a.people.get("Director") or []) & set(b.people.get("Director") or []))
    s += JF_W["a"] * len(set(a.people.get("Actor") or []) & set(b.people.get("Actor") or []))
    return s


def rec_jellyfin(ctx, user, cands, k, seed=0):
    """Points de départ = 7 derniers films vus + favoris ; films : score pondéré de Jellyfin ;
    séries : séries au hasard partageant un genre ou un mot-clé ; fusion en tourniquet."""
    T = ctx.ds.titles
    rng = random.Random(seed)
    h = ctx.hist[user]
    movies = sorted((x for x in h.values() if T[x.title].kind == "Movie" and x.played), key=lambda x: -x.last)
    anchors = [x.title for x in movies[:7]]
    anchors += [x.title for x in h.values() if x.fav and T[x.title].kind == "Movie" and x.title not in anchors][:10]
    series = sorted((x for x in h.values() if T[x.title].kind == "Series" and x.played), key=lambda x: -x.last)
    anchors += [x.title for x in series[:3]]
    lists = []
    for a in anchors:
        ta = T[a]
        if ta.kind == "Movie":
            sc = [(jf_movie_score(ta, T[c]), rng.random(), c) for c in cands if T[c].kind == "Movie"]
            lists.append([c for s, _, c in sorted(sc, reverse=True) if s > 0])
        else:
            g, tg = set(ta.genres), set(ta.tags)
            m = [c for c in cands if T[c].kind == "Series" and (g & set(T[c].genres) or tg & set(T[c].tags))]
            rng.shuffle(m)
            lists.append(m)
    out, seen = [], set()
    for rnd in range(max((len(x) for x in lists), default=0)):
        for lst in lists:
            if rnd < len(lst) and lst[rnd] not in seen:
                seen.add(lst[rnd]); out.append(lst[rnd])
                if len(out) >= k:
                    return out
    return out


# --- moteur
ENGINE_W = dict(centroid=1.0, maxsim=2.0, collab=0.3, pop=0.5, quality=0.1, neg=0.6, fresh=2.0, fresh_days=45.0,
                gate="rank", gate_top=60, release=0.0, release_days=365.0, rating="community",
                rating_prior_votes=150.0, household=0.3, household_prior=2.0)


def household(ds, title, user, prior) -> float:
    """Avis du foyer (sans ceux du compte), moyenne bayésienne avec `prior` avis neutres (3), ramenée à -1..+1."""
    by = (ds.meta.get("reviews") or {}).get(title)
    if not by:
        return 0.0
    vals = [s for u, s in by.items() if u != user]
    if not vals:
        return 0.0
    return max(-1.0, min(1.0, ((sum(vals) + prior * 3.0) / (len(vals) + prior) - 3.0) / 2.0))


def quality(t, src="community", prior_votes=0.0) -> float:
    """Note sur 10 selon la source ; absente = 6,5. Une note nulle compte comme absente.
    La note de la communauté est ramenée vers 6,5 si elle repose sur peu de votes (moyenne bayésienne)."""
    comm = t.community if t.community else None
    if comm is not None and prior_votes > 0 and t.votes is not None and t.votes >= 0:
        comm = (t.votes * comm + prior_votes * 6.5) / (t.votes + prior_votes)
    crit = t.critic / 10.0 if t.critic else None
    if src == "critic":
        v = crit if crit is not None else comm
    elif src in ("best", "mean"):
        if comm is None or crit is None:
            v = crit if comm is None else comm
        else:
            v = max(comm, crit) if src == "best" else (comm + crit) / 2.0
    else:
        v = comm
    return 6.5 if v is None else v
MMR = 0.3


def score_engine(ctx, vec, user, cands, W=ENGINE_W, expo=None):
    T = ctx.ds.titles
    sig = ctx.sig[user]
    pos = {t: s for t, s in sig.items() if s > 0}
    neg = {t: -s for t, s in sig.items() if s < 0}
    prof = defaultdict(float)
    for t, s in pos.items():
        for f, x in vec[t].items():
            prof[f] += s * x
    for t, s in neg.items():
        for f, x in vec[t].items():
            prof[f] -= W["neg"] * s * x
    pn = math.sqrt(sum(x * x for x in prof.values())) or 1.0
    prof = {f: x / pn for f, x in prof.items()}
    # goût du moment : les `short_n` derniers titres aimés (dernière activité), profil des seuls aimés
    wst = W.get("short", 0.0)
    hist = ctx.hist.get(user, {})
    short = sorted(pos, key=lambda t: (-(hist[t].last if t in hist else 0), t))[: int(W.get("short_n", 5))] if wst > 0 else []
    stp = defaultdict(float)
    for t in short:
        for f, x in vec[t].items():
            stp[f] += pos[t] * x
    stn = math.sqrt(sum(x * x for x in stp.values())) or 1.0
    stp = {f: x / stn for f, x in stp.items()}
    expo = expo or {}
    # heure de la journée : titres aimés regardés dans la même tranche que maintenant
    tod = {}
    if W.get("tod", 0.0) > 0:
        slot = hour_of(ctx.now) // 6
        acc = defaultdict(float)
        for t, sv in pos.items():
            x = hist.get(t)
            if x is None or x.backfill:
                continue
            m = (0.5 if x.start and hour_of(x.start) // 6 == slot else 0.0) + \
                (0.5 if x.last and x.last <= ctx.now and hour_of(x.last) // 6 == slot else 0.0)
            if m > 0:
                for f, val in vec[t].items():
                    acc[f] += sv * m * val
        tn = math.sqrt(sum(v * v for v in acc.values())) or 1.0
        tod = {f: v / tn for f, v in acc.items()}
    tot = sum(pos.values()) or 1.0
    others_pop = Counter(t for u, s in ctx.pos.items() if u != user for t in s)
    maxpop = max(others_pop.values(), default=1)
    out, parts, feats = {}, {}, {}
    for c in cands:
        v = vec[c]
        cen = dot(prof, v)
        ms = max((s * dot(vec[t], v) for t, s in pos.items()), default=0.0) / (max(pos.values(), default=1.0))
        col = sum(s * ctx.cooc.get(t, {}).get(c, 0.0) for t, s in pos.items()) / tot
        neg_pen = max((s * dot(vec[t], v) for t, s in neg.items()), default=0.0)
        pop = math.log1p(others_pop[c]) / math.log1p(maxpop)
        q = max(-1.0, min(1.0, (quality(T[c], W.get("rating", "community"), W.get("rating_prior_votes", 150.0)) - 6.5) / 2.0))
        hh = household(ctx.ds, c, user, W.get("household_prior", 2.0))
        rel = T[c].released or 0
        re_ = math.exp(-max(0.0, (ctx.now - rel) / DAY) / W.get("release_days", 365.0)) if rel else 0.0
        age = max(0.0, (ctx.now - (T[c].added or 0)) / DAY)
        fr = math.exp(-age / W["fresh_days"]) if T[c].added else 0.0
        st = dot(stp, v) if short else 0.0
        taste = W["centroid"] * cen + W["maxsim"] * ms + W["collab"] * col - W["neg"] * neg_pen + wst * st \
            + (W.get("tod", 0.0) * dot(tod, v) if tod else 0.0)
        if W.get("gate") == "rank":
            parts[c] = (taste, fr, pop, q, re_, hh)
            feats[c] = (cen, ms, col, -neg_pen, st)
        elif W.get("gate"):
            # nouveauté et popularité ne comptent que pour ce qui correspond déjà aux goûts
            tp = max(taste, 0.0)
            out[c] = taste + W["fresh"] * fr * tp + W["pop"] * pop * tp + W["quality"] * q
        else:
            out[c] = taste + W["pop"] * pop + W["quality"] * q + W["fresh"] * fr
    if parts:
        out = gate_rank({c: p[0] for c, p in parts.items()}, {c: p[1] for c, p in parts.items()},
                        {c: p[2] for c, p in parts.items()}, {c: p[3] for c, p in parts.items()}, W,
                        {c: p[4] for c, p in parts.items()}, {c: p[5] for c, p in parts.items()})
    net = W.get("neural")
    if parts and net and W.get("w_neural", 1.0) > 0:
        # correction apprise (réseau 10 → 8 → 1) sur les 10 ingrédients, garde-fou compris
        order = sorted(parts, key=lambda c: -parts[c][0])
        top = max(1, int(W.get("gate_top", 60)))
        rank = {c: i for i, c in enumerate(order)}
        for c, p in parts.items():
            b = max(p[0], 0.0) * max(0.0, 1.0 - rank[c] / top)
            x = list(feats[c]) + [b * p[1], b * p[2], b * p[3], b * p[4], b * p[5]]
            out[c] += W.get("w_neural", 1.0) * residual(net, x)
    for c in out:
        if c in expo:
            out[c] -= impression_penalty(expo[c], ctx.now, W) * abs(out[c])
    return out


def hour_of(t) -> int:
    """Heure UTC d'un instant (le plugin utilise l'heure locale du serveur)."""
    return int((t % 86400) // 3600)


NEURAL_HIDDEN = 8


def residual(net, x) -> float:
    """Réseau 10 → 8 → 1 (tanh) : [poids d'entrée 8×10, biais 8, poids de sortie 8]."""
    K, H = len(x), NEURAL_HIDDEN
    r = 0.0
    for h in range(H):
        a = net[H * K + h]
        for k in range(K):
            a += net[h * K + k] * x[k]
        r += net[H * K + H + h] * math.tanh(a)
    return r


def impression_penalty(e, now, W) -> float:
    """Part retirée du score d'un titre montré sans être ouvert (jours d'affichage, dernier affichage)."""
    days, last = e
    w = W.get("imp", 0.4)
    if w <= 0 or days <= 0:
        return 0.0
    age = max(0.0, (now - last) / DAY)
    return w * (1.0 - math.exp(-days / W.get("imp_days", 7.0))) * 0.5 ** (age / W.get("imp_recovery", 30.0))


def gate_rank(taste, fr, pop, q, W, rel=None, hh=None):
    """Bonus (nouveauté, popularité, qualité) MULTIPLICATIFS et réservés au haut du classement par goût :
    un titre hors des `gate_top` meilleurs par goût n'en reçoit aucun, quelle que soit sa nouveauté."""
    order = sorted(taste, key=lambda c: -taste[c])
    top = max(1, int(W.get("gate_top", 60)))
    rank = {c: i for i, c in enumerate(order)}
    out = {}
    for c, t in taste.items():
        g = max(0.0, 1.0 - rank[c] / top)            # 1 au sommet, 0 au-delà de gate_top
        tp = max(t, 0.0)
        out[c] = t + tp * g * (W["fresh"] * fr[c] + W["pop"] * pop[c] + W["quality"] * q[c] + 0.0
                               + W.get("release", 0.0) * (rel[c] if rel else 0.0)
                               + W.get("household", 0.3) * (hh[c] if hh else 0.0))
    return out


SAGA_MAX = 12          # au-delà, une collection est un regroupement thématique, pas une suite


def franchise(T, colls, t):
    """Collection la plus précise (la plus petite, ≥ 2 titres) contenant t, ou None."""
    best = None
    for c in T[t].colls:
        n = len(colls.get(c, ()))
        if n >= 2 and (best is None or n < best[0]):
            best = (n, c)
    return best


PERSONAL_FACTORS = (1.0, 0.5, 2.0, 0.0, 4.0)
SHOWN_FOR_VARIETY = 20


def history_similarity(ctx, vec, user):
    """Ressemblance moyenne (pondérée) entre les 30 titres aimés les plus forts ; None s'il y en a moins de 3."""
    sig = ctx.sig.get(user, {})
    pos = sorted(((t, s) for t, s in sig.items() if s > 0 and t in vec), key=lambda e: (-e[1], e[0]))[:30]
    if len(pos) < 3:
        return None
    num = den = 0.0
    for i in range(len(pos)):
        for j in range(i + 1, len(pos)):
            ww = pos[i][1] * pos[j][1]
            num += ww * dot(vec[pos[i][0]], vec[pos[j][0]])
            den += ww
    return num / den if den > 0 else None


def list_similarity(vec, l):
    s, n = 0.0, 0
    for i in range(len(l)):
        for j in range(i + 1, len(l)):
            s += dot(vec[l[i]], vec[l[j]])
            n += 1
    return s / n if n else 0.0


def genre_distribution(ctx, user):
    p, tot = defaultdict(float), 0.0
    for t, s in sorted(ctx.sig.get(user, {}).items()):
        tt = ctx.ds.titles.get(t)
        if s <= 0 or tt is None or not tt.genres:
            continue
        gs = list(dict.fromkeys(tt.genres))
        for g in gs:
            p[g] += s / len(gs)
        tot += s
    return {g: v / tot for g, v in p.items()}


class MmrPicker:
    def __init__(self, sc, vec, lam):
        self.sc, self.vec, self.lam, self.pen = sc, vec, lam, {}

    def pick(self, pool):
        bi, bv = 0, -math.inf
        for i, c in enumerate(pool):
            val = self.sc[c] - self.lam * self.pen.get(c, 0.0)
            if val > bv:
                bi, bv = i, val
        return bi

    def added(self, t, pool):
        if self.lam <= 0:
            return
        for c in pool:
            s = dot(self.vec[c], self.vec[t])
            if c not in self.pen or s > self.pen[c]:
                self.pen[c] = s


class CalibratedPicker:
    """Steck (RecSys 2018) : (1 − λ) score normalisé − λ KL(p ‖ (1 − α) q + α p)."""
    ALPHA = 0.01

    def __init__(self, sc, titles, p, lam, pool):
        self.sc, self.titles, self.p, self.lam = sc, titles, p, lam
        self.genres = sorted(p)
        self.q, self.n = defaultdict(float), 0
        self.min = min((sc[c] for c in pool), default=0.0)
        self.span = max((sc[c] for c in pool), default=0.0) - self.min

    def kl(self, extra):
        eg = list(dict.fromkeys(extra.genres)) if extra is not None else []
        n = self.n + (1 if eg else 0)
        kl = 0.0
        for g in self.genres:
            q = self.q.get(g, 0.0) + (1.0 / len(eg) if g in eg else 0.0)
            q = q / n if n > 0 else 0.0
            pg = self.p[g]
            kl += pg * math.log(pg / ((1 - self.ALPHA) * q + self.ALPHA * pg))
        return kl

    def pick(self, pool):
        bi, bv = 0, -math.inf
        for i, c in enumerate(pool):
            sn = (self.sc[c] - self.min) / self.span if self.span > 0 else 0.0
            val = (1 - self.lam) * sn - self.lam * self.kl(self.titles[c])
            if val > bv:
                bi, bv = i, val
        return bi

    def added(self, t, pool):
        gs = list(dict.fromkeys(self.titles[t].genres))
        if not gs:
            return
        self.n += 1
        for g in gs:
            self.q[g] += 1.0 / len(gs)


def _greedy(ctx, vec, cands, pool, k, saga, picker):
    T = ctx.ds.titles
    colls = ctx.ds.meta.get("colls", {})
    cset = set(cands)
    pool, out, used = list(pool), [], Counter()
    while pool and len(out) < k:
        best = pool.pop(picker.pick(pool))
        if saga:
            fr = franchise(T, colls, best)
            if fr:
                n, cid = fr
                if used[cid] >= (1 if n <= SAGA_MAX else 2):
                    continue
                if n <= SAGA_MAX:
                    first = min((t for t in colls[cid] if t in cset and T[t].kind == T[best].kind),
                                key=lambda t: (T[t].year or 9999, T[t].name))
                    if first not in out:
                        best = first
                used[cid] += 1
        if best not in out:
            out.append(best)
            picker.added(best, pool)
    return out


def rec_engine(ctx, vec, user, cands, k, W=ENGINE_W, mmr=0.0, saga=True, sc=None, div="fixed", cal_strength=0.5):
    """Classement + diversité + règle de saga :
    - une seule entrée par saga (deux par regroupement thématique) ;
    - pour une saga, on propose le premier volet pas encore vu, pas un volet au milieu ;
    - diversité fixe (MMR), personnalisée (la variété de la liste suit celle de l'historique) ou calibrée (genres)."""
    if sc is None:
        sc = score_engine(ctx, vec, user, cands, W)
    ranked = sorted(cands, key=lambda c: -sc[c])
    if div == "fixed" and mmr <= 0 and not saga:
        return ranked[:k]
    pool = ranked[: k * 8]
    if div == "personalized":
        target = history_similarity(ctx, vec, user)
        if target is not None:
            best, gap = None, math.inf
            for f in PERSONAL_FACTORS:
                l = _greedy(ctx, vec, cands, pool, k, saga, MmrPicker(sc, vec, mmr * f))
                g = abs(list_similarity(vec, l[:SHOWN_FOR_VARIETY]) - target)
                if g < gap - 1e-12:
                    best, gap = l, g
            return best
    if div == "calibrated":
        p = genre_distribution(ctx, user)
        if p:
            return _greedy(ctx, vec, cands, pool, k, saga, CalibratedPicker(sc, ctx.ds.titles, p, cal_strength, pool))
    return _greedy(ctx, vec, cands, pool, k, saga, MmrPicker(sc, vec, mmr))
