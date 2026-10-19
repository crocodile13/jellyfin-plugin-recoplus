"""Chargement de l'export et construction des interactions utilisateur × titre.

Un « titre » est un film ou une série. L'historique d'épisodes est agrégé par série.
"""
from __future__ import annotations

import gzip
import json
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone

DAY = 86400.0


def ts(s: str | None) -> float | None:
    if not s:
        return None
    s = s.replace("Z", "+00:00")
    if " " in s and "T" not in s:
        s = s.replace(" ", "T")
    try:
        d = datetime.fromisoformat(s)
    except ValueError:
        d = datetime.fromisoformat(s[:19])
    if d.tzinfo is None:
        d = d.replace(tzinfo=timezone.utc)
    return d.timestamp()


@dataclass
class Title:
    id: str
    kind: str                     # Movie | Series
    name: str
    year: int | None
    genres: list
    tags: list
    studios: list
    people: dict
    community: float | None
    added: float | None
    n_episodes: int = 0
    colls: list = field(default_factory=list)   # collections Jellyfin contenant ce titre
    released: float | None = None               # date de sortie (pas d'ajout)
    critic: float | None = None                 # note des critiques, 0-100
    countries: list = field(default_factory=list)
    language: str | None = None                 # langue originale (ISO 639-1)
    votes: int | None = None                    # votes derrière la note de la communauté (TMDb)


@dataclass
class Interaction:
    """Ce qu'un utilisateur a fait d'un titre."""
    title: str
    start: float                  # premier contact (date)
    last: float                   # dernière activité
    completion: float             # 0..1 (film : vu=1 / position ; série : part des épisodes vus)
    played: bool                  # film vu, ou au moins un épisode vu
    episodes: int = 0             # épisodes vus (séries)
    plays: int = 0
    fav: bool = False
    requested: float = 0.0        # demande Seerr (date), 0 sinon
    watchlisted: float = 0.0      # liste de suivi Seerr (date), 0 sinon
    rewatch: float = 0.0          # revisionnage 0..1
    fav_seasons: int = 0          # saisons en favori (séries)
    fav_episodes: int = 0         # épisodes en favori (séries)
    rating: float | None = None   # note explicite -1..+1
    hidden: bool = False          # masqué (« pas intéressé »)


@dataclass
class Dataset:
    titles: dict
    users: dict = field(default_factory=dict)      # user -> {title_id: Interaction}
    meta: dict = field(default_factory=dict)


def movie_rewatch(plays) -> float:
    """Comme le plugin (HistoryMath) : 2 lectures = 0,5 ; 3 et plus = 1."""
    plays = plays or 0
    return min(1.0, (plays - 1) / 2.0) if plays >= 2 else 0.0


def likes_rating(x):
    """Pouce Jellyfin → -1/+1 (la note 0-10 n'est pas exportée)."""
    lk = x.get("likes")
    return None if lk is None else (1.0 if lk else -1.0)


def load(path: str) -> Dataset:
    d = json.load(gzip.open(path))
    titles = {}
    for i in d["items"]:
        titles[i["id"]] = Title(i["id"], i["type"], i["name"] or "", i.get("year"), i.get("genres") or [],
                                i.get("tags") or [], i.get("studios") or [], i.get("people") or {},
                                i.get("community"), ts(i.get("added")),
                                released=ts(i.get("premiere")), critic=i.get("critic"),
                                countries=i.get("countries") or [], language=i.get("language"))
    for c in d.get("collections", []):
        for k in c["items"]:
            if k in titles:
                titles[k].colls.append(c["id"])
    ep_series = {}
    for e in d["episodes"]:
        ep_series[e["id"]] = e["series"]
        if e["series"] in titles:
            titles[e["series"]].n_episodes += 1
    # premier contact par (utilisateur, titre) depuis les sessions Playback Reporting
    first_seen = {}
    for s in d.get("sessions", []):
        tid = ep_series.get(s["id"], s["id"])
        t = ts(s["at"])
        if t and tid in titles:
            k = (s["user"], tid)
            first_seen[k] = min(first_seen.get(k, t), t)
    users = {}
    for a in d["accounts"]:
        per_series = defaultdict(list)
        inter = {}
        fav_seasons = defaultdict(int)
        for x in a["items"]:
            if x["type"] == "Season":
                if x.get("fav") and x.get("series") in titles:
                    fav_seasons[x["series"]] += 1
                continue
            if x["type"] == "Episode":
                if x.get("series") in titles:
                    per_series[x["series"]].append(x)
                continue
            if x["id"] not in titles:
                continue
            t = titles[x["id"]]
            last = ts(x.get("last"))
            if t.kind == "Movie":
                comp = 1.0 if x["played"] else min(1.0, (x.get("pct") or 0) / 100)
                start = first_seen.get((a["user"], t.id), last)
                inter[t.id] = Interaction(t.id, start or 0, last or 0, comp, x["played"], plays=x["plays"],
                                          fav=x["fav"], rewatch=movie_rewatch(x["plays"]), rating=likes_rating(x))
            elif t.kind == "Series" and (x["fav"] or likes_rating(x) is not None):
                y = inter.setdefault(t.id, Interaction(t.id, 0, 0, 0, False))
                y.fav = y.fav or x["fav"]
                y.rating = likes_rating(x)
        for sid, eps in per_series.items():
            played = [e for e in eps if e["played"]]
            lasts = [ts(e["last"]) for e in eps if e.get("last")]
            lasts = [v for v in lasts if v]
            if not lasts:
                continue
            n = max(titles[sid].n_episodes, 1)
            prev = inter.get(sid)
            start = first_seen.get((a["user"], sid), min(lasts))
            inter[sid] = Interaction(sid, start, max(lasts), min(1.0, len(played) / n), bool(played),
                                     episodes=len(played), plays=len(played), fav=bool(prev and prev.fav),
                                     rating=prev.rating if prev else None,
                                     fav_episodes=sum(1 for e in eps if e.get("fav")),
                                     rewatch=min(1.0, sum(1 for e in eps if (e.get("plays") or 0) >= 2) / 3.0))
        for sid, n in fav_seasons.items():
            inter.setdefault(sid, Interaction(sid, 0, 0, 0, False)).fav_seasons = n
        users[a["user"]] = {k: v for k, v in inter.items()
                            if v.last or v.fav or v.rating is not None or v.fav_seasons or v.fav_episodes}
    # Date d'ajout effective : un titre remplacé ou rescanné garde sa première apparition connue
    for h in users.values():
        for tid, x in h.items():
            t = titles[tid]
            if x.start and (t.added is None or x.start < t.added):
                t.added = x.start
    colls = {c["id"]: [k for k in c["items"] if k in titles] for c in d.get("collections", [])}
    return Dataset(titles, users, {"exported": d["export"]["at"], "now": ts(d["export"]["at"]), "colls": colls})
