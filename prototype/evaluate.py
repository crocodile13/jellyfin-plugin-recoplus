"""Banc d'évaluation temporel : à chaque nouveau titre adopté par un utilisateur,
on demande aux recommandeurs une liste à partir de ce qui était connu juste avant,
et on regarde si ce titre y figurait (rang).

« Adopté » = film vu en entier, ou série dont au moins 3 épisodes ont été vus."""
from __future__ import annotations

import math
import statistics as st
import sys
from collections import defaultdict

from data import load
from engine import (Context, ENGINE_W, build_vectors, rec_engine, rec_jellyfin, rec_popular, rec_random)

MIN_TRAIN = 4          # nombre de titres adoptés avant de commencer à évaluer
KS = (10, 20)


def adopted(x, kind):
    return x.played if kind == "Movie" else x.episodes >= 3


def events(ds):
    ev = []
    for u, h in ds.users.items():
        ad = sorted((x for x in h.values() if adopted(x, ds.titles[x.title].kind) and not x.backfill), key=lambda x: x.start)
        for i, x in enumerate(ad):
            if i >= MIN_TRAIN and x.start:
                ev.append((u, x.title, x.start))
    return ev


def snapshot(ds, now):
    return {u: {t: x for t, x in h.items() if x.backfill or (x.start and x.start < now)} for u, h in ds.users.items()}


def run(ds, methods, verbose=False):
    vec = build_vectors(ds.titles)
    ev = events(ds)
    ranks = defaultdict(list)                 # méthode -> [(user, rang ou None)]
    for u, target, t0 in ev:
        hist = snapshot(ds, t0 - 1)
        ctx = Context(ds, t0 - 1, hist)
        seen = set(hist[u])
        cands = [t for t in ds.titles if t not in seen and (ds.titles[t].added or 0) <= t0]
        for name, fn in methods.items():
            lst = fn(ctx, vec, u, cands, max(KS) * 5)
            r = lst.index(target) + 1 if target in lst else None
            ranks[name].append((u, r))
    return ev, ranks


def summarize(ranks, title=""):
    rows = []
    for name, rs in ranks.items():
        n = len(rs)
        hit = {k: sum(1 for _, r in rs if r and r <= k) / n for k in KS}
        mrr = sum(1 / r for _, r in rs if r) / n
        per_user = defaultdict(list)
        for u, r in rs:
            per_user[u].append(1 if r and r <= 10 else 0)
        macro = st.mean(st.mean(v) for v in per_user.values())
        rows.append((name, n, hit[10], hit[20], mrr, macro))
    print(title)
    print(f"  {'méthode':<26} {'n':>4} {'hit@10':>7} {'hit@20':>7} {'MRR':>6} {'hit@10/cpt':>11}")
    for name, n, h10, h20, mrr, mac in rows:
        print(f"  {name:<26} {n:>4} {h10:>7.3f} {h20:>7.3f} {mrr:>6.3f} {mac:>11.3f}")
    return rows


def default_methods(W=ENGINE_W):
    return {
        "hasard (Suggestions)": lambda c, v, u, cs, k: rec_random(c, u, cs, k, seed=hash(u) & 0xffff),
        "popularité": lambda c, v, u, cs, k: rec_popular(c, u, cs, k),
        "Jellyfin 12 (réplique)": lambda c, v, u, cs, k: rec_jellyfin(c, u, cs, k, seed=1),
        "moteur": lambda c, v, u, cs, k: rec_engine(c, v, u, cs, k, W),
    }


if __name__ == "__main__":
    ds = load(sys.argv[1])
    ev, ranks = run(ds, default_methods())
    by_user = defaultdict(int)
    for u, *_ in ev:
        by_user[u] += 1
    print(f"{len(ev)} événements d'évaluation sur {len(by_user)} comptes : {dict(by_user)}")
    summarize(ranks)
