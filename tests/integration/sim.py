#!/usr/bin/env python3
"""Test d'intégration et simulation : un vrai Jellyfin (Docker, sur le runner de la CI), Reco+ installé, une bibliothèque
de vrais titres (fichiers factices, métadonnées TMDb), des comptes simulés ; puis ce que Reco+ propose à chacun.

    python sim.py layout <dossier média> <fichier vidéo modèle>   arborescence de la bibliothèque
    python sim.py run                                             tout le reste (JF_URL, SIM_OUT, SIM_EXPECT)

Aucun secret : le mot de passe administrateur est tiré au hasard à chaque exécution et n'est jamais écrit ; le jeton
d'accès ne sort pas de ce processus. Le rapport ne contient que des titres et des comptes fictifs.
Échec (code 1) si un point d'accroche de Reco+ ne tient pas : vérifications de compatibilité, comptes sans liste, titre
déjà vu proposé, rangée attendue vide.
"""
from __future__ import annotations

import datetime as dt
import json
import os
import re
import secrets
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from catalog import MOVIES, PERSONAS, SERIES

BASE = os.environ.get("JF_URL", "http://127.0.0.1:8096").rstrip("/")
OUT = Path(os.environ.get("SIM_OUT", "sim-out"))
# plugins attendus dans cette configuration (séparés par des virgules) : hss, je, pr
EXPECT = {x for x in os.environ.get("SIM_EXPECT", "").split(",") if x}
CLIENT = 'MediaBrowser Client="RecoPlusSim", Device="CI", DeviceId="recoplus-sim", Version="1.0"'
NOW = dt.datetime.now(dt.timezone.utc)

problems: list[str] = []


def log(*a):
    print(*a, flush=True)


def safe(name: str) -> str:
    return re.sub(r'[<>:"/\\|?*]', "", name)


# ------------------------------------------------------------------ bibliothèque

def layout(media: Path, sample: Path):
    for title, year in MOVIES:
        d = media / "movies" / f"{safe(title)} ({year})"
        d.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(sample, d / f"{safe(title)} ({year}).mkv")
    for title, year, seasons, eps in SERIES:
        for s in range(1, seasons + 1):
            d = media / "shows" / f"{safe(title)} ({year})" / f"Season {s:02d}"
            d.mkdir(parents=True, exist_ok=True)
            for e in range(1, eps + 1):
                shutil.copyfile(sample, d / f"{safe(title)} S{s:02d}E{e:02d}.mkv")
    log(f"bibliothèque : {len(MOVIES)} films, {len(SERIES)} séries")


# ------------------------------------------------------------------ API

class HttpError(RuntimeError):
    def __init__(self, status, method, path, detail=""):
        super().__init__(f"HTTP {status} {method} {path} {detail}".strip())
        self.status = status


TOKEN: str | None = None


def call(method: str, path: str, body=None, query: dict | None = None, timeout=60, tries=20):
    """Appel de l'API ; 503 (serveur qui démarre ou occupé) : nouvel essai, jusqu'à `tries` fois."""
    for k in range(tries):
        try:
            return _call(method, path, body, query, timeout)
        except HttpError as e:
            if e.status != 503 or k == tries - 1:
                raise
            time.sleep(3)


