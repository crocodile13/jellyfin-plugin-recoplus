"""Banc MovieLens : le moteur de référence (prototype Python) sur des foyers fictifs tirés de MovieLens.

MovieLens (GroupLens) donne des notes datées et l'identifiant TMDb de chaque film ; Wikidata (CC0, sans clé) donne,
par cet identifiant, réalisateurs, acteurs, scénaristes, studios, pays et langue. Le banc compose des foyers de 2 à 4
personnes de la même époque, une bibliothèque d'environ 1 500 films qui existaient à la date de coupure, et mesure,
pour chaque variante de réglages, où le moteur classe les films aimés juste après la coupure (gain 1/log2(1 + rang)
sur tous les candidats, comme le calibrage ; hit@10). Écart à la variante par défaut, IC95 bootstrap par foyer.

Approximations (le rapport les rappelle) : une note n'est pas un visionnage (≥ 3 : vu ; ≤ 2 : abandon) ; films
seulement ; acteurs = les 8 de la distribution Wikidata les plus fréquents dans le jeu (Wikidata n'a pas l'ordre du
générique) ; popularité et note de la communauté = notes MovieLens avant la coupure ; date d'ajout inconnue (nouveauté coupée).

    python bench.py --data ml-latest-small --households 60
    python bench.py --data ml-32m --households 400 --out rapport

Les données ne sont jamais versionnées (licence MovieLens : recherche, pas de redistribution) ; le cache Wikidata
(`wikidata.json`) est à côté des données.
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import random
import re
import statistics
import sys
import time
import urllib.parse
import urllib.request
import zipfile
from collections import Counter, defaultdict
from multiprocessing import Pool
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / "prototype"))
import engine as EN  # noqa: E402
from data import DAY, Dataset, Interaction, Title  # noqa: E402

URL = "https://files.grouplens.org/datasets/movielens/{}.zip"
UA = "RecoPlus-bench/1.0 (https://github.com/crocodile13/jellyfin-plugin-recoplus)"
LIBRARY = 1500
HOLDOUT, HOLDOUT_DAYS, MIN_BEFORE = 3, 180, 15
TREND_DAYS, TREND_TOP = 30, 40
PROPS = {"P57": "Director", "P161": "Actor", "P725": "Actor", "P58": "Writer", "P272": "Studio", "P495": "Country",
         "P364": "Language"}


# ---------------------------------------------------------------- données
def fetch(name: str, root: Path) -> Path:
    d = root / name
    if not (d / "ratings.csv").exists():
        root.mkdir(parents=True, exist_ok=True)
        z = root / f"{name}.zip"
        if not z.exists():
            print(f"téléchargement de {name}…", flush=True)
            urllib.request.urlretrieve(URL.format(name), z)
        with zipfile.ZipFile(z) as f:
            f.extractall(root)
    return d


def rows(path: Path):
    with open(path, newline="", encoding="utf-8") as f:
        r = csv.reader(f)
        next(r)
        yield from r


def load_movies(d: Path):
    movies, tmdb = {}, {}
    for mid, title, genres in rows(d / "movies.csv"):
        m = re.search(r"\((\d{4})\)\s*$", title)
        g = [] if genres == "(no genres listed)" else genres.split("|")
        movies[int(mid)] = (title, int(m.group(1)) if m else None, g)
    for mid, _imdb, t in rows(d / "links.csv"):
        if t.isdigit():
            tmdb[int(mid)] = t
    return movies, tmdb


def load_tags(d: Path) -> dict:
    """Mots-clés : génome (10 plus pertinents ≥ 0,8) s'il existe, sinon étiquettes posées par au moins 2 personnes."""
    out = defaultdict(list)
    if (d / "genome-scores.csv").exists():
        names = {int(i): t for i, t in rows(d / "genome-tags.csv")}
        best = defaultdict(list)
        for mid, tid, rel in rows(d / "genome-scores.csv"):
            r = float(rel)
            if r >= 0.8:
                best[int(mid)].append((r, names[int(tid)]))
        for mid, l in best.items():
            out[mid] = [t for _, t in sorted(l, reverse=True)[:10]]
    else:
        by = defaultdict(set)
        for uid, mid, tag, _ts in rows(d / "tags.csv"):
            by[(int(mid), tag.strip().lower())].add(uid)
        for (mid, tag), us in by.items():
            if len(us) >= 2:
                out[mid].append(tag)
    return out


