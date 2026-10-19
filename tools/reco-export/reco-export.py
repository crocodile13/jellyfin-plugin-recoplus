#!/usr/bin/env python3
"""reco-export — export en lecture seule des données utiles à la mise au point
d'un moteur de recommandation Jellyfin.

Lit :  ~/.config/jellyfin-api.env  (JF_URL, JF_KEY)   — la clé n'est jamais affichée
       la base playback_reporting.db (facultative, --playback-db), ouverte en lecture seule
Écrit : ~/reco-export/reco-export-AAAAMMJJ-HHMM.json.gz       (à transmettre)
        ~/reco-export/correspondance-comptes.json (600)       (reste sur la VM)

Contenu exporté :
  * bibliothèque : films, séries (genres, mots-clés, acteurs/réalisateurs/scénaristes
    principaux, studios, année, notes, durée, date d'ajout, identifiants TMDB/IMDB),
    épisodes (série, saison, numéro, durée) ;
  * pour chaque compte, sous pseudonyme (u01, u02…) : éléments vus, en cours ou
    favoris avec leurs données de lecture (vu, nombre de lectures, position,
    pourcentage, date de dernière lecture, favori) ;
  * facultatif : historique des sessions (date, élément, durée), sans nom
    d'appareil ni de client.
Jamais exportés : noms et identifiants des comptes, adresses, appareils, clé API.
Aucune écriture dans Jellyfin (requêtes GET uniquement)."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

VERSION = "1.0.3"
PAGE = 400
ITEM_FIELDS = "Genres,Tags,People,Studios,ProviderIds,DateCreated,OfficialRating,RunTimeTicks,ProductionLocations"
PEOPLE_KEEP = {"Actor": 15, "Director": 5, "Writer": 5, "Creator": 5, "Producer": 3, "Composer": 2}


class ApiError(RuntimeError):
    pass


class Api:
    def __init__(self, url: str, key: str, timeout: int = 120):
        self.url, self.key, self.timeout = url.rstrip("/"), key, timeout
        self.calls = 0

    def get(self, path: str, **params):
        q = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None}, doseq=True)
        req = urllib.request.Request(f"{self.url}{path}{'?' + q if q else ''}",
                                     headers={"Authorization": f'MediaBrowser Token="{self.key}"',
                                              "Accept": "application/json"})
        last = None
        for attempt in range(4):
            try:
                with urllib.request.urlopen(req, timeout=self.timeout) as r:
                    self.calls += 1
                    return json.load(r)
            except urllib.error.HTTPError as e:          # le message ne contient pas la clé (passée en en-tête)
                if e.code in (400, 401, 403, 404):
                    raise ApiError(f"HTTP {e.code} sur {path}") from None
                last = f"HTTP {e.code}"
            except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
                last = type(e).__name__
            time.sleep(1.5 * (attempt + 1))
        raise ApiError(f"{path} : échec après 4 essais ({last})")

    def paged(self, path: str, **params):
        start = 0
        while True:
            r = self.get(path, StartIndex=start, Limit=PAGE, EnableTotalRecordCount="true",
                         CollapseBoxSetItems="false", **params)
            items = r.get("Items", []) if isinstance(r, dict) else []
            yield from items
            start += len(items)
            total = r.get("TotalRecordCount", 0) if isinstance(r, dict) else 0
            if not items or start >= total:
                break


def read_env(p: Path) -> dict:
    if not p.is_file():
        raise SystemExit(f"{p} absent")
    if p.stat().st_mode & 0o077:
        raise SystemExit(f"{p} lisible par d'autres : chmod 600 {p}")
    env = {}
    for line in p.read_text().splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            k, v = line.split("=", 1)
            env[k.strip()] = v.strip().strip('"').strip("'")
    if not env.get("JF_URL") or not env.get("JF_KEY"):
        raise SystemExit(f"{p} : JF_URL et JF_KEY requis")
    return env


def slim_item(it: dict) -> dict:
    people = {}
    for p in it.get("People") or []:
        t = p.get("Type") or "Other"
        if t in PEOPLE_KEEP and len(people.setdefault(t, [])) < PEOPLE_KEEP[t]:
            people[t].append(p.get("Name"))
    pid = it.get("ProviderIds") or {}
    return {
        "id": it["Id"], "type": it.get("Type"), "name": it.get("Name"),
        "year": it.get("ProductionYear"), "genres": it.get("Genres") or [],
        "tags": it.get("Tags") or [], "studios": [s.get("Name") for s in it.get("Studios") or []],
        "people": people, "community": it.get("CommunityRating"), "critic": it.get("CriticRating"),
        "official": it.get("OfficialRating"), "runtime": it.get("RunTimeTicks"),
        "added": it.get("DateCreated"), "premiere": it.get("PremiereDate"),
        "tmdb": pid.get("Tmdb"), "imdb": pid.get("Imdb"), "parent": it.get("ParentId"),
        "countries": it.get("ProductionLocations") or [],
    }


def slim_episode(it: dict) -> dict:
    return {"id": it["Id"], "series": it.get("SeriesId"), "season": it.get("ParentIndexNumber"),
            "episode": it.get("IndexNumber"), "runtime": it.get("RunTimeTicks"),
            "premiere": it.get("PremiereDate"), "added": it.get("DateCreated")}


def slim_userdata(it: dict) -> dict:
    ud = it.get("UserData") or {}
    return {"id": it["Id"], "type": it.get("Type"), "series": it.get("SeriesId"),
            "played": bool(ud.get("Played")), "plays": ud.get("PlayCount") or 0,
            "pos": ud.get("PlaybackPositionTicks") or 0, "pct": ud.get("PlayedPercentage"),
            "last": ud.get("LastPlayedDate"), "fav": bool(ud.get("IsFavorite")),
            "likes": ud.get("Likes"), "runtime": it.get("RunTimeTicks")}


def export(api: Api, playback_db: Path | None, log=print) -> tuple[dict, dict]:
    info = api.get("/System/Info")
    users = api.get("/Users")
    if not isinstance(users, list) or not users:
        raise ApiError("liste des utilisateurs vide ou invalide (clé sans droits administrateur ?)")
    users.sort(key=lambda u: u.get("LastActivityDate") or "", reverse=True)
    pseudo = {u["Id"]: f"u{i + 1:02d}" for i, u in enumerate(users)}
    mapping = {pseudo[u["Id"]]: {"name": u.get("Name"), "id": u["Id"]} for u in users}

    log(f"Jellyfin {info.get('Version')} — {len(users)} comptes ; lecture de la bibliothèque…")
    items = [slim_item(i) for i in api.paged("/Items", Recursive="true", IncludeItemTypes="Movie,Series",
                                             Fields=ITEM_FIELDS, EnableUserData="false", EnableImages="false")]
    episodes = [slim_episode(i) for i in api.paged("/Items", Recursive="true", IncludeItemTypes="Episode",
                                                   Fields="DateCreated,RunTimeTicks", EnableUserData="false",
                                                   EnableImages="false")]
    # Les enfants d'une collection sont filtrés par utilisateur côté Jellyfin (FilterLinkedChildrenPerUser) :
    # sans userId la liste revient vide. On lit donc avec un compte administrateur actif.
    admins = [u["Id"] for u in users
              if (u.get("Policy") or {}).get("IsAdministrator") and not (u.get("Policy") or {}).get("IsDisabled")]
    if not admins:
        raise ApiError("aucun compte administrateur actif : impossible de lire le contenu des collections")
    collections = []
    for b in api.paged("/Items", Recursive="true", IncludeItemTypes="BoxSet", EnableImages="false",
                       userId=admins[0]):
        kids = [k["Id"] for k in api.paged("/Items", ParentId=b["Id"], userId=admins[0], EnableImages="false")]
        collections.append({"id": b["Id"], "name": b.get("Name"), "items": kids})
    if collections and not any(c["items"] for c in collections):
        raise ApiError(f"{len(collections)} collections toutes vides : contenu non lisible, export incomplet")
    stray = sum(i["type"] not in ("Movie", "Series") for i in items)
    if stray:
        raise ApiError(f"{stray} éléments inattendus (collections ?) parmi films et séries : export incomplet")
    log(f"  {sum(i['type'] == 'Movie' for i in items)} films, {sum(i['type'] == 'Series' for i in items)} séries, "
        f"{len(episodes)} épisodes, {len(collections)} collections")

    accounts = []
    for u in users:
        uid, p = u["Id"], u.get("Policy") or {}
        seen = {}
        for flt in ("IsPlayed", "IsResumable", "IsFavorite"):
            for it in api.paged("/Items", userId=uid, Recursive="true", Filters=flt,
                                IncludeItemTypes="Movie,Series,Season,Episode", Fields="RunTimeTicks",
                                EnableImages="false"):
                seen[it["Id"]] = slim_userdata(it)
        accounts.append({
            "user": pseudo[uid], "admin": bool(p.get("IsAdministrator")), "disabled": bool(p.get("IsDisabled")),
            "last_activity": u.get("LastActivityDate"), "last_login": u.get("LastLoginDate"),
            "items": sorted(seen.values(), key=lambda x: x["last"] or ""),
        })
        log(f"  {pseudo[uid]} : {len(seen)} éléments avec historique")

    sessions = []
    if playback_db:
        uri = "file:" + urllib.parse.quote(str(playback_db.resolve())) + "?mode=ro&immutable=1"
        db = sqlite3.connect(uri, uri=True)
        try:
            cols = {r[1] for r in db.execute("PRAGMA table_info(PlaybackActivity)")}
            need = {"DateCreated", "UserId", "ItemId", "ItemType", "PlayDuration"}
            if not need <= cols:
                raise SystemExit(f"{playback_db} : table PlaybackActivity inattendue ({sorted(cols)})")
            norm = {k.replace("-", "").lower(): v for k, v in pseudo.items()}
            for dc, uid, iid, typ, dur in db.execute(
                    "SELECT DateCreated, UserId, ItemId, ItemType, PlayDuration FROM PlaybackActivity"):
                pu = norm.get(str(uid).replace("-", "").lower())
                if pu:
                    sessions.append({"at": dc, "user": pu, "id": str(iid).replace("-", ""), "type": typ,
                                     "secs": dur})
        finally:
            db.close()
        log(f"  {len(sessions)} sessions de lecture (Playback Reporting)")

    data = {"export": {"tool": VERSION, "at": time.strftime("%FT%T%z"), "jellyfin": info.get("Version"),
                       "calls": api.calls},
            "items": items, "episodes": episodes, "collections": collections, "accounts": accounts,
            "sessions": sessions}
    return data, mapping


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Export lecture seule pour la mise au point des recommandations")
    ap.add_argument("--env", default="~/.config/jellyfin-api.env")
    ap.add_argument("--out", default="~/reco-export")
    ap.add_argument("--playback-db", help="chemin de playback_reporting.db (facultatif)")
    a = ap.parse_args(argv)
    env = read_env(Path(a.env).expanduser())
    out = Path(a.out).expanduser()
    out.mkdir(mode=0o700, parents=True, exist_ok=True)
    pdb = Path(a.playback_db).expanduser() if a.playback_db else None
    if pdb and not pdb.is_file():
        raise SystemExit(f"{pdb} absent")
    try:
        data, mapping = export(Api(env["JF_URL"], env["JF_KEY"]), pdb)
    except ApiError as e:
        print(f"ERREUR : {e}", file=sys.stderr)
        return 1
    blob = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode()
    ids = {v for m in mapping.values() for v in (m["id"], m["id"].replace("-", ""))}
    if env["JF_KEY"].encode() in blob or any(i.encode() in blob for i in ids if i):
        print("ERREUR : une donnée d'identification se retrouve dans l'export, rien n'est écrit", file=sys.stderr)
        return 2
    f = out / time.strftime("reco-export-%Y%m%d-%H%M.json.gz")
    with gzip.open(f, "wb", compresslevel=9) as g:
        g.write(blob)
    os.chmod(f, 0o600)
    m = out / "correspondance-comptes.json"
    fd = os.open(m, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as h:
        json.dump(mapping, h, ensure_ascii=False, indent=1)
    print(f"Export : {f} ({f.stat().st_size / 1024:.0f} Kio)  sha256 {hashlib.sha256(f.read_bytes()).hexdigest()}")
    print(f"Correspondance pseudonymes → comptes (reste sur la VM) : {m}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
