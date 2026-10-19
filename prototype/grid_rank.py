"""Grille du moteur à bonus réservés au haut du classement, évaluée sur la liste affichée (saga + MMR)."""
import sys, itertools, pickle
import numpy as np
exec(open('listeval.py').read())
P = dict(EN.DEFAULTS, half_life=90)
for u, target, t0, ctx, cands in cache:                      # signaux calculés une seule fois (P fixe)
    ctx.P = P
    ctx.sig = {uu: {tid: EN.signal(x, ds.titles[tid].kind, ds.titles[tid].n_episodes, ctx.now, P)
                    for tid, x in h.items()} for uu, h in ctx.hist.items()}
    ctx.pos = {uu: {t for t, v in ss.items() if v > 0} for uu, ss in ctx.sig.items()}
    ctx.cooc = EN.item_cooc(ctx.pos)
CI = [np.array([FAST.ix[c] for c in cands]) for *_, cands in cache]

def fast_lists(W, mmr=0.3, k=20):
    out = []
    for (u, target, t0, ctx, cands), ci in zip(cache, CI):
        FAST.cands_ix = ci
        s = FAST.scores(ctx, u, W)
        order = ci[np.argsort(-s[ci], kind="stable")][: k * 8]
        pool = list(order); chosen = []
        while pool and len(chosen) < k:                       # MMR sur la matrice de similarité
            if chosen:
                pen = FAST.S[np.ix_(pool, chosen)].max(axis=1)
            else:
                pen = np.zeros(len(pool))
            j = int(np.argmax(s[pool] - mmr * pen)); chosen.append(pool.pop(j))
        ranked = [FAST.ids[i] for i in chosen] + [FAST.ids[i] for i in pool]
        sc = {c: (len(ranked) - i) for i, c in enumerate(ranked)}   # ordre MMR figé, puis règle de saga
        out.append((u, target, EN.rec_engine(ctx, FAST.vec, u, ranked, k, W, mmr=0.0, saga=True, sc=sc)))
    return out

if __name__ == "__main__":
    base = dict(EN.ENGINE_W, centroid=1, maxsim=2, collab=0.3, fresh_days=45, gate="rank")
    even = [i for i in range(len(cache)) if i % 2 == 0]; odd = [i for i in range(len(cache)) if i % 2]
    sub = lambda r, idx: score([r[i] for i in idx], True)
    grid = []
    for gt, fr, q, pop in itertools.product((15, 30, 60), (1, 2, 4, 8), (0.1, 0.5), (0.5, 1.0)):
        W = dict(base, gate_top=gt, fresh=fr, quality=q, pop=pop)
        r = fast_lists(W)
        grid.append(((gt, fr, q, pop), sub(r, even), sub(r, odd), sub(r, range(len(r)))))
        print(grid[-1], flush=True)
    pickle.dump(grid, open('grid_rank.pkl', 'wb'))
    for name, a, b in (("pairs→impairs", 1, 2), ("impairs→pairs", 2, 1)):
        g = max(grid, key=lambda g: g[a][0] + g[a][1])
        print(name, g[0], "appr", tuple(round(x, 3) for x in g[a]), "test", tuple(round(x, 3) for x in g[b]))
    for g in sorted(grid, key=lambda g: -(g[3][0] + g[3][1]))[:8]:
        print("  ", g[0], tuple(round(x, 3) for x in g[3]))
