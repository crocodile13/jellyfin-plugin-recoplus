"""Banc hors ligne : le moteur de référence (prototype Python) sur les fiches figées du banc (nfo/) et ses comptes.

Approximation du vrai calcul (pas de Seerr, pas de rangées) pour mesurer vite une idée sur le contenu ou le score :
pour chaque compte, les titres mis de côté (holdout) sont-ils retrouvés dans son classement ? Rien n'est lu du réseau.

    python offline.py            mesure avec les réglages par défaut
    import offline; offline.run(extra=…)   extra(title_id) → {caractéristique: poids} ajouté au contenu de chaque titre
    offline.run(w={"origin": 0}, home={"anime": ["l:ja", "c:japan"]})   réglages du score, origines du pays des comptes
"""
from __future__ import annotations

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
os.environ["SIM_BENCH"] = "1"
sys.path[:0] = [str(ROOT / "prototype"), str(HERE)]
import bench  # noqa: E402
import catalog  # noqa: E402
import engine as EN  # noqa: E402
from data import DAY, Dataset, Interaction, Title  # noqa: E402

NOW = 1_790_000_000.0


def load():
    """Titres des fiches figées : identifiant = nom du fichier ; aussi l'identifiant TMDb de chacun."""
    titles, byname, tmdb = {}, {}, {}
    for f in sorted(glob.glob(str(HERE / "nfo" / "*.nfo"))):
        r = ET.parse(f).getroot()
        g = lambda k: (r.findtext(k) or "").strip()  # noqa: E731
        kind = "Series" if r.tag == "tvshow" else "Movie"
        tid = os.path.basename(f)[:-4]
        people = {"Actor": [a.findtext("name") for a in r.findall("actor")][:8], "Director": [x.text for x in r.findall("director")],
                  "Writer": [x.text for x in r.findall("credits")]}
        y = int(g("year")) if g("year").isdigit() else None
        titles[tid] = Title(tid, kind, g("title"), y, [x.text for x in r.findall("genre")], [x.text for x in r.findall("tag")],
                            [x.text for x in r.findall("studio")], people, float(g("rating") or 0) or None, NOW - 400 * DAY,
                            n_episodes=10 if kind == "Series" else 0, countries=[x.text for x in r.findall("country") if x.text])
        u = r.find("uniqueid[@type='tmdb']")
        if u is not None and (u.text or "").isdigit():
            tmdb[tid] = ("tv" if kind == "Series" else "movie", int(u.text))
        byname.setdefault(g("title"), tid)
        byname.setdefault(re.sub(r" \(\d{4}\)$", "", tid), tid)
    return titles, byname, tmdb


T, BY, TMDB = load()


def find(t):
    return BY.get(t)


def history(p):
    """Historique d'un compte du banc, comme le test d'intégration le joue sur le vrai serveur."""
    h = {}

    def it(t, **kw):
        tid = find(t)
        if tid is None:
            return None
        x = h.get(tid) or Interaction(tid, 0, 0, 0, False)
        h[tid] = x
        for k, v in kw.items():
            setattr(x, k, v)
        return x

    for t, d in p.get("watched", []):
        ts = NOW - d * DAY
        it(t, start=ts - 3600, last=ts, completion=1.0, played=True, plays=1, episodes=10 if find(t) and T[find(t)].kind == "Series" else 0)
    for t, n, d in p.get("episodes", []):
        ts = NOW - d * DAY
        it(t, start=ts - n * DAY, last=ts, completion=n / 10, played=True, episodes=n, hours=n * .75)
    for t, part, d in p.get("abandoned", []):
        ts = NOW - d * DAY
        it(t, start=ts, last=ts, completion=part, played=False)
    for t in p.get("favorites", []):
        it(t, fav=True)
    for t in p.get("dislikes", []):
        it(t, rating=-1.0)
    for t, s in p.get("reviews", []):
        it(t, rating=(s - 3) / 2)
    for t in p.get("hidden", []):
        it(t, hidden=True)
    for t in p.get("bulk", []):
        x = h.get(find(t))
        if x:
            x.backfill = True
    return h


def run(extra=None, w=None, home=None):
    """hit@10, hit@20, MRR des titres mis de côté, et part du top 10 dans les univers du compte ; par compte aussi."""
    personas = dict(catalog.PERSONAS)
    universe = bench.universe_of()
    base = EN.tokens
    if extra is not None:
        EN.tokens = lambda t: {**base(t), **extra(t.id)}
    try:
        ds = Dataset(T, meta={"home_origins": home or {}})
        hist = {u: history(p) for u, p in personas.items()}
        ctx = EN.Context(ds, NOW, hist)
        vec = EN.build_vectors(T)
    finally:
        EN.tokens = base
    w = dict(EN.ENGINE_W, **(w or {}))
    h10 = h20 = mrr = n = 0
    taste, per = [], {}
    for u, p in personas.items():
        cands = [t for t in T if t not in hist[u]]
        lst = EN.rec_engine(ctx, vec, u, cands, 200, w, mmr=EN.MMR, saga=False, sc=EN.score_engine(ctx, vec, u, cands, w), div="personalized")
        found = []
        for t, _y, _k in p.get("holdout", []):
            tid = find(t)
            n += 1
            if tid in lst:
                r = lst.index(tid) + 1
                h10 += r <= 10
                h20 += r <= 20
                mrr += 1 / r
                found.append(r)
        uni = set(p.get("univers", {}))
        if uni:
            taste.append(sum(1 for x in lst[:10] if universe.get(T[x].name, set()) & uni) / 10)
        per[u] = found
    return {"hit10": h10 / n, "hit20": h20 / n, "mrr": mrr / n, "taste": sum(taste) / max(1, len(taste)), "cases": n}, per


if __name__ == "__main__":
    m, _ = run()
    print({k: round(v, 3) if isinstance(v, float) else v for k, v in m.items()})
