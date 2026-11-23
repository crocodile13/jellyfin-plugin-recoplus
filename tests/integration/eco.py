"""Seerr pour le test d'intégration : relié à Jellyfin, comptes importés de Jellyfin, demandes et listes de suivi faites
au nom des comptes simulés. Reco+ ne parle qu'à Seerr : Radarr et Sonarr (le téléchargement) ne sont pas nécessaires ;
l'arrivée d'un titre demandé est simulée en posant son fichier dans la bibliothèque.

Aucun secret écrit : la clé d'API de Seerr est lue sur place, masquée dans le journal de la CI, et confiée à Reco+
seulement par sa route de secrets.
"""
from __future__ import annotations

import http.cookiejar
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request

SEERR = os.environ.get("SEERR_URL", "http://127.0.0.1:5055").rstrip("/")

_jar = http.cookiejar.CookieJar()
_opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(_jar))
SEERR_KEY: str | None = None


def mask(v: str):
    print(f"::add-mask::{v}", flush=True)


def _req(url, method="GET", body=None, headers=None, opener=None, timeout=60):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json", "Accept": "application/json", **(headers or {})})
    try:
        with (opener or urllib.request.build_opener()).open(req, timeout=timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        detail = e.read()[:300].decode(errors="replace")
        e.close()
        raise RuntimeError(f"HTTP {e.code} {method} {url.split('?')[0]} {detail}") from None


def seerr(method, path, body=None, as_user: int | None = None):
    """Appel Seerr : session de l'administrateur (cookie) pendant l'installation, clé d'API ensuite."""
    h = {}
    if SEERR_KEY:
        h["X-Api-Key"] = SEERR_KEY
        if as_user:
            h["X-Api-User"] = str(as_user)
    return _req(SEERR + path, method, body, h, _opener)


def wait(what, fn, timeout=600, every=5):
    t0 = time.time()
    while True:
        try:
            r = fn()
            if r:
                return r
        except Exception:   # service qui démarre
            pass
        if time.time() - t0 > timeout:
            raise RuntimeError(f"délai dépassé : {what}")
        time.sleep(every)


# ------------------------------------------------------------------ Seerr

def setup_seerr(admin_password: str, jellyfin_users: dict[str, str]) -> str:
    """Installe Seerr comme le ferait un administrateur ; renvoie sa clé d'API (masquée)."""
    global SEERR_KEY
    wait("démarrage de Seerr", lambda: _req(SEERR + "/api/v1/status"), timeout=300)
    # connexion Jellyfin de l'administrateur : configure le serveur et crée le compte administrateur de Seerr
    _req(SEERR + "/api/v1/auth/jellyfin", "POST", {"username": "admin", "password": admin_password, "hostname": "jf", "port": 8096,
                                                   "useSsl": False, "urlBase": "", "email": "admin@example.invalid", "serverType": 2},
         opener=_opener)
    _req(SEERR + "/api/v1/settings/jellyfin/library?sync=true", opener=_opener)
    libs = _req(SEERR + "/api/v1/settings/jellyfin/library", opener=_opener)
    for lib in libs:
        _req(SEERR + f"/api/v1/settings/jellyfin/library/{lib['id']}", "PUT", {"enabled": True}, opener=_opener)
    _req(SEERR + "/api/v1/settings/initialize", "POST", {}, opener=_opener)
    SEERR_KEY = _req(SEERR + "/api/v1/settings/main", opener=_opener)["apiKey"]
    mask(SEERR_KEY)
    # comptes Jellyfin importés dans Seerr (droits par défaut : demander)
    jf = seerr("GET", "/api/v1/settings/jellyfin/users")
    ids = [u["id"] for u in jf if u.get("username") in jellyfin_users]
    seerr("POST", "/api/v1/user/import-from-jellyfin", {"jellyfinUserIds": ids})
    print(f"Seerr prêt : {len(ids)} comptes importés", flush=True)
    return SEERR_KEY


def seerr_users() -> dict[str, int]:
    r = seerr("GET", "/api/v1/user?take=100")
    return {u.get("jellyfinUsername") or u.get("displayName"): u["id"] for u in r.get("results", [])}


def tmdb_id(title: str, year: int, media: str) -> int | None:
    r = seerr("GET", "/api/v1/search?" + urllib.parse.urlencode({"query": title, "page": 1, "language": "en"}).replace("+", "%20"))
    for it in r.get("results", []):
        date = it.get("releaseDate") or it.get("firstAirDate") or ""
        if it.get("mediaType") == media and date[:4] == str(year):
            return it["id"]
    return None


def wishes(personas: dict, users: dict[str, int], problems: list[str]) -> dict[str, list[tuple[str, int]]]:
    """Demandes et listes de suivi de chaque compte, faites en son nom. Renvoie compte → [(mediaType, tmdbId)] voulus."""
    out: dict[str, list[tuple[str, int]]] = {}
    for name, p in personas.items():
        for kind in ("requests", "watchlist"):
            for title, year, media in p.get(kind, []):
                tid = tmdb_id(title, year, media)
                if not tid:
                    problems.append(f"{name} : « {title} » introuvable dans Seerr")
                    continue
                if kind == "requests":
                    body = {"mediaType": media, "mediaId": tid, **({"seasons": "all"} if media == "tv" else {})}
                    seerr("POST", "/api/v1/request", body, as_user=users[name])
                else:
                    seerr("POST", "/api/v1/watchlist", {"tmdbId": tid, "mediaType": media, "title": title}, as_user=users[name])
                out.setdefault(name, []).append((media, tid))
        if name in out:
            print(f"  compte {name} : {len(out[name])} demande(s) ou liste de suivi (Seerr)", flush=True)
    return out
