"""Banc réaliste : une trentaine de comptes générés (graine fixe : reproductible) à partir de profils mélangés, avec des
comportements variés, et une mesure objective en plus de la lecture humaine.

Chaque compte a un mélange de goûts (univers de universes.py, avec des poids), un niveau d'activité, une part de
visionnages hors de ses goûts (le bruit), des abandons, des favoris, des « je n'aime pas », des séries dévorées ou
lâchées, un historique étalé sur plus d'un an. Cas particuliers : import en masse (« marquer comme vu » sur une liste
d'un coup), compte familial partagé, goûts qui changent, favoris seuls (vus ailleurs).

Mesure : les 3 derniers titres vraiment regardés de chaque compte sont gardés hors de l'historique simulé ; après le
calcul, à quel rang Reco+ les proposait-il ? Comparé à une base « ce que le foyer regarde » (popularité), calculée ici
sur les mêmes données. Plus la part du début de « Pour toi » qui tombe dans les goûts du compte.
"""
from __future__ import annotations

import random

from universes import U

# (nom, univers et poids, activité : titres regardés, bruit : part hors goûts)
PROFILES = [
    ("otaku", {"ghibli_anime": .8, "scifi": .2}),
    ("pixar", {"famille": .85, "comedie": .15}),
    ("marvel", {"superheros": .6, "action": .3, "scifi": .1}),
    ("frissons", {"horreur": .75, "polar": .25}),
    ("polar", {"polar": .7, "drame": .2, "coree": .1}),
    ("cinephile", {"drame": .35, "coree": .25, "cinema_fr": .25, "polar": .15}),
    ("sf", {"scifi": .7, "fantasy": .2, "superheros": .1}),
    ("fantasy", {"fantasy": .7, "famille": .15, "scifi": .15}),
    ("romcom", {"romance": .6, "comedie": .3, "drame": .1}),
    ("sitcom", {"comedie": .8, "romance": .2}),
    ("docu", {"documentaire": .8, "drame": .2}),
    ("francophile", {"cinema_fr": .7, "comedie": .2, "drame": .1}),
    ("kdrama", {"coree": .7, "romance": .3}),
]
LEVELS = {"gros": (34, .08), "moyen": (16, .12), "petit": (6, .15)}
HOLDOUT = 3


def _pool(weights):
    items = []
    for u, w in weights.items():
        for t, y, k in U[u]:
            items.append((t, y, k, u, w))
    return items


def _pick(rng, weights, n, exclude):
    """n titres tirés selon les poids des univers (sans doublon)."""
    pool = [x for x in _pool(weights) if (x[0], x[1]) not in exclude]
    out = []
    while pool and len(out) < n:
        tot = sum(x[4] for x in pool)
        r, acc = rng.random() * tot, 0.0
        for i, x in enumerate(pool):
            acc += x[4]
            if acc >= r:
                out.append(x)
                exclude.add((x[0], x[1]))
                pool = [p for p in pool if (p[0], p[1]) != (x[0], x[1])]
                break
    return out


def _account(rng, weights, n, noise, span=400, start=2):
    """Historique d'un compte : (watched, episodes, abandoned, favorites, dislikes, holdout)."""
    taken: set = set()
    liked = _pick(rng, weights, n + HOLDOUT, taken)
    others = {u: 1.0 for u in U if u not in weights}
    noisy = _pick(rng, others, max(1, round(n * noise)), taken) if others else []
    # dates : plus récentes plus denses ; les gardés de côté sont les plus récents
    days = sorted((start + span * (rng.random() ** 1.6) for _ in liked), reverse=True)
    liked = list(zip(liked, days))
    holdout = [x for x, _ in liked[-HOLDOUT:]]
    liked = liked[:-HOLDOUT]
    watched, episodes, abandoned, favorites, dislikes = [], [], [], [], []
    for (t, y, k, u, w), d in liked:
        r = rng.random()
        if k == "s":
            if r < .2:
                episodes.append((t, rng.choice([1, 2]), round(d) + 35))      # lâchée après 1-2 épisodes, il y a longtemps
            elif r < .5:
                episodes.append((t, rng.randint(3, 7), round(d)))            # en cours
            else:
                watched.append((t, round(d)))                                # dévorée
        elif r < .1:
            abandoned.append((t, round(rng.uniform(.1, .5), 2), round(d) + 35))
        else:
            watched.append((t, round(d)))
        if rng.random() < .12:
            favorites.append(t)
    for (t, y, k, u, w) in noisy:
        d = round(start + span * rng.random())
        if rng.random() < .5:
            (abandoned if k == "m" else episodes).append((t, .2, d + 35) if k == "m" else (t, 1, d + 35))
            if rng.random() < .4:
                dislikes.append(t)
        else:
            watched.append((t, d))
    return watched, episodes, abandoned, favorites, dislikes, [(t, y, k) for t, y, k, u, w in holdout]


