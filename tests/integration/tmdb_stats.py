"""Relevé de TMDb (via le Seerr du test d'intégration) pour choisir les seuils par défaut de Reco+ : votes, note et
popularité des titres de chaque liste (sorties récentes par âge, populaires, tendances, à venir), sans aucun filtre.
Les titres bruts vont dans l'artifact (tmdb-stats.json) ; l'analyse se fait à part. Lancé sur les branches sim-* quand
SIM_TMDB_STATS est posé. Aucun secret : la clé de Seerr reste dans eco.py."""
from __future__ import annotations

import datetime as dt
import json
import time

import eco

PAGES = 25   # 20 titres par page


def _page(path: str) -> list[dict]:
    try:
        return (eco.seerr("GET", path) or {}).get("results") or []
    except Exception as e:   # page refusée ou Seerr lent : on garde ce qui a été lu
        print(f"  TMDb : {path.split('?')[0]} : {e}", flush=True)
        return []


def collect(out_path) -> dict:
    today = dt.date.today()
    d = lambda days: (today - dt.timedelta(days=days)).isoformat()   # noqa: E731
    lists = {
        "movies_recent_240d": f"/api/v1/discover/movies?primaryReleaseDateGte={d(240)}&primaryReleaseDateLte={today}&sortBy=popularity.desc",
        "tv_recent_120d": f"/api/v1/discover/tv?firstAirDateGte={d(120)}&firstAirDateLte={today}&sortBy=popularity.desc",
        "movies_popular": "/api/v1/discover/movies?sortBy=popularity.desc",
        "tv_popular": "/api/v1/discover/tv?sortBy=popularity.desc",
        "trending_week": "/api/v1/discover/trending?timeWindow=week",
        "movies_upcoming": "/api/v1/discover/movies/upcoming",
        "tv_upcoming": "/api/v1/discover/tv/upcoming",
    }
    titles: dict[str, dict] = {}
    for name, path in lists.items():
        n = 0
        for p in range(1, PAGES + 1):
            items = _page(path + ("&" if "?" in path else "?") + f"page={p}")
            if not items:
                break
            for rank, r in enumerate(items):
                mt = r.get("mediaType") or ("movie" if "movies" in path else "tv")
                if mt not in ("movie", "tv") or not r.get("id"):
                    continue
                key = f"{mt}:{r['id']}"
                t = titles.setdefault(key, {
                    "key": key, "name": r.get("title") or r.get("name"), "date": r.get("releaseDate") or r.get("firstAirDate"),
                    "votes": r.get("voteCount") or 0, "rating": r.get("voteAverage") or 0, "popularity": r.get("popularity") or 0,
                    "language": r.get("originalLanguage"), "genres": r.get("genreIds") or [], "adult": bool(r.get("adult")), "lists": {}})
                t["lists"][name] = (p - 1) * 20 + rank + 1
                n += 1
            time.sleep(0.15)
        print(f"  TMDb : {name} : {n} titres", flush=True)
    # dates de sortie par pays des films récents les plus populaires (fiches) : fenêtre et écart avec la date principale
    recent = sorted((t for t in titles.values() if "movies_recent_240d" in t["lists"]), key=lambda t: t["lists"]["movies_recent_240d"])[:150]
    for t in recent:
        try:
            det = eco.seerr("GET", f"/api/v1/movie/{t['key'].split(':')[1]}") or {}
        except Exception:
            continue
        local = {}
        for c in ((det.get("releases") or {}).get("results") or []):
            ds = sorted(r.get("release_date", "")[:10] for r in c.get("release_dates") or [] if r.get("type") in (2, 3, 4, 6) and r.get("release_date"))
            if ds:
                local[c.get("iso_3166_1")] = ds[0]
        t["local"] = {k: v for k, v in local.items() if k in ("FR", "US", "GB", "DE", "ES", "BE", "CA", "IT")}
        t["runtime"] = det.get("runtime")
        time.sleep(0.1)
    data = {"date": today.isoformat(), "titles": list(titles.values())}
    out_path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return {"titres": len(titles), "fiches": len(recent)}
