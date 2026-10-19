"""Génère un jeu de référence synthétique (aucune donnée réelle) et les résultats attendus du prototype,
pour vérifier que le moteur C# donne exactement les mêmes valeurs."""
import json
import random
import sys

import engine as EN
from data import Dataset, Interaction, Title

DAY = 86400.0


def synth(seed=7):
    rng = random.Random(seed)
    genres = ["Animation", "Comédie", "Drame", "Science-Fiction & Fantastique", "Action", "Romance", "Horreur", "Documentaire"]
    tags = [f"tag{i}" for i in range(40)] + ["Based On Manga", "Anime"]
    studios = [f"Studio {i}" for i in range(12)]
    actors = [f"Acteur {i}" for i in range(80)]
    directors = [f"Réal {i}" for i in range(15)]
    now = 1_790_000_000.0
    titles = {}
    for i in range(90):
        kind = "Series" if i % 3 == 0 else "Movie"
        tid = f"{i:032x}"
        people = {"Actor": rng.sample(actors, rng.randint(0, 12))}
        if kind == "Movie":
            people["Director"] = rng.sample(directors, rng.randint(0, 2))
            people["Writer"] = rng.sample(directors, rng.randint(0, 1))
        else:
            people["Creator"] = rng.sample(directors, rng.randint(0, 1))
        titles[tid] = Title(tid, kind, f"Titre {i:02d}", rng.choice([None, 1985, 1999, 2004, 2013, 2019, 2024, 2026]),
                            rng.sample(genres, rng.randint(1, 3)), rng.sample(tags, rng.randint(0, 6)),
                            rng.sample(studios, rng.randint(0, 4)), people,
                            rng.choice([None, 0, 5.1, 6.5, 7.2, 8.4, 9.1]),
                            rng.choice([None, now - rng.uniform(0, 400) * DAY, now - rng.uniform(0, 30) * DAY]),
                            n_episodes=rng.randint(1, 60) if kind == "Series" else 0)
    ids = list(titles)
    colls = {}
    saga = ids[1:9:3]                       # 3 films en saga
    colls["c" * 32] = saga
    colls["d" * 32] = ids[10:40:2][:14]     # regroupement thématique (> 12)
    colls["e" * 32] = [ids[50]]             # collection d'un seul titre (ignorée)
    colls["f" * 32] = ids[60:63]            # mélange films/série
    for cid, items in colls.items():
        for k in items:
            titles[k].colls.append(cid)
    users = {}
    for u in range(7):
        h = {}
        for tid in rng.sample(ids, rng.randint(0 if u == 6 else 5, 30)):
            t = titles[tid]
            last = rng.choice([0, now - rng.uniform(0, 500) * DAY, now - rng.uniform(0, 20) * DAY])
            start = last - rng.uniform(0, 30) * DAY if last else 0
            if t.kind == "Movie":
                played = rng.random() < 0.6
                comp = 1.0 if played else rng.choice([0.0, 0.02, 0.3, 0.5, 0.8])
                h[tid] = Interaction(tid, start, last, comp, played, plays=1 if played else 0, fav=rng.random() < 0.15)
            else:
                eps = rng.choice([0, 1, 2, 3, 8, 20])
                n = max(t.n_episodes, 1)
                h[tid] = Interaction(tid, start, last, min(1.0, eps / n), eps > 0, episodes=eps, plays=eps,
                                     fav=rng.random() < 0.15)
        users[f"u{u}"] = h
    ds = Dataset(titles, users, {"now": now, "colls": {c: [k for k in v if k in titles] for c, v in colls.items()}})
    return ds


def run(ds, P, W, scheme, mmr, k=12):
    EN.WEIGHTING["scheme"] = scheme
    vec = EN.build_vectors(ds.titles)
    ctx = EN.Context(ds, ds.meta["now"], ds.users, P)
    out = {"signals": ctx.sig, "cooc": {i: r for i, r in list(ctx.cooc.items())[:10]}, "vectors": {}, "users": {}}
    for tid in list(ds.titles)[:6]:
        out["vectors"][tid] = vec[tid]
    for u in ds.users:
        cands = [t for t in ds.titles if t not in ds.users[u]]
        sc = EN.score_engine(ctx, vec, u, cands, W)
        lst = EN.rec_engine(ctx, vec, u, cands, k, W, mmr=mmr, saga=True, sc=sc)
        out["users"][u] = {"cands": cands, "scores": sc, "list": lst}
    return out


def dump_ds(ds):
    return {
        "now": ds.meta["now"],
        "collections": ds.meta["colls"],
        "titles": [dict(id=t.id, kind=t.kind, name=t.name, year=t.year, genres=t.genres, tags=t.tags, studios=t.studios,
                        people=t.people, community=t.community, added=t.added, episodes=t.n_episodes)
                   for t in ds.titles.values()],
        "users": {u: [dict(title=x.title, start=x.start, last=x.last, completion=x.completion, played=x.played,
                           episodes=x.episodes, plays=x.plays, fav=x.fav) for x in h.values()] for u, h in ds.users.items()},
    }


if __name__ == "__main__":
    ds = synth()
    cases = []
    base_P = dict(EN.DEFAULTS)
    base_W = dict(EN.ENGINE_W)
    variants = [
        ("défaut", base_P, base_W, "tfidf", EN.MMR),
        ("puissance+bm25+sans rétrécissement", dict(base_P, decay="power", shrink=0.0, min_common=1, half_life=200),
         dict(base_W, fresh=1, gate_top=15, quality=0.7), "bm25", 0.0),
        ("abandons marqués", dict(base_P, abandon_days=10, abandon_weight=1.2, fav_boost=0.4),
         dict(base_W, neg=1.5, centroid=0.5, collab=1.0), "tfidf", 0.8),
    ]
    for name, P, W, scheme, mmr in variants:
        cases.append({"name": name, "P": P, "W": W, "scheme": scheme, "mmr": mmr, "expected": run(ds, P, W, scheme, mmr)})
    EN.WEIGHTING["scheme"] = "tfidf"
    # évaluation temporelle de référence (réglage par défaut, liste intégrée MMR + saga, k = 20)
    from evaluate import events, snapshot
    vec = EN.build_vectors(ds.titles)
    evs = []
    for u, target, t0 in events(ds):
        hist = snapshot(ds, t0 - 1)
        ctx = EN.Context(ds, t0 - 1, hist, base_P)
        cands = [t for t in ds.titles if t not in hist[u] and (ds.titles[t].added or 0) <= t0]
        sc = EN.score_engine(ctx, vec, u, cands, base_W)
        lst = EN.rec_engine(ctx, vec, u, cands, 20, base_W, mmr=EN.MMR, saga=True, sc=sc)
        r = lst.index(target) if target in lst else -1
        evs.append({"user": u, "target": target, "t0": t0, "hit10": 0 <= r < 10, "hit20": 0 <= r < 20})
    print("événements synthétiques :", len(evs), "hit10", sum(e["hit10"] for e in evs))
    json.dump({"dataset": dump_ds(ds), "cases": cases, "evaluation": evs}, open(sys.argv[1], "w"), ensure_ascii=False)
    print("cas :", [c["name"] for c in cases])