def personas(seed=20261123):
    """Comptes générés : chaque profil en gros et petit spectateur, plus des cas particuliers."""
    rng = random.Random(seed)
    out = {}
    for name, weights in PROFILES:
        for level in ("gros", "petit") if name in ("otaku", "polar", "pixar", "sf", "romcom", "docu") else ("moyen",):
            n, noise = LEVELS[level]
            w, e, a, f, dl, h = _account(rng, weights, n, noise)
            out[f"b_{name}_{level}"] = {
                "attendu": "goûts : " + ", ".join(f"{u} {round(p * 100)} %" for u, p in weights.items()),
                "univers": weights, "watched": w, "episodes": e, "abandoned": a, "favorites": f, "dislikes": dl, "holdout": h,
            }
    # import en masse : 12 titres marqués vus d'un coup (dates inconnues), puis quelques vrais visionnages
    w, e, a, f, dl, h = _account(rng, {"polar": .6, "drame": .4}, 16, .05)
    bulk = [t for t, _ in w[:12]]
    out["b_import_masse"] = {"attendu": "polar et drame ; 12 titres importés d'un coup comptent comme des goûts anciens",
                             "univers": {"polar": .6, "drame": .4}, "watched": [(t, 300) for t in bulk] + w[12:], "episodes": e,
                             "abandoned": a, "favorites": f, "dislikes": dl, "holdout": h, "bulk": bulk}
    # compte familial partagé : enfants et parents sur le même compte
    mix = {"famille": .5, "horreur": .25, "romance": .25}
    w, e, a, f, dl, h = _account(rng, mix, 24, .05)
    out["b_famille_partagee"] = {"attendu": "mélange enfants (famille) et parents (horreur, romance) : les deux doivent apparaître",
                                 "univers": mix, "watched": w, "episodes": e, "abandoned": a, "favorites": f, "dislikes": dl, "holdout": h}
    # goûts qui changent : polar il y a longtemps, anime depuis deux mois
    w1, e1, a1, f1, d1, _ = _account(rng, {"polar": 1.0}, 12, 0, span=200, start=200)
    w2, e2, a2, f2, d2, h2 = _account(rng, {"ghibli_anime": 1.0}, 8, 0, span=50, start=2)
    out["b_gout_change"] = {"attendu": "polar ancien, anime récent : l'anime doit dominer le début, le polar rester présent",
                            "univers": {"ghibli_anime": .7, "polar": .3}, "watched": w1 + w2, "episodes": e1 + e2, "abandoned": a1 + a2,
                            "favorites": f1 + f2, "dislikes": d1 + d2, "holdout": h2}
    # favoris seuls : aimé ailleurs, rien regardé ici
    fav = [t for t, y, k, u, w in _pick(rng, {"scifi": .6, "fantasy": .4}, 8, set())]
    out["b_favoris_seuls"] = {"attendu": "science-fiction et fantasy aimées ailleurs (favoris seulement)",
                              "univers": {"scifi": .6, "fantasy": .4}, "watched": [], "episodes": [], "abandoned": [], "favorites": fav,
                              "dislikes": [], "holdout": []}
    return out


def universe_of():
    """Titre → univers auxquels il appartient."""
    res: dict = {}
    for u, items in U.items():
        for t, y, k in items:
            res.setdefault(t, set()).add(u)
    return res