def _call(method: str, path: str, body=None, query: dict | None = None, timeout=60):
    url = BASE + path + ("?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None}) if query else "")
    data = None if body is None else json.dumps(body).encode()
    auth = CLIENT + (f', Token="{TOKEN}"' if TOKEN else "")
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Authorization": auth, "Content-Type": "application/json", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw and r.headers.get_content_type() == "application/json" else None
    except urllib.error.HTTPError as e:
        detail = e.read()[:200].decode(errors="replace")
        e.close()
        raise HttpError(e.code, method, path, detail) from None


def wait(what: str, fn, timeout=600, every=5):
    t0 = time.time()
    while True:
        try:
            r = fn()
            if r:
                return r
        except (HttpError, urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        if time.time() - t0 > timeout:
            raise RuntimeError(f"délai dépassé : {what}")
        time.sleep(every)


def iso(days_ago: float) -> str:
    return (NOW - dt.timedelta(days=days_ago)).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")


# ------------------------------------------------------------------ mise en place

def setup():
    global TOKEN
    info = wait("démarrage de Jellyfin", lambda: (lambda i: i if isinstance(i, dict) and i.get("Version") else None)(call("GET", "/System/Info/Public", tries=1)),
                timeout=300, every=3)
    log(f"Jellyfin {info.get('Version')}")
    password = secrets.token_urlsafe(24)   # jamais écrit nulle part
    if not info.get("StartupWizardCompleted"):
        call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        call("GET", "/Startup/User")
        call("POST", "/Startup/User", {"Name": "admin", "Password": password})
        call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True})
        call("POST", "/Startup/Complete")
    auth = call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": password})
    TOKEN = auth["AccessToken"]
    print(f"::add-mask::{TOKEN}", flush=True)

    for name, kind, path in (("Films", "movies", "/media/movies"), ("Séries", "tvshows", "/media/shows")):
        call("POST", "/Library/VirtualFolders", {"LibraryOptions": {"PathInfos": [{"Path": path}]}},
             {"name": name, "collectionType": kind, "refreshLibrary": "false"})
    call("POST", "/Library/Refresh")

    def scanned():
        r = call("GET", "/Items", query={"IncludeItemTypes": "Movie,Series", "Recursive": "true", "Fields": "Genres", "Limit": 1000})
        items = r.get("Items", [])
        with_meta = sum(1 for i in items if i.get("Genres"))
        log(f"  analyse : {len(items)} titres, {with_meta} avec genres")
        return len(items) >= 0.95 * (len(MOVIES) + len(SERIES)) and with_meta >= 0.8 * len(items) and not refreshing()
    wait("analyse de la bibliothèque", scanned, timeout=1500, every=20)

    for name in PERSONAS:
        call("POST", "/Users/New", {"Name": name, "Password": secrets.token_urlsafe(18)})


def refreshing() -> bool:
    tasks = call("GET", "/ScheduledTasks", query={"isHidden": "false"})
    return any(t.get("Key") == "RefreshLibrary" and t.get("State") != "Idle" for t in tasks)


# ------------------------------------------------------------------ comptes simulés

def library():
    r = call("GET", "/Items", query={"IncludeItemTypes": "Movie,Series", "Recursive": "true",
                                       "Fields": "Path,ProductionYear,Genres,OfficialRating", "Limit": 2000})
    by_folder = {}
    for it in r["Items"]:
        folder = Path(it.get("Path") or "").name if it["Type"] == "Series" else Path(it.get("Path") or "").parent.name
        by_folder[folder] = it
    return by_folder


def find(lib, title, year=None):
    for (t, y) in [(m[0], m[1]) for m in MOVIES] + [(s[0], s[1]) for s in SERIES]:
        if t == title and (year is None or y == year):
            it = lib.get(f"{safe(t)} ({y})")
            if it:
                return it
    raise KeyError(title)


def post_either(paths, query=None, body=None):
    last = None
    for p in paths:
        try:
            return call("POST", p, body, query)
        except HttpError as e:
            if e.status != 404:
                raise
            last = e
    raise last


def simulate(lib, users):
    for name, p in PERSONAS.items():
        uid = users[name]
        for title, days in p.get("watched", []):
            it = find(lib, title)
            if it["Type"] == "Series":
                episodes(it, uid, 10_000, days)
            else:
                post_either([f"/UserPlayedItems/{it['Id']}", f"/Users/{uid}/PlayedItems/{it['Id']}"], {"userId": uid, "datePlayed": iso(days)})
        for title, n, days in p.get("episodes", []):
            episodes(find(lib, title), uid, n, days)
        for title, part, days in p.get("abandoned", []):
            it = find(lib, title)
            full = call("GET", f"/Items/{it['Id']}", query={"userId": uid}) or {}
            ticks = int((full.get("RunTimeTicks") or 72_000_000_000) * part)
            post_either([f"/UserItems/{it['Id']}/UserData", f"/Users/{uid}/Items/{it['Id']}/UserData"], {"userId": uid},
                        {"PlaybackPositionTicks": ticks, "LastPlayedDate": iso(days), "Played": False})
        for title in p.get("favorites", []):
            it = find(lib, title)
            post_either([f"/UserFavoriteItems/{it['Id']}", f"/Users/{uid}/FavoriteItems/{it['Id']}"], {"userId": uid})
        for title in p.get("dislikes", []):
            it = find(lib, title)
            post_either([f"/UserItems/{it['Id']}/Rating", f"/Users/{uid}/Items/{it['Id']}/Rating"], {"userId": uid, "likes": "false"})
        log(f"  compte {name} : historique simulé")


