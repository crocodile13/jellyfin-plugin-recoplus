"""Évaluation sur la LISTE réellement affichée (diversité + règle de saga), pas seulement sur le score brut.
Une saga compte comme touchée si le titre adopté OU un volet de la même saga est proposé
(variante « stricte » : seul le titre exact compte)."""
import sys
from collections import defaultdict
import numpy as np
import engine as EN

exec(open('tune.py').read().split('even = [')[0])          # charge ds, cache, FAST, evaluate, metrics

def lists(W, P, mmr, saga, k=20):
    out = []
    for u, target, t0, ctx, cands in cache:
        ctx.P = P
        ctx.sig = {uu: {tid: EN.signal(x, ds.titles[tid].kind, ds.titles[tid].n_episodes, ctx.now, P)
                        for tid, x in h.items()} for uu, h in ctx.hist.items()}
        ctx.pos = {uu: {t for t, v in ss.items() if v > 0} for uu, ss in ctx.sig.items()}
        ctx.cooc = EN.item_cooc(ctx.pos)
        FAST.cands_ix = np.array([FAST.ix[c] for c in cands])
        s = FAST.scores(ctx, u, W)
        sc = {c: s[FAST.ix[c]] for c in cands}
        out.append((u, target, EN.rec_engine(ctx, FAST.vec, u, cands, k, W, mmr=mmr, saga=saga, sc=sc)))
    return out

def score(res, strict):
    colls = ds.meta["colls"]
    h10 = h20 = 0
    for u, target, lst in res:
        fr = EN.franchise(ds.titles, colls, target)
        same = set(colls[fr[1]]) if (fr and fr[0] <= EN.SAGA_MAX and not strict) else {target}
        pos = [i for i, t in enumerate(lst) if t in same]
        h10 += bool(pos and pos[0] < 10); h20 += bool(pos and pos[0] < 20)
    n = len(res)
    return h10 / n, h20 / n
