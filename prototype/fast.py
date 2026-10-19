"""Version matricielle (numpy) du score du moteur, strictement équivalente à engine.score_engine,
pour pouvoir explorer beaucoup de réglages. Vérifiée contre la version de référence (verify())."""
from __future__ import annotations

import math

import numpy as np

import engine as EN


class Fast:
    def __init__(self, ds):
        self.ds = ds
        self.ids = sorted(ds.titles)
        self.ix = {t: i for i, t in enumerate(self.ids)}
        self.added = np.array([ds.titles[t].added or np.nan for t in self.ids])
        self.comm = np.array([ds.titles[t].community if ds.titles[t].community is not None else 6.5
                              for t in self.ids])
        self.set_features()

    def set_features(self):
        vec = EN.build_vectors(self.ds.titles)
        feats = sorted({f for v in vec.values() for f in v})
        fi = {f: j for j, f in enumerate(feats)}
        V = np.zeros((len(self.ids), len(feats)))
        for t, v in vec.items():
            for f, x in v.items():
                V[self.ix[t], fi[f]] = x
        self.vec = vec
        self.S = V @ V.T

    def scores(self, ctx, user, W):
        n = len(self.ids)
        s = np.zeros(n)
        for t, v in ctx.sig[user].items():
            s[self.ix[t]] = v
        pos = np.clip(s, 0, None)
        neg = np.clip(-s, 0, None)
        # profil = Σ pos v − neg_w Σ neg v (puis normalisé)
        c = pos - W["neg"] * neg
        pn = math.sqrt(max(c @ self.S @ c, 0.0)) or 1.0
        cen = (c @ self.S) / pn
        pmax = pos.max() if pos.any() else 1.0
        pi = np.nonzero(pos)[0]
        ms = (pos[pi, None] * self.S[pi]).max(axis=0) / pmax if len(pi) else np.zeros(n)
        ni = np.nonzero(neg)[0]
        negp = (neg[ni, None] * self.S[ni]).max(axis=0) if len(ni) else np.zeros(n)
        tot = pos.sum() or 1.0
        col = np.zeros(n)
        for t in pi:
            row = ctx.cooc.get(self.ids[t])
            if row:
                for j, v in row.items():
                    col[self.ix[j]] += pos[t] * v
        col /= tot
        from collections import Counter
        op = Counter(t for u, ss in ctx.pos.items() if u != user for t in ss)
        mp = max(op.values(), default=1)
        pop = np.zeros(n)
        for t, v in op.items():
            pop[self.ix[t]] = math.log1p(v) / math.log1p(mp)
        q = np.clip((self.comm - 6.5) / 2.0, -1, 1)
        age = np.clip((ctx.now - self.added) / EN.DAY, 0, None)
        fr = np.where(np.isnan(self.added), 0.0, np.exp(-np.nan_to_num(age, nan=0.0) / W["fresh_days"]))
        taste = W["centroid"] * cen + W["maxsim"] * np.where(len(pi) > 0, ms, 0) + W["collab"] * col - W["neg"] * negp
        if W.get("gate") == "rank":
            return self._gate_rank(taste, fr, pop, q, W)
        if W.get("gate"):
            tp = np.clip(taste, 0, None)
            return taste + W["fresh"] * fr * tp + W["pop"] * pop * tp + W["quality"] * q
        return taste + W["pop"] * pop + W["quality"] * q + W["fresh"] * fr


    # les candidats ne sont pas connus ici : le rang est calculé sur la liste fournie par set_cands()
    cands_ix = None

    def _gate_rank(self, taste, fr, pop, q, W):
        ci = self.cands_ix if self.cands_ix is not None else np.arange(len(taste))
        t = taste[ci]
        order = np.argsort(-t, kind="stable")
        rank = np.empty(len(ci)); rank[order] = np.arange(len(ci))
        g = np.clip(1.0 - rank / max(1, int(W.get("gate_top", 60))), 0, None)
        out = np.full(len(taste), -1e9)
        out[ci] = t + np.clip(t, 0, None) * g * (W["fresh"] * fr[ci] + W["pop"] * pop[ci] + W["quality"] * q[ci])
        return out


def verify(ds, ctx, user, cands, W=EN.ENGINE_W):
    f = Fast(ds)
    f.cands_ix = np.array([f.ix[c] for c in cands])
    ref = EN.score_engine(ctx, f.vec, user, cands, W)
    fs = f.scores(ctx, user, W)
    err = max(abs(ref[c] - fs[f.ix[c]]) for c in cands)
    return err
