"""Réglage : grille sur les poids du moteur, validation croisée 2 plis (événements pairs / impairs)
pour ne pas choisir des réglages qui ne collent qu'au hasard des données."""
from __future__ import annotations

import itertools
import math
import sys
from collections import defaultdict

import engine as EN
from data import load
from evaluate import events, snapshot

ds = load(sys.argv[1])
ev = events(ds)
cache = []
for u, target, t0 in ev:
    hist = snapshot(ds, t0 - 1)
    ctx = EN.Context(ds, t0 - 1, hist)
    cands = [t for t in ds.titles if t not in hist[u] and (ds.titles[t].added or 0) <= t0]
    cache.append((u, target, t0, ctx, cands))


from fast import Fast, verify
FAST = Fast(ds)
_e = cache[len(cache) // 2]
print("écart max version matricielle / référence :", verify(ds, _e[3], _e[0], _e[4]))


def evaluate(W, P, feat=None):
    if feat and any(EN.FEAT_W[k] != v for k, v in feat.items()):
        EN.FEAT_W.update(feat)
        FAST.set_features()
    res = []
    for u, target, t0, ctx, cands in cache:
        ctx.P = P
        ctx.sig = {uu: {tid: EN.signal(x, ds.titles[tid].kind, ds.titles[tid].n_episodes, ctx.now, P)
                        for tid, x in h.items()} for uu, h in ctx.hist.items()}
        ctx.pos = {uu: {t for t, v in ss.items() if v > 0} for uu, ss in ctx.sig.items()}
        ctx.cooc = EN.item_cooc(ctx.pos)
        sc = FAST.scores(ctx, u, W)
        ci = [FAST.ix[c] for c in cands]
        tv = sc[FAST.ix[target]]
        rank = 1 + int((sc[ci] > tv).sum())
        res.append((u, rank))
    return res


def metrics(res, idx):
    sub = [res[i] for i in idx]
    n = len(sub)
    h10 = sum(1 for _, r in sub if r <= 10) / n
    h20 = sum(1 for _, r in sub if r <= 20) / n
    mrr = sum(1 / r for _, r in sub) / n
    pu = defaultdict(list)
    for u, r in sub:
        pu[u].append(r <= 10)
    mac = sum(sum(v) / len(v) for v in pu.values()) / len(pu)
    return h10, h20, mrr, mac


even = [i for i in range(len(ev)) if i % 2 == 0]
odd = [i for i in range(len(ev)) if i % 2 == 1]
grid = []
for hl, cen, ms, col, pop, fresh in itertools.product(
        (90, 365), (0.5, 1.0), (1.0, 2.0, 3.0), (0.0, 0.6), (0.2, 0.4, 0.8), (0.0, 0.3, 0.6, 1.2)):
    P = dict(EN.DEFAULTS, half_life=hl)
    W = dict(EN.ENGINE_W, centroid=cen, maxsim=ms, collab=col, pop=pop, fresh=fresh)
    res = evaluate(W, P, None)
    a, b = metrics(res, even), metrics(res, odd)
    grid.append(((hl, cen, ms, col, pop, fresh), a, b, metrics(res, range(len(ev)))))
EN.FEAT_W.update(d=1.3, a=0.7)
# choisi sur les pairs, jugé sur les impairs (et inversement)
for name, fit, test in (("pairs→impairs", 1, 2), ("impairs→pairs", 2, 1)):
    best = max(grid, key=lambda g: (g[fit][1] + g[fit][0], g[fit][2]))
    print(f"{name}: réglage {best[0]}  apprentissage hit@10 {best[fit][0]:.3f} hit@20 {best[fit][1]:.3f}"
          f"  | test hit@10 {best[test][0]:.3f} hit@20 {best[test][1]:.3f} MRR {best[test][2]:.3f}")
print("10 meilleurs réglages sur l'ensemble (hit@10, hit@20, MRR, hit@10/compte) :")
for g in sorted(grid, key=lambda g: -(g[3][0] + g[3][1]))[:10]:
    print("  ", g[0], tuple(round(x, 3) for x in g[3]))