def episodes(series, uid, n, days):
    eps = call("GET", f"/Shows/{series['Id']}/Episodes", query={"userId": uid}).get("Items", [])
    eps.sort(key=lambda e: (e.get("ParentIndexNumber") or 0, e.get("IndexNumber") or 0))
    eps = eps[:n]
    for k, e in enumerate(eps):
        # un épisode par jour, le dernier il y a `days` jours (pas un marquage en masse)
        post_either([f"/UserPlayedItems/{e['Id']}", f"/Users/{uid}/PlayedItems/{e['Id']}"],
                    {"userId": uid, "datePlayed": iso(days + len(eps) - 1 - k)})


# ------------------------------------------------------------------ Reco+

def task(key):
    return next((t for t in call("GET", "/ScheduledTasks", query={"isHidden": "false"}) if t.get("Key") == key), None)


def recompute():
    before = (task("RecoPlusNightly") or {}).get("LastExecutionResult") or {}
    call("POST", "/RecoPlus/Recompute")

    def done():
        t = task("RecoPlusNightly") or {}
        last = t.get("LastExecutionResult") or {}
        return t.get("State") == "Idle" and last and last.get("EndTimeUtc") != before.get("EndTimeUtc") and last
    res = wait("calcul de Reco+", done, timeout=900, every=10)
    log(f"calcul complet : {res.get('Status')}")
    if res.get("Status") != "Completed":
        problems.append(f"calcul complet : {res.get('Status')} {res.get('ErrorMessage') or ''}")


def names(ids_or_items):
    return [i["Name"] for i in ids_or_items]


def report(lib, users):
    status = call("GET", "/RecoPlus/Status", query={"lang": "fr"})
    out = {"jellyfin": call("GET", "/System/Info/Public").get("Version"), "status": {
        "version": status.get("version"), "checks": status.get("checks"), "disabled": status.get("disabled"), "hss": status.get("hss"),
        "engine": {k: status["engine"].get(k) for k in ("UsersWithRecs", "LastError", "PlaybackReporting", "JellyfinEnhanced", "Seerr")},
        "alerts": status.get("alerts")}, "accounts": {}}
    for c in status.get("checks") or []:
        name = c["check"]
        optional = any(x in name for x in ("Home Screen Sections", "Jellyfin Enhanced", "Media Bar", "LocalRecs"))
        wanted = ("hss" in EXPECT and "Home Screen" in name) or ("je" in EXPECT and "Enhanced" in name)
        if not c["ok"] and (not optional or wanted):
            problems.append(f"vérification « {name} » : {c['detail']}")
    if status.get("disabled"):
        problems.append(f"parties coupées : {status['disabled']}")
    if "hss" in EXPECT and "déclarées" not in (status.get("hss") or ""):
        problems.append(f"Home Screen Sections : {status.get('hss')}")

    for name, p in PERSONAS.items():
        uid = users[name]
        seen = {find(lib, t)["Name"] for t, _ in p.get("watched", [])} | {find(lib, t)["Name"] for t, _, _ in p.get("episodes", [])}
        prev = call("GET", "/RecoPlus/Preview", query={"userId": uid, "lang": "fr"})
        items = [re.sub(r"^📌 ", "", x).removesuffix(" (série)") for x in prev.get("items") or []]
        sugg = call("GET", "/Items/Suggestions", query={"userId": uid, "limit": 15, "type": "Movie,Series"})
        recs = call("GET", "/Movies/Recommendations", query={"userId": uid, "categoryLimit": 3, "itemLimit": 6})
        anchor = next((find(lib, t) for t, _ in p.get("watched", []) if find(lib, t)["Type"] == "Movie"), None)
        similar = call("GET", f"/Items/{anchor['Id']}/Similar", query={"userId": uid, "limit": 8}) if anchor else None
        acc = {
            "attendu": p["attendu"], "vu": sorted(seen), "pour_toi": items, "rangees": prev.get("rows") or {},
            "suggestions": names(sugg.get("Items", [])),
            "recommandations_films": [{"parce_que": r.get("BaselineItemName"), "titres": names(r.get("Items", []))} for r in recs or []],
            "plus_comme_ceci": {"de": anchor["Name"], "titres": names(similar.get("Items", []))} if anchor else None,
        }
        out["accounts"][name] = acc
        if not items:
            problems.append(f"{name} : aucune liste")
        bad = seen & (set(items) | set(acc["suggestions"]))
        if bad:
            problems.append(f"{name} : titres déjà vus proposés {sorted(bad)}")
        if name != "nouveau" and not any(v for k, v in acc["rangees"].items()):
            problems.append(f"{name} : toutes les rangées sont vides")
    return out