def first_pass(d: Path):
    """Par compte : nombre de notes, première et dernière date ; par film : nombre, somme, première date."""
    users = {}
    mcount, msum, mfirst = Counter(), Counter(), {}
    for uid, mid, r, ts in rows(d / "ratings.csv"):
        u, m, t = int(uid), int(mid), int(ts)
        x = users.get(u)
        users[u] = [1, t, t] if x is None else [x[0] + 1, min(x[1], t), max(x[2], t)]
        mcount[m] += 1
        msum[m] += float(r)
        if m not in mfirst or t < mfirst[m]:
            mfirst[m] = t
    return users, mcount, msum, mfirst


def households(users: dict, n: int, seed: int) -> list:
    """Foyers de 2 à 4 comptes voisins dans le temps (même époque), comptes de 40 à 3 000 notes, chacun une fois."""
    ok = sorted((u for u, (c, a, b) in users.items() if 40 <= c <= 3000), key=lambda u: (users[u][1] + users[u][2], u))
    rng = random.Random(seed)
    out, i = [], 0
    while i < len(ok):
        k = rng.randint(2, 4)
        out.append(ok[i:i + k])
        i += k
    rng.shuffle(out)
    return [h for h in out if len(h) >= 2][:n]


def second_pass(d: Path, wanted: set) -> dict:
    hist = defaultdict(list)
    for uid, mid, r, ts in rows(d / "ratings.csv"):
        u = int(uid)
        if u in wanted:
            hist[u].append((int(ts), int(mid), float(r)))
    for l in hist.values():
        l.sort()
    return hist


def stats_at_cut(d: Path, libs: list) -> dict:
    """Nombre et moyenne des notes de chaque film de chaque bibliothèque, comptées **avant** la coupure de son foyer
    (popularité et note de la communauté sans rien du futur). libs : [(coupure, ensemble d'identifiants)]."""
    import bisect
    cuts = defaultdict(list)                                # film -> coupures où il est en bibliothèque
    for cut, lib in libs:
        for t in lib:
            cuts[int(t)].append(cut)
    for m in cuts:
        cuts[m] = sorted(set(cuts[m]))
    cnt = {m: [0] * (len(c) + 1) for m, c in cuts.items()}
    tot = {m: [0.0] * (len(c) + 1) for m, c in cuts.items()}
    for _uid, mid, r, ts in rows(d / "ratings.csv"):
        m = int(mid)
        c = cuts.get(m)
        if c is None:
            continue
        i = bisect.bisect_right(c, int(ts))                 # compte pour toute coupure strictement après la note
        cnt[m][i] += 1
        tot[m][i] += float(r)
    out = {}
    for m, c in cuts.items():
        n = s_ = 0.0
        for i, cut in enumerate(c):
            n += cnt[m][i]
            s_ += tot[m][i]
            out[(m, cut)] = (int(n), s_)
    return out


