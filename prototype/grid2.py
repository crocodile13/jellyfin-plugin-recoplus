"""Variantes issues de la littérature, évaluées sur la liste affichée, contre le réglage retenu."""
import sys, itertools, pickle, random
import numpy as np
exec(open('grid_rank.py').read().split('if __name__')[0].replace("for u, target, t0, ctx, cands in cache:                      # signaux", "for _ in ():  #"))
W = dict(EN.ENGINE_W, centroid=1, maxsim=2, collab=0.3, fresh_days=45, gate="rank", gate_top=60, fresh=2, quality=0.1, pop=0.5)

def prepare(P):
    for u, target, t0, ctx, cands in cache:
        PP = dict(P, half_life=EN.auto_half_life(ctx.hist)) if P["half_life"] == "auto" else P
        ctx.P = PP
        ctx.sig = {uu: {tid: EN.signal(x, ds.titles[tid].kind, ds.titles[tid].n_episodes, ctx.now, PP)
                        for tid, x in h.items()} for uu, h in ctx.hist.items()}
        ctx.pos = {uu: {t for t, v in ss.items() if v > 0} for uu, ss in ctx.sig.items()}
        ctx.cooc = EN.item_cooc(ctx.pos, PP["shrink"], PP["min_common"])

def hits(res, k=10):
    return [int(t in l[:k]) for u, t, l in res]

def ci(a, b, users):
    us = sorted(set(users)); by = {u: [i for i, x in enumerate(users) if x == u] for u in us}
    rng = random.Random(0); d = []
    for _ in range(2000):
        idx = [i for u in (rng.choice(us) for _ in us) for i in by[u]]
        d.append(sum(a[i] - b[i] for i in idx) / len(idx))
    d.sort(); return d[50], d[1950]

users = [u for u, *_ in cache]
print("T0 auto sur tout l'historique :", round(EN.auto_half_life(ds.users), 1), "jours")
rows = []
ref = None
for scheme, hl, decay, (shrink, mc) in itertools.product(("tfidf", "bm25"), (90.0, "auto"), ("exp", "power"), ((0.0, 1), (10.0, 2))):
    if EN.WEIGHTING["scheme"] != scheme:
        EN.WEIGHTING["scheme"] = scheme; FAST.set_features()
    prepare(dict(EN.DEFAULTS, half_life=hl, decay=decay, shrink=shrink, min_common=mc))
    r = fast_lists(W); h10, h20 = hits(r, 10), hits(r, 20)
    if ref is None: ref = h10
    lo, hi = ci(h10, ref, users)
    rows.append((scheme, hl, decay, shrink, mc, sum(h10) / len(h10), sum(h20) / len(h20), lo, hi))
    print(f"{scheme:5} T0={hl!s:5} {decay:5} shrink={shrink:4} min={mc}  hit@10 {rows[-1][5]:.3f} hit@20 {rows[-1][6]:.3f}  écart vs réf IC95 [{lo:+.3f};{hi:+.3f}]", flush=True)
pickle.dump(rows, open('grid2.pkl', 'wb'))