def markdown(r) -> str:
    L = [f"## Reco+ {r['status']['version']} sur Jellyfin {r['jellyfin']} ({', '.join(sorted(EXPECT)) or 'Reco+ seul'})", ""]
    L.append("| Vérification | | Détail |\n|---|---|---|")
    for c in r["status"]["checks"] or []:
        L.append(f"| {c['check']} | {'✔' if c['ok'] else '✖'} | {c['detail']} |")
    L.append("")
    L.append(f"Home Screen Sections : {r['status']['hss']}  ")
    L.append(f"Comptes prêts : {r['status']['engine']['UsersWithRecs']} — erreur : {r['status']['engine']['LastError'] or 'aucune'}")
    L.append("")
    L.append("### Problèmes" if problems else "### Aucun problème détecté")
    L += [f"- {p}" for p in problems]
    for name, a in r["accounts"].items():
        L += ["", f"### {name}", f"*Attendu : {a['attendu']}*", "", f"**Vu** : {', '.join(a['vu']) or '—'}", "",
              f"**Pour toi** : {', '.join(a['pour_toi'][:15])}", ""]
        for row, titles in a["rangees"].items():
            L.append(f"- **{row}** : {', '.join(titles[:10]) or '(vide)'}")
        L.append(f"- **/Items/Suggestions** : {', '.join(a['suggestions'][:10])}")
        for c in a["recommandations_films"]:
            L.append(f"- **/Movies/Recommendations — parce que {c['parce_que']}** : {', '.join(c['titres'])}")
        if a["plus_comme_ceci"]:
            L.append(f"- **Plus comme ceci ({a['plus_comme_ceci']['de']})** : {', '.join(a['plus_comme_ceci']['titres'])}")
    return "\n".join(L) + "\n"


def run():
    OUT.mkdir(parents=True, exist_ok=True)
    setup()
    lib = library()
    log(f"bibliothèque indexée : {len(lib)} titres")
    users = {u["Name"]: u["Id"] for u in call("GET", "/Users")}
    simulate(lib, users)
    # Reco+ : vérifications de compatibilité 20 s après le démarrage, planificateur 60 s après
    wait("vérifications de Reco+", lambda: call("GET", "/RecoPlus/Status").get("checks"), timeout=300)
    recompute()
    r = report(lib, users)
    r["problems"] = problems
    (OUT / "report.json").write_text(json.dumps(r, ensure_ascii=False, indent=2), encoding="utf-8")
    md = markdown(r)
    (OUT / "report.md").write_text(md, encoding="utf-8")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
            f.write(md)
    log(md)
    return 1 if problems else 0


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "layout":
        layout(Path(sys.argv[2]), Path(sys.argv[3]))
    elif len(sys.argv) >= 2 and sys.argv[1] == "run":
        sys.exit(run())
    else:
        print(__doc__)
        sys.exit(2)