def wikidata(tmdb_ids: list, cache: Path) -> dict:
    """Personnes, studios, pays, langue par identifiant TMDb (identifiants Wikidata, pas de noms : seule l'identité compte)."""
    got = json.loads(cache.read_text()) if cache.exists() else {}
    todo = [t for t in tmdb_ids if t not in got]
    print(f"Wikidata : {len(tmdb_ids) - len(todo)} en cache, {len(todo)} à demander", flush=True)
    for i in range(0, len(todo), 150):
        part = todo[i:i + 150]
        q = ("SELECT ?tmdb ?p ?v WHERE { VALUES ?tmdb { " + " ".join(f'"{t}"' for t in part) + " } ?f wdt:P4947 ?tmdb . "
             "VALUES ?p { " + " ".join("wdt:" + p for p in PROPS) + " } ?f ?p ?v . }")
        for attempt in range(5):
            try:
                req = urllib.request.Request("https://query.wikidata.org/sparql",
                                             data=urllib.parse.urlencode({"query": q, "format": "json"}).encode(),
                                             headers={"User-Agent": UA, "Accept": "application/sparql-results+json"})
                with urllib.request.urlopen(req, timeout=120) as r:
                    res = json.load(r)["results"]["bindings"]
                break
            except Exception as e:  # noqa: BLE001
                print(f"  Wikidata, nouvel essai ({e})", flush=True)
                time.sleep(5 * (attempt + 1))
        else:
            raise RuntimeError("Wikidata injoignable")
        for t in part:
            got[t] = {}
        for x in res:
            role = PROPS[x["p"]["value"].rsplit("/", 1)[1]]
            got[x["tmdb"]["value"]].setdefault(role, []).append(x["v"]["value"].rsplit("/", 1)[1])
        if (i // 150) % 10 == 9 or i + 150 >= len(todo):
            cache.write_text(json.dumps(got))
            print(f"  {min(i + 150, len(todo))}/{len(todo)}", flush=True)
        time.sleep(0.5)
    return got


# ---------------------------------------------------------------- foyers
def build(house, hist, movies, tmdb, tags, wd, mcount, mfirst, actor_freq, seed, at_cut=None):
    """Coupure, historiques avant elle, films aimés juste après, bibliothèque ; None si rien à mesurer."""
    t80 = []
    for u in house:
        l = hist[u]
        t80.append(l[int(len(l) * 0.8)][0])
    cut = statistics.median(t80)
    hh, holdout = {}, {}
    for u in house:
        before = [x for x in hist[u] if x[0] < cut]
        after = [m for ts, m, r in hist[u] if cut <= ts < cut + HOLDOUT_DAYS * DAY and r >= 4.0]
        if len(before) < MIN_BEFORE:
            continue
        h = {}
        for ts, m, r in before:
            if r >= 3.0:
                h[str(m)] = Interaction(str(m), ts, ts, 1.0, True, plays=1)
            elif r <= 2.0:
                h[str(m)] = Interaction(str(m), ts, ts, 0.3, False)
        seen = set(h)
        keep = [str(m) for m in dict.fromkeys(after) if str(m) not in seen][:HOLDOUT]
        hh[u] = h
        if keep:
            holdout[u] = keep
    if not holdout:
        return None
    # bibliothèque : ce que le foyer a vu et va aimer, complété par des films qui existaient à la coupure
    lib = set(m for h in hh.values() for m in h) | set(m for l in holdout.values() for m in l)
    pool = [m for m in movies if mfirst.get(m, math.inf) < cut and str(m) not in lib]
    rng = random.Random(seed)
    w = [mcount[m] ** 0.75 for m in pool]
    need = max(0, LIBRARY - len(lib))
    picked = set()
    while len(picked) < min(need, len(pool)):
        for m in rng.choices(pool, weights=w, k=need - len(picked)):
            picked.add(m)
    lib |= {str(m) for m in picked}
    titles = {}
    for sid in lib:
        m = int(sid)
        name, year, genres = movies[m]
        info = wd.get(tmdb.get(m, ""), {})
        actors = sorted(set(info.get("Actor", [])), key=lambda a: (-actor_freq[a], a))[:EN.ACTORS]
        people = {"Actor": actors, "Director": sorted(set(info.get("Director", []))),
                  "Writer": sorted(set(info.get("Writer", [])))}
        n, tot = (at_cut or {}).get((m, cut), (0, 0.0))
        titles[sid] = Title(sid, "Movie", name, year, genres, tags.get(m, []), sorted(set(info.get("Studio", []))), people,
                            round(2 * tot / n, 2) if n else None, None, votes=n,
                            countries=sorted(set(info.get("Country", []))),
                            language=(sorted(info.get("Language", [""]))[0] or None))
    # tendance du moment (ce que Seerr donne de TMDb) : films les plus notés dans les 30 jours avant la coupure
    recent = {sid: (at_cut or {}).get((int(sid), cut), (0, 0))[0] - (at_cut or {}).get((int(sid), cut - TREND_DAYS * DAY), (0, 0))[0]
              for sid in lib}
    top = [t for t in sorted(recent, key=lambda t: (-recent[t], t)) if recent[t] > 0][:TREND_TOP]
    trending = {t: 1 / math.sqrt(1 + i) for i, t in enumerate(top)}
    return {"cut": cut, "titles": titles, "hist": hh, "holdout": holdout, "trending": trending}


# ---------------------------------------------------------------- variantes
def variants():
    """(nom, poids des caractéristiques, réglages du score, réglages des signaux, recommandeur de référence)."""
    v = [("défaut", {}, {}, {}, None)]
    labels = dict(g="genres", t="mots-clés", s="studios", a="acteurs", d="réalisateurs", w="scénaristes", y="décennie")
    for k, name in labels.items():
        v.append((f"{name} coupés", {k: 0.0}, {}, {}, None))
    for k in ("a", "d", "w", "s", "t"):
        v.append((f"{labels[k]} ×2", {k: EN.FEAT_W[k] * 2}, {}, {}, None))
        v.append((f"{labels[k]} ×0,5", {k: EN.FEAT_W[k] * 0.5}, {}, {}, None))
    v += [("pays et langue (0,3)", {"n": 0.3, "l": 0.3}, {}, {}, None),
          ("vus ensemble coupé", {}, {"collab": 0.0}, {}, None),
          ("popularité coupée", {}, {"pop": 0.0}, {}, None),
          ("popularité ×2", {}, {"pop": 1.0}, {}, None),
          ("tendance coupée", {}, {"trending_share": 0.0}, {}, None),
          ("note ×3", {}, {"quality": 0.3}, {}, None),
          ("garde-fou 30", {}, {"gate_top": 30}, {}, None),
          ("garde-fou 120", {}, {"gate_top": 120}, {}, None),
          ("titre le plus proche coupé", {}, {"maxsim": 0.0}, {}, None),
          ("goût d'ensemble coupé", {}, {"centroid": 0.0}, {}, None),
          ("abandons ignorés", {}, {"neg": 0.0}, {}, None),
          ("demi-vie 30 j", {}, {}, {"half_life": 30.0}, None),
          ("demi-vie 365 j", {}, {}, {"half_life": 365.0}, None),
          ("référence : Jellyfin 12", {}, {}, {}, "jellyfin"),
          ("référence : populaires", {}, {}, {}, "popular"),
          ("référence : hasard", {}, {}, {}, "random")]
    return v


def ranks(order: list, cands: list, targets: list) -> list:
    pos = {c: i + 1 for i, c in enumerate(order)}
    tail = len(order) + (len(cands) - len(order) + 1) / 2          # absent de la liste : rang moyen de la fin
    return [pos.get(t, tail) for t in targets]


def evaluate(job):
    house, seed = job
    feat0, w0, p0 = dict(EN.FEAT_W), dict(EN.ENGINE_W), dict(EN.DEFAULTS)
    out = {}
    try:
        ds = Dataset(house["titles"], meta={"trending": house["trending"]})
        T = house["titles"]
        vec_cache = {}
        for name, feat, w, p, ref in variants():
            EN.FEAT_W.clear(); EN.FEAT_W.update({**feat0, **feat})
            key = tuple(sorted(EN.FEAT_W.items()))
            if key not in vec_cache:
                vec_cache[key] = EN.build_vectors(T)
            vec = vec_cache[key]
            P = {**p0, **p}
            W = {**w0, **w}
            ctx = EN.Context(ds, house["cut"], house["hist"], P)
            gains, hits, n = [], [], 0
            for u, targets in house["holdout"].items():
                cands = sorted(t for t in T if t not in house["hist"][u])
                if ref == "jellyfin":
                    order = EN.rec_jellyfin(ctx, u, cands, len(cands), seed)
                elif ref == "popular":
                    order = sorted(cands, key=lambda c: (-(T[c].votes or 0), c))
                elif ref == "random":
                    order = EN.rec_random(ctx, u, cands, len(cands), seed)
                else:
                    sc = EN.score_engine(ctx, vec, u, cands, W)
                    order = sorted(cands, key=lambda c: (-sc[c], c))
                r = ranks(order, cands, targets)
                gains.append(sum(1 / math.log2(1 + x) for x in r) / len(r))
                hits.append(sum(x <= 10 for x in r) / len(r))
                n += len(r)
            out[name] = (sum(gains) / len(gains), sum(hits) / len(hits), n)
    finally:
        EN.FEAT_W.clear(); EN.FEAT_W.update(feat0)
    return out


def bootstrap(diffs: list, seed=7, b=2000):
    rng = random.Random(seed)
    n = len(diffs)
    ms = sorted(sum(diffs[rng.randrange(n)] for _ in range(n)) / n for _ in range(b))
    return ms[int(0.025 * b)], ms[int(0.975 * b)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="ml-latest-small")
    ap.add_argument("--root", default=str(HERE / "data"))
    ap.add_argument("--households", type=int, default=60)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out", default=str(HERE / "out"))
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 2)
    a = ap.parse_args()
    root, out = Path(a.root), Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    d = fetch(a.data, root)
    movies, tmdb = load_movies(d)
    tags = load_tags(d)
    users, mcount, _msum, mfirst = first_pass(d)
    hs = households(users, a.households * 2, a.seed)       # de la marge : certains foyers n'ont rien à mesurer
    hist = second_pass(d, {u for h in hs for u in h})
    print(f"données lues en {time.time() - t0:.0f} s : {len(users)} comptes, {len(movies)} films", flush=True)

    # premières bibliothèques sans personnes (pour savoir quels films demander à Wikidata), puis les vraies
    empty = Counter()
    drafts = []
    for i, h in enumerate(hs):
        b = build(h, hist, movies, tmdb, tags, {}, mcount, mfirst, empty, a.seed + i)
        if b:
            drafts.append((i, h, set(b["titles"]), b["cut"]))
        if len(drafts) >= a.households:
            break
    ids = sorted({tmdb[int(t)] for _, _, lib, _ in drafts for t in lib if int(t) in tmdb})
    wd = wikidata(ids, root / "wikidata.json")
    actor_freq = Counter(x for info in wd.values() for x in set(info.get("Actor", [])))
    at_cut = stats_at_cut(d, [(c, lib) for _, _, lib, cut in drafts for c in (cut, cut - TREND_DAYS * DAY)])
    houses = [build(h, hist, movies, tmdb, tags, wd, mcount, mfirst, actor_freq, a.seed + i, at_cut) for i, h, _, _ in drafts]
    covered = sum(1 for t in ids if wd.get(t, {}).get("Director"))
    print(f"{len(houses)} foyers ; réalisateur connu pour {covered}/{len(ids)} films", flush=True)

    with Pool(a.jobs) as pool:
        res = pool.map(evaluate, [(h, a.seed) for h in houses], chunksize=1)
    names = [v[0] for v in variants()]
    base = [r["défaut"][0] for r in res]
    lines = [f"# Banc MovieLens ({a.data})", "",
             f"{len(houses)} foyers de 2 à 4 comptes, bibliothèques d'environ {LIBRARY} films, "
             f"{sum(r['défaut'][2] for r in res)} films aimés à retrouver (les {HOLDOUT} premiers après la coupure, "
             f"dans les {HOLDOUT_DAYS} jours). Réalisateur connu pour {covered}/{len(ids)} films (Wikidata). "
             f"Durée : {time.time() - t0:.0f} s.", "",
             "Gain : moyenne de 1/log2(1 + rang) sur tous les candidats (comme le calibrage), par compte puis par foyer. "
             "Écart à « défaut » en %, IC95 bootstrap par foyer ; ✔ = au-dessus de zéro, ✖ = en dessous.", "",
             "| Variante | Gain | Écart | IC95 | hit@10 | |", "|---|---|---|---|---|---|"]
    m0 = sum(base) / len(base)
    report = {}
    for name in names:
        g = [r[name][0] for r in res]
        hit = sum(r[name][1] for r in res) / len(res)
        m = sum(g) / len(g)
        lo, hi = bootstrap([x - y for x, y in zip(g, base)]) if name != "défaut" else (0.0, 0.0)
        mark = "✔" if lo > 0 else "✖" if hi < 0 else ""
        report[name] = {"gain": m, "hit10": hit, "lo": lo, "hi": hi}
        rel = lambda x: f"{100 * x / m0:+.1f} %"  # noqa: E731
        lines.append(f"| {name} | {m:.4f} | {rel(m - m0) if name != 'défaut' else '—'} | "
                     f"{rel(lo) + ' … ' + rel(hi) if name != 'défaut' else '—'} | {100 * hit:.1f} % | {mark} |")
    lines += ["", "Approximations : note ≥ 3 = vu, ≤ 2 = abandon ; films seulement ; acteurs = les 8 de la distribution "
              "Wikidata les plus fréquents ; popularité et note de la communauté = notes MovieLens d'avant la coupure ; "
              "date d'ajout inconnue (nouveauté coupée)."]
    md = "\n".join(lines) + "\n"
    (out / "report.md").write_text(md, encoding="utf-8")
    (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(md)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
            f.write(md)


if __name__ == "__main__":
    main()
