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


@dataclass
class Dataset:
    titles: dict
    users: dict = field(default_factory=dict)      # user -> {title_id: Interaction}
    meta: dict = field(default_factory=dict)


def load(path: str) -> Dataset:
    d = json.load(gzip.open(path))
    titles = {}
    for i in d["items"]:
        titles[i["id"]] = Title(i["id"], i["type"], i["name"] or "", i.get("year"), i.get("genres") or [],
                                i.get("tags") or [], i.get("studios") or [], i.get("people") or {},
                                i.get("community"), ts(i.get("added")))
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
        for x in a["items"]:
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
                                          fav=x["fav"])
            elif t.kind == "Series" and x["fav"]:
                inter.setdefault(t.id, Interaction(t.id, 0, 0, 0, False)).fav = True
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
                                     episodes=len(played), plays=len(played), fav=bool(prev and prev.fav))
        users[a["user"]] = {k: v for k, v in inter.items() if v.last or v.fav}
    # Date d'ajout effective : un titre remplacé ou rescanné garde sa première apparition connue
    for h in users.values():
        for tid, x in h.items():
            t = titles[tid]
            if x.start and (t.added is None or x.start < t.added):
                t.added = x.start
    colls = {c["id"]: [k for k in c["items"] if k in titles] for c in d.get("collections", [])}
    return Dataset(titles, users, {"exported": d["export"]["at"], "now": ts(d["export"]["at"]), "colls": colls})
