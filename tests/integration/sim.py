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
import xml.etree.ElementTree as ET
from pathlib import Path

import eco
from catalog import MOVIES, PERSONAS, SERIES

BASE = os.environ.get("JF_URL", "http://127.0.0.1:8096").rstrip("/")
OUT = Path(os.environ.get("SIM_OUT", "sim-out"))
# plugins attendus dans cette configuration (séparés par des virgules) : hss, je, pr
EXPECT = {x for x in os.environ.get("SIM_EXPECT", "").split(",") if x}
CLIENT = 'MediaBrowser Client="RecoPlusSim", Device="CI", DeviceId="recoplus-sim", Version="1.0"'
NOW = dt.datetime.now(dt.timezone.utc)

problems: list[str] = []


def safe(name: str) -> str:
    return re.sub(r'[<>:"/\\|?*]', "", name)


def log(*a):
    print(*a, flush=True)


# ------------------------------------------------------------------ bibliothèque

NFO = Path(__file__).with_name("nfo")   # métadonnées figées (voir export_nfo) : bibliothèque analysée sans Internet
# hors ligne seulement si chaque titre a sa fiche ; sinon en ligne, et les fiches sont exportées (artifact) pour la suite
OFFLINE = NFO.is_dir() and all((NFO / f"{safe(t)} ({y}).nfo").is_file() for t, y in [(m[0], m[1]) for m in MOVIES] + [(x[0], x[1]) for x in SERIES])


def layout(media: Path, sample: Path):
    for title, year in MOVIES:
        d = media / "movies" / f"{safe(title)} ({year})"
        d.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(sample, d / f"{safe(title)} ({year}).mkv")
        if (NFO / f"{safe(title)} ({year}).nfo").is_file():
            shutil.copyfile(NFO / f"{safe(title)} ({year}).nfo", d / "movie.nfo")
    for title, year, seasons, eps in SERIES:
        if (NFO / f"{safe(title)} ({year}).nfo").is_file():
            (media / "shows" / f"{safe(title)} ({year})").mkdir(parents=True, exist_ok=True)
            shutil.copyfile(NFO / f"{safe(title)} ({year}).nfo", media / "shows" / f"{safe(title)} ({year})" / "tvshow.nfo")
        for s in range(1, seasons + 1):
            d = media / "shows" / f"{safe(title)} ({year})" / f"Season {s:02d}"
            d.mkdir(parents=True, exist_ok=True)
            for e in range(1, eps + 1):
                shutil.copyfile(sample, d / f"{safe(title)} S{s:02d}E{e:02d}.mkv")
    log(f"bibliothèque : {len(MOVIES)} films, {len(SERIES)} séries ({'métadonnées figées (.nfo)' if OFFLINE else 'métadonnées TMDb en ligne'})")


def export_nfo(out: Path):
    """Métadonnées de la bibliothèque analysée (TMDb via Jellyfin) écrites en .nfo : les runs suivants s'en servent hors ligne."""
    out.mkdir(parents=True, exist_ok=True)
    r = call("GET", "/Items", query={"IncludeItemTypes": "Movie,Series", "Recursive": "true", "Limit": 2000,
                                       "Fields": "Path,Genres,Tags,Studios,People,ProviderIds,ProductionLocations,OriginalTitle,PremiereDate,OfficialRating"})
    n = 0
    for it in r["Items"]:
        full = call("GET", f"/Items/{it['Id']}")          # notes, langue d'origine : fiche complète
        root = ET.Element("tvshow" if it["Type"] == "Series" else "movie")
        def add(tag, val, **attrs):
            if val not in (None, ""):
                ET.SubElement(root, tag, {k: str(v) for k, v in attrs.items()}).text = str(val)
        add("title", it.get("Name"))
        add("originaltitle", it.get("OriginalTitle"))
        add("year", it.get("ProductionYear"))
        add("premiered", (it.get("PremiereDate") or "")[:10])
        add("rating", full.get("CommunityRating"))
        add("criticrating", full.get("CriticRating"))
        add("mpaa", it.get("OfficialRating"))
        for g in it.get("Genres") or []:
            add("genre", g)
        for t in it.get("Tags") or []:
            add("tag", t)
        for st in it.get("Studios") or []:
            add("studio", st.get("Name"))
        for c in it.get("ProductionLocations") or []:
            add("country", c)
        ids = it.get("ProviderIds") or {}
        if ids.get("Tmdb"):
            add("uniqueid", ids["Tmdb"], type="tmdb", default="true")
        if ids.get("Imdb"):
            add("uniqueid", ids["Imdb"], type="imdb")
        if ids.get("TmdbCollection"):
            s_ = ET.SubElement(root, "set", {"tmdbcolid": ids["TmdbCollection"]})
            ET.SubElement(s_, "name").text = ids["TmdbCollection"]
        for k, person in enumerate(it.get("People") or []):
            kind = person.get("Type")
            if kind in ("Actor", "GuestStar") and k < 30:
                a = ET.SubElement(root, "actor")
                ET.SubElement(a, "name").text = person.get("Name")
                ET.SubElement(a, "role").text = person.get("Role") or ""
                ET.SubElement(a, "sortorder").text = str(k)
            elif kind == "Director":
                add("director", person.get("Name"))
            elif kind == "Writer":
                add("credits", person.get("Name"))
        folder = Path(it.get("Path") or "").name if it["Type"] == "Series" else Path(it.get("Path") or "").parent.name
        ET.indent(root)
        (out / f"{folder}.nfo").write_text(ET.tostring(root, encoding="unicode", xml_declaration=False), encoding="utf-8")
        n += 1
    log(f"{n} fiches .nfo exportées dans {out}")


# ------------------------------------------------------------------ API

class HttpError(RuntimeError):
    def __init__(self, status, method, path, detail=""):
        super().__init__(f"HTTP {status} {method} {path} {detail}".strip())
        self.status = status


TOKEN: str | None = None
USERS_IDS: dict = {}


def call(method: str, path: str, body=None, query: dict | None = None, timeout=60, tries=20, token: str | None = None):
    """Appel de l'API (administrateur, ou le compte du jeton donné) ; 503 (serveur qui démarre ou occupé) : nouvel essai."""
    for k in range(tries):
        try:
            return _call(method, path, body, query, timeout, token)
        except HttpError as e:
            if e.status != 503 or k == tries - 1:
                raise
            time.sleep(3)


def _call(method: str, path: str, body=None, query: dict | None = None, timeout=60, token: str | None = None):
    url = BASE + path + ("?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None}) if query else "")
    data = None if body is None else json.dumps(body).encode()
    tok = token or TOKEN
    auth = CLIENT + (f', Token="{tok}"' if tok else "")
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
    global ADMIN_PASSWORD
    password = ADMIN_PASSWORD = secrets.token_urlsafe(24)   # jamais écrit nulle part
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
        opts = {"PathInfos": [{"Path": path}]}
        if OFFLINE:
            # métadonnées lues dans les .nfo : aucun téléchargement (fiches, images), aucune extraction d'images
            types = ("Movie",) if kind == "movies" else ("Series", "Season", "Episode")
            # captures (SIM_SHOTS) : les affiches viennent de TMDb, les fiches restent celles des .nfo
            img = ["TheMovieDb"] if os.environ.get("SIM_SHOTS") and t_poster(kind) else []
            opts.update({"TypeOptions": [{"Type": t, "MetadataFetchers": [], "ImageFetchers": img if t in ("Movie", "Series") else [], "MetadataFetcherOrder": [],
                                          "ImageFetcherOrder": img if t in ("Movie", "Series") else []} for t in types],
                         "MetadataSavers": [], "EnableChapterImageExtraction": False, "ExtractChapterImagesDuringLibraryScan": False,
                         "EnableTrickplayImageExtraction": False, "ExtractTrickplayImagesDuringLibraryScan": False,
                         "EnableEmbeddedTitles": False, "EnableEmbeddedExtrasTitles": False, "EnableEmbeddedEpisodeInfos": False})
        call("POST", "/Library/VirtualFolders", {"LibraryOptions": opts}, {"name": name, "collectionType": kind, "refreshLibrary": "false"})
    call("POST", "/Library/Refresh")

    def scanned():
        r = call("GET", "/Items", query={"IncludeItemTypes": "Movie,Series", "Recursive": "true", "Fields": "Genres", "Limit": 1000})
        items = r.get("Items", [])
        with_meta = sum(1 for i in items if i.get("Genres"))
        log(f"  analyse : {len(items)} titres, {with_meta} avec genres")
        return len(items) >= 0.95 * (len(MOVIES) + len(SERIES)) and with_meta >= 0.8 * len(items) and not refreshing()
    wait("analyse de la bibliothèque", scanned, timeout=1500, every=5 if OFFLINE else 20)

    for name in PERSONAS:
        PASSWORDS[name] = secrets.token_urlsafe(18)   # en mémoire seulement
        call("POST", "/Users/New", {"Name": name, "Password": PASSWORDS[name]})


PASSWORDS: dict[str, str] = {}
ADMIN_PASSWORD = ""
WISHED: dict[str, list] = {}   # compte → [(mediaType, tmdbId)] demandés ou en liste de suivi
_tokens: dict[str, str] = {}


def as_user(name: str) -> str:
    """Jeton d'un compte simulé (masqué dans le journal de la CI)."""
    if name not in _tokens:
        t = call("POST", "/Users/AuthenticateByName", {"Username": name, "Pw": PASSWORDS[name]})["AccessToken"]
        print(f"::add-mask::{t}", flush=True)
        _tokens[name] = t
    return _tokens[name]


def refreshing() -> bool:
    tasks = call("GET", "/ScheduledTasks", query={"isHidden": "false"})
    return any(t.get("Key") == "RefreshLibrary" and t.get("State") != "Idle" for t in tasks)


# ------------------------------------------------------------------ comptes simulés

def library():
    r = call("GET", "/Items", query={"IncludeItemTypes": "Movie,Series", "Recursive": "true",
                                       "Fields": "Path,ProductionYear,Genres,OfficialRating,ProviderIds", "Limit": 2000})
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
        bulk = set(p.get("bulk", []))
        for k, (title, days) in enumerate(p.get("watched", [])):
            it = find(lib, title)
            # un import en masse : tous à quelques secondes d'écart ; sinon chaque titre à sa propre heure
            when = iso(days) if title in bulk else iso(days + (k % 7) / 24)
            if it["Type"] == "Series":
                episodes(it, uid, 10_000, days)
            else:
                post_either([f"/UserPlayedItems/{it['Id']}", f"/Users/{uid}/PlayedItems/{it['Id']}"], {"userId": uid, "datePlayed": when})
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


# ------------------------------------------------------------------ plugins compagnons

def plugin_id(name_part: str):
    return next((p["Id"] for p in call("GET", "/Plugins") if name_part.lower() in p["Name"].lower()), None)


def setup_hss():
    """Comme le bouton « Les ajouter pour moi » de la page : Reco+ active Home Screen Sections et y ajoute ses rangées."""
    r = call("POST", "/RecoPlus/HomeSections/Setup", {}, query={"lang": "fr"})
    log(f"Home Screen Sections : {r.get('message')}")
    st = call("GET", "/RecoPlus/Status").get("hssSetup") or {}
    if r.get("added", -1) <= 0 or not st.get("Enabled") or st.get("InTable") != st.get("Declared"):
        problems.append(f"mise en route de Home Screen Sections : {r.get('message')} — état {st}")
    again = call("POST", "/RecoPlus/HomeSections/Setup", {})
    if again.get("added") != 0:
        problems.append(f"Home Screen Sections : un second passage ajoute encore {again.get('added')} rangée(s)")


def hss_own_choice_check():
    """
    Ticket #80 : un compte qui a enregistré son propre choix de sections avant qu'une rangée Reco+ n'arrive au tableau ne la
    voyait jamais. Ici « potter » enregistre un choix sans « Nouveautés pour toi » ; l'assistant la coche chez lui, une
    fois ; décochée ensuite par le compte, elle le reste.
    """
    tok = as_user("potter")
    row = "RecoPlusNouveautes"

    def mine():
        return call("GET", "/ModularHomeViews/UserSettings", token=tok).get("EnabledSections") or []

    def save(sections):
        # HSS 3.0 : tant que son fichier des choix n'existe pas, un enregistrement crée un fichier vide sans rien y mettre ;
        # on renvoie donc jusqu'à le relire (deux fois au plus)
        for _ in range(2):
            st = call("GET", "/ModularHomeViews/UserSettings", token=tok)
            st["EnabledSections"] = sections
            call("POST", "/ModularHomeViews/UserSettings", st, token=tok)
            if sorted(mine()) == sorted(sections):
                return
        raise RuntimeError(f"choix du compte non enregistré par Home Screen Sections ({mine()})")

    save([x for x in mine() if x != row])
    call("POST", "/RecoPlus/HomeSections/Setup", {})
    if row not in mine():
        problems.append(f"Home Screen Sections : rangée jamais proposée à un compte qui a son propre choix, pas cochée chez lui ({mine()})")
    save([x for x in mine() if x != row])
    call("POST", "/RecoPlus/HomeSections/Setup", {})
    if row in mine():
        problems.append("Home Screen Sections : rangée décochée par le compte recochée par Reco+")
    # l'aperçu le dit
    hidden = call("GET", "/RecoPlus/Preview", query={"userId": USERS_IDS.get("potter"), "lang": "fr"}).get("hiddenRows") or []
    if "Nouveautés pour toi" not in hidden:
        problems.append(f"aperçu : rangée décochée par le compte non signalée ({hidden})")
    save(mine() + [row])   # remise en état pour la suite du test
    log("Home Screen Sections, choix propre d'un compte : rangée nouvelle cochée une fois, décochée respectée")


def setup_je(lib, users):
    """Masquages (« pas intéressé ») et avis notés, faits par chaque compte avec les routes de Jellyfin Enhanced."""
    for name, p in PERSONAS.items():
        tok = None
        items = {}
        for title in p.get("hidden", []):
            it = find(lib, title)
            items[it["Id"]] = {"ItemId": it["Id"], "Name": it["Name"], "Type": it["Type"], "TmdbId": (it.get("ProviderIds") or {}).get("Tmdb", ""),
                               "HideScope": "global", "HiddenAt": iso(1)}
        if items:
            tok = tok or as_user(name)
            call("POST", f"/JellyfinEnhanced/user-settings/{users[name]}/hidden-content.json", {"Items": items}, token=tok)
        for title, stars in p.get("reviews", []):
            it = find(lib, title)
            tmdb = (it.get("ProviderIds") or {}).get("Tmdb")
            if not tmdb:
                problems.append(f"{name} : pas d'identifiant TMDb pour {title} (avis impossible)")
                continue
            tok = tok or as_user(name)
            call("POST", f"/JellyfinEnhanced/reviews/{'tv' if it['Type'] == 'Series' else 'movie'}/{tmdb}", {"Content": "", "Rating": stars}, token=tok)
        if items or p.get("reviews"):
            log(f"  compte {name} : {len(items)} masqué(s), {len(p.get('reviews', []))} avis (Jellyfin Enhanced)")


def setup_seerr(users):
    """Seerr relié à Jellyfin, demandes et listes de suivi des comptes, puis Reco+ relié à Seerr (mode étendu)."""
    key = eco.setup_seerr(ADMIN_PASSWORD, users)
    wished = eco.wishes(PERSONAS, eco.seerr_users(), problems)
    pid = plugin_id("Reco+")
    cfg = call("GET", f"/Plugins/{pid}/Configuration")
    # test seulement : Seerr ne sert personne d'autre (rythme plus élevé) et une exploration plus courte suffit au circuit
    cfg.update({"SeerrEnabled": True, "SeerrUrl": "http://seerr:5055", "ExtendedEnabled": True, "SeerrRate": 30,
                "ExtendedAnchors": 15, "ExtendedDetails": 120})
    call("POST", f"/Plugins/{pid}/Configuration", cfg)
    call("POST", "/RecoPlus/Secret", {"Name": "SeerrApiKey", "Value": key})   # jamais dans la configuration
    if "je" in EXPECT:
        # Jellyfin Enhanced relié à Seerr, comme chez la plupart des utilisateurs du mode étendu : Reco+ réutilise alors ses
        # cartes et sa fiche (chemin du ticket #50, jamais vu dans un navigateur auparavant)
        jid = plugin_id("Jellyfin Enhanced")
        je = call("GET", f"/Plugins/{jid}/Configuration")
        je.update({"JellyseerrEnabled": True, "JellyseerrUrls": "http://seerr:5055", "JellyseerrApiKey": key})
        call("POST", f"/Plugins/{jid}/Configuration", je)
        log("Jellyfin Enhanced relié à Seerr")
    t = call("POST", "/RecoPlus/TestSeerr", {}, query={"lang": "fr"})
    log(f"Reco+ → Seerr : {t.get('message')}")
    if not t.get("ok"):
        problems.append(f"test Seerr de Reco+ : {t.get('message')}")
    return wished


def arrive(lib_before):
    """Une demande arrive : son fichier est posé dans la bibliothèque (comme après Radarr), puis la bibliothèque est relue."""
    media = Path(os.environ.get("SIM_MEDIA", "../../media"))
    arrived = {}
    for name, p in PERSONAS.items():
        if p.get("arrive"):
            title, year = p["arrive"]
            d = media / "movies" / f"{safe(title)} ({year})"
            d.mkdir(parents=True, exist_ok=True)
            sample = next((media / "movies").rglob("*.mkv"))
            shutil.copyfile(sample, d / f"{safe(title)} ({year}).mkv")
            # fiche du titre (comme Radarr la laisserait avec ses métadonnées) : la bibliothèque est analysée hors ligne
            tid = eco.tmdb_id(title, year, "movie")
            det = eco.seerr("GET", f"/api/v1/movie/{tid}") if tid else {}
            root = ET.Element("movie")
            for tag, val in (("title", det.get("title") or title), ("year", year), ("premiered", det.get("releaseDate")), ("rating", det.get("voteAverage"))):
                if val:
                    ET.SubElement(root, tag).text = str(val)
            for g in det.get("genres") or []:
                ET.SubElement(root, "genre").text = g.get("name")
            for k in ((det.get("keywords") or [])[:15]):
                ET.SubElement(root, "tag").text = k.get("name")
            if tid:
                ET.SubElement(root, "uniqueid", {"type": "tmdb", "default": "true"}).text = str(tid)
            ET.indent(root)
            (d / "movie.nfo").write_text(ET.tostring(root, encoding="unicode"), encoding="utf-8")
            arrived[name] = f"{safe(title)} ({year})"
    if not arrived:
        return {}
    call("POST", "/Library/Refresh")
    lib = wait("arrivée dans la bibliothèque", lambda: (lambda l: l if all(k in l for k in arrived.values()) else None)(library()),
               timeout=300, every=5)
    return {name: lib[folder]["Name"] for name, folder in arrived.items()}


# ------------------------------------------------------------------ Reco+

def task(key):
    return next((t for t in call("GET", "/ScheduledTasks", query={"isHidden": "false"}) if t.get("Key") == key), None)


def recompute():
    # un calcul déjà en cours (celui du démarrage, « aucune liste en cache ») ne compte pas : seulement un calcul qui a
    # démarré après la demande (sinon le rapport lisait les listes d'avant l'exploration Seerr)
    def started(last):
        # Jellyfin écrit jusqu'à 7 décimales et un « Z » : ramenées à ce que fromisoformat accepte
        try:
            return dt.datetime.fromisoformat(re.sub(r"(\.\d{6})\d+", r"\1", str(last.get("StartTimeUtc", ""))).replace("Z", "+00:00"))
        except ValueError:
            return None

    # le dernier calcul connu avant la demande, terminé ou en cours : seul un calcul démarré ensuite compte. Une marge
    # d'horloge (2 s) laissait passer un calcul lancé juste avant par l'enregistrement de la configuration de Seerr, sans
    # sa clé : le rapport lisait les listes d'avant Seerr (12.2, eco)
    before = task("RecoPlusNightly") or {}
    prev = started(before.get("LastExecutionResult") or {}) if before.get("State") == "Idle" else dt.datetime.now(dt.timezone.utc)
    call("POST", "/RecoPlus/Recompute")

    def done():
        t = task("RecoPlusNightly") or {}
        last = t.get("LastExecutionResult") or {}
        st = started(last) if last else None
        return t.get("State") == "Idle" and st is not None and (prev is None or st > prev) and last
    res = wait("calcul de Reco+", done, timeout=900, every=3)
    log(f"calcul complet : {res.get('Status')}")
    if res.get("Status") != "Completed":
        problems.append(f"calcul complet : {res.get('Status')} {res.get('ErrorMessage') or ''}")


def measure(lib, name, uid, p, items):
    """Banc : rang des titres gardés de côté (vraiment regardés ensuite) chez Reco+ et chez la popularité dans le foyer ;
    part du début de « Pour toi » dans les goûts du compte."""
    import bench
    ranked = names(call("GET", "/Items/Suggestions", query={"userId": uid, "limit": 500, "type": "Movie,Series"}).get("Items", []))
    held = [find(lib, t)["Name"] for t, _, _ in p["holdout"]]
    known = {find(lib, t)["Name"] for t, *_ in p.get("watched", []) + p.get("episodes", []) + p.get("abandoned", [])}
    known |= {find(lib, t)["Name"] for t in p.get("favorites", []) + p.get("dislikes", [])}
    # popularité dans le foyer : nombre de comptes qui ont regardé chaque titre (hors titres gardés de côté)
    count: dict[str, int] = {}
    for q in PERSONAS.values():
        for t, *_ in q.get("watched", []) + q.get("episodes", []):
            n_ = find(lib, t)["Name"]
            count[n_] = count.get(n_, 0) + 1
    pool = sorted((i["Name"] for i in lib.values() if i["Name"] not in known), key=lambda x: (-count.get(x, 0), x))
    rank = lambda lst, x: lst.index(x) + 1 if x in lst else None
    uni = bench.universe_of()
    name_uni = {find(lib, t)["Name"]: us for t, us in uni.items()}
    mine = {u for u, w in (p.get("univers") or {}).items() if w >= .15}
    top = items[:20]
    share = sum(1 for t in top if name_uni.get(t, set()) & mine) / len(top) if top and mine else None
    return {"gardes": held, "reco": [rank(ranked, h) for h in held], "popularite": [rank(pool, h) for h in held], "dans_ses_gouts": share}


def hss_row(name, uid, section):
    """Ce que Home Screen Sections renvoie au compte pour une rangée Reco+ (sa propre route, en tant que ce compte)."""
    try:
        r = call("GET", f"/HomeScreen/Section/{section}", query={"userId": uid}, token=as_user(name))
    except HttpError as e:
        problems.append(f"{name} : Home Screen Sections, rangée {section} : {e}")
        return []
    return [i["Name"] for i in (r or {}).get("Items", [])]


def names(ids_or_items):
    return [i["Name"] for i in ids_or_items]


def report(lib, users):
    status = call("GET", "/RecoPlus/Status", query={"lang": "fr"})
    out = {"jellyfin": call("GET", "/System/Info/Public").get("Version"), "status": {
        "version": status.get("version"), "checks": status.get("checks"), "disabled": status.get("disabled"), "hss": status.get("hss"),
        "engine": {k: status["engine"].get(k) for k in ("UsersWithRecs", "LastError", "PlaybackReporting", "JellyfinEnhanced", "Seerr", "TmdbLists", "TmdbListCounts")},
        "alerts": status.get("alerts")}, "accounts": {}}
    for c in status.get("checks") or []:
        name = c["check"]
        optional = any(x in name for x in ("Home Screen Sections", "Jellyfin Enhanced", "Media Bar", "LocalRecs"))
        wanted = ("hss" in EXPECT and "Home Screen" in name) or ("je" in EXPECT and "Enhanced" in name)
        if not c["ok"] and (not optional or wanted):
            problems.append(f"vérification « {name} » : {c['detail']}")
    if status.get("disabled"):
        problems.append(f"parties coupées : {status['disabled']}")
    je_state = status["engine"].get("JellyfinEnhanced") or ""
    if "je" in EXPECT and not re.search(r"[1-9]\d* titres masqués, [1-9]\d* avis", je_state):
        problems.append(f"Jellyfin Enhanced : masquages ou avis non lus ({je_state})")
    if "seerr" in EXPECT and not (status["engine"].get("Seerr") or "").startswith("connecté"):
        problems.append(f"Seerr vu par Reco+ : {status['engine'].get('Seerr')}")
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
        # aperçu visuel (onglet Aperçu) : mêmes rangées, « Pour toi » en tête, titres hors bibliothèque à leur place
        cards = prev.get("cards") or []
        acc["apercu_cartes"] = {c["Title"]: len(c["Cards"]) for c in cards}
        if items and (not cards or cards[0]["Id"] not in ("PourToi", "PourToiFilms", "PourToiSeries")):
            problems.append(f"{name} : aperçu visuel sans « Pour toi » en tête ({[c['Id'] for c in cards][:3]})")
        if items and cards and cards[0]["Id"] == "PourToi":
            first = [c["Name"] for c in cards[0]["Cards"] if not c["External"]][:3]
            if first != items[:3]:
                problems.append(f"{name} : aperçu visuel, « Pour toi » commence par {first}, la liste servie par {items[:3]}")
        if "seerr" in EXPECT and name in WISHED and not any(c["External"] for r in cards for c in r["Cards"]):
            problems.append(f"{name} : aperçu visuel sans aucun titre hors bibliothèque (mode étendu)")
        out["accounts"][name] = acc
        hidden = {find(lib, t)["Name"] for t in p.get("hidden", [])} if "je" in EXPECT else set()
        everywhere = set(items) | set(acc["suggestions"]) | {x for v in acc["rangees"].values() for x in v}
        if hidden & everywhere:
            problems.append(f"{name} : titre masqué (Jellyfin Enhanced) proposé {sorted(hidden & everywhere)}")
        if "seerr" in EXPECT:
            ext = call("GET", "/RecoPlus/Extended", query={"lang": "fr"}, token=as_user(name))
            acc["hors_bibliotheque"] = [f"{e['Name']} ({e.get('Year') or '?'}){' — ' + e['Reason'] if e.get('Reason') else ''}{' · ' + e['Row'] if e.get('Row') else ''}"
                                        for e in ext.get("items", [])]
            acc["apercu_hors_bibliotheque"] = prev.get("extended") or []
            keys = {(e["MediaType"], e["TmdbId"]) for e in ext.get("items", [])}
            lib_tmdb = {(("tv" if i["Type"] == "Series" else "movie"), int((i.get("ProviderIds") or {}).get("Tmdb") or 0)) for i in lib.values()}
            if keys & lib_tmdb:
                problems.append(f"{name} : titres de la bibliothèque proposés comme hors bibliothèque {sorted(keys & lib_tmdb)}")
            if keys & set(WISHED.get(name, [])):
                problems.append(f"{name} : titre demandé ou en liste de suivi reproposé hors bibliothèque")
            if name != "nouveau" and not ext.get("items"):
                problems.append(f"{name} : aucun titre hors bibliothèque (mode étendu)")
            # « tu as aimé » seulement pour un titre vu ou aimé ; un titre demandé : « tu attends »
            for title, _, _ in p.get("requests", []) + p.get("watchlist", []):
                if any(f"Parce que tu as aimé {title}" in e for e in acc["hors_bibliotheque"]):
                    problems.append(f"{name} : « Parce que tu as aimé {title} » alors qu'il est seulement demandé")
            # saga : jamais un volet plus loin que le premier pas encore vu (Harry Potter 3 et 4 sont dans la bibliothèque)
            if name == "potter" and any(e.startswith("Harry Potter") for e in acc["hors_bibliotheque"]):
                problems.append(f"{name} : volet de Harry Potter hors bibliothèque proposé avant les tomes 3 et 4 {[e for e in acc['hors_bibliotheque'] if e.startswith('Harry Potter')]}")
            # sans goût connu : pas de pépite tirée au hasard dans tout TMDb
            if name == "nouveau" and any(re.search(r"— Pépite peu connue( ·|$)", e) for e in acc["hors_bibliotheque"]):
                problems.append(f"{name} : pépites tous genres proposées à un compte sans goût connu")
        if "hss" in EXPECT:
            acc["hss_pour_toi"] = hss_row(name, users[name], "RecoPlusPourToi")
            if not acc["hss_pour_toi"]:
                problems.append(f"{name} : rangée « Pour toi » vide dans Home Screen Sections")
            elif seen & set(acc["hss_pour_toi"]):
                problems.append(f"{name} : titres déjà vus dans la rangée HSS {sorted(seen & set(acc['hss_pour_toi']))}")
        # pourquoi chaque titre (fiche du titre) : les raisons servies au script, pour les premiers de « Pour toi »
        why = call("GET", "/RecoPlus/Extended", query={"lang": "fr"}, token=as_user(name)).get("reasons") or {}
        by_id = {i["Id"].replace("-", "").lower(): i["Name"] for i in lib.values()}
        named = {by_id.get(k, k): v for k, v in why.items()}
        acc["raisons"] = {t: named[t] for t in items[:12] if t in named}
        if name != "nouveau" and not why:
            problems.append(f"{name} : aucune raison donnée (fiche du titre)")
        if p.get("holdout") is not None:
            acc["mesure"] = measure(lib, name, uid, p, items)
        if not items:
            problems.append(f"{name} : aucune liste")
        bad = seen & (set(items) | set(acc["suggestions"]))
        if bad:
            problems.append(f"{name} : titres déjà vus proposés {sorted(bad)}")
        if name != "nouveau" and not any(v for k, v in acc["rangees"].items()):
            problems.append(f"{name} : toutes les rangées sont vides")
    # nouvelles sorties (TMDb via Seerr) : au moins un compte en reçoit
    if "seerr" in EXPECT:
        got = {n: a["apercu_cartes"].get("Nouvelles sorties", 0) + a["apercu_cartes"].get("Nouvelles sorties : films", 0)
               + a["apercu_cartes"].get("Nouvelles sorties : séries", 0) for n, a in out["accounts"].items()}
        out["nouvelles_sorties"] = got
        # où les titres de chaque liste se perdent : renvoyés par Seerr, gardés, bibliothèque, hors bibliothèque avec fiche ;
        # puis, par compte, proposables (pool) et placés dans une rangée
        out["listes"] = status["engine"].get("TmdbListCounts")
        for n in out["accounts"]:
            src = (call("GET", "/RecoPlus/Preview", query={"userId": users[n], "lang": "fr"}).get("sources") or {}).get("list")
            out["accounts"][n]["source_listes"] = src
        if not any(got.values()):
            problems.append(f"rangée « Nouvelles sorties » vide pour tous les comptes ({got})")
    return out


def after_report(lib, users):
    """Bouton « Demander » (au nom du compte), puis arrivée d'un titre demandé : jamais reproposé à celui qui l'a demandé."""
    out = {}
    name = "action"
    items = call("GET", "/RecoPlus/Extended", token=as_user(name)).get("items", [])
    if items:
        it = items[0]
        r = call("POST", "/RecoPlus/Request", {"MediaType": it["MediaType"], "TmdbId": it["TmdbId"]}, token=as_user(name))
        out["demande"] = f"{name} demande « {it['Name']} » : {r}"
        again = call("GET", "/RecoPlus/Extended", token=as_user(name)).get("items", [])
        if any(e["TmdbId"] == it["TmdbId"] and e["MediaType"] == it["MediaType"] for e in again):
            problems.append(f"{name} : titre demandé par le bouton encore proposé")
    arrived = arrive(lib)
    if arrived:
        recompute()
        for who, title in arrived.items():
            seen_in = {}
            for n, uid in users.items():
                if n not in PERSONAS:
                    continue
                prev = call("GET", "/RecoPlus/Preview", query={"userId": uid, "lang": "fr"})
                rows = {"Pour toi": [re.sub(r"^📌 ", "", x) for x in prev.get("items") or []], **(prev.get("rows") or {})}
                where = [k for k, v in rows.items() if title in [x.removesuffix(" (série)") for x in v]]
                if where:
                    seen_in[n] = where
            out["arrivee"] = f"« {title} » demandé par {who} et arrivé : proposé à {seen_in or 'personne'}"
            if who in seen_in:
                problems.append(f"{who} : « {title} », qu'il a demandé, lui est proposé dès son arrivée ({seen_in[who]})")
    return out


def user_page_check():
    """
    Page Reco+ de l'utilisateur et pays du compte : le navigateur signale fuseau et langues, le pays détecté en découle ;
    un choix du compte passe avant, un pays mal formé est refusé ; avec Plugin Pages, la page est dans son menu.
    """
    out = {}
    name = "action"
    tok = as_user(name)
    call("POST", "/RecoPlus/Client", {"TimeZone": "Europe/Brussels", "Languages": ["fr-BE", "fr"]}, token=tok)
    me = call("GET", "/RecoPlus/Me", query={"lang": "fr"}, token=tok)
    out["pays détecté"] = f"{me['region']['auto']} (indices : {', '.join(c['source'] + '=' + c['country'] for c in me['region']['clues'])})"
    if me["region"]["auto"] != "BE" or not me["region"]["detected"]:
        problems.append(f"{name} : pays détecté {me['region']} au lieu de la Belgique (fuseau Europe/Brussels)")
    call("POST", "/RecoPlus/Me", {"Region": "CA", "Extended": None}, token=tok)
    if call("GET", "/RecoPlus/Me", token=tok)["region"]["current"] != "CA":
        problems.append(f"{name} : le pays choisi dans sa page n'est pas retenu")
    try:
        call("POST", "/RecoPlus/Me", {"Region": "Belgique"}, token=tok)
        problems.append(f"{name} : pays mal formé accepté")
    except HttpError as e:
        if e.status != 400:
            problems.append(f"{name} : pays mal formé, réponse {e.status} au lieu de 400")
    call("POST", "/RecoPlus/Me", {"Region": None, "Extended": None}, token=tok)
    # un autre compte ne voit pas ce qu'un compte a signalé (identité du jeton)
    other = call("GET", "/RecoPlus/Me", token=as_user("potter"))
    if any(c["source"] == "timezone" for c in other["region"]["clues"]):
        problems.append("potter : voit le fuseau signalé par un autre compte")
    if "hss" in EXPECT:   # Plugin Pages installé avec Home Screen Sections
        def listed():
            pages = call("GET", "/PluginPages/User", token=tok) or {}
            return [p for p in pages.get("Items", []) if p.get("Id") == "Jellyfin.Plugin.RecoPlus"]
        try:
            page = wait("page Reco+ dans Plugin Pages", listed, timeout=180)[0]
            call("GET", page["Url"], token=tok)
            out["Plugin Pages"] = f"page « {page.get('DisplayText')} » ({page.get('Url')}) dans le menu"
        except (RuntimeError, HttpError) as e:
            problems.append(f"Plugin Pages : page Reco+ absente du menu des utilisateurs ({e})")
    return out


# Audace des découvertes : même courbe que la page (configPage.html, BOLD) : 0 prudente, 50 équilibrée, 100 aventureuse
BOLD = [("ExtendedMinVotes", 2000, 500, 100, 50, True), ("ExtendedMinRating", 7.8, 7.5, 7.0, 0.1, False),
        ("ExtendedGemGenres", 1, 3, 6, 1, False), ("ExtendedNoise", 0.1, 0.3, 0.6, 0.05, False)]


def bold_values(a: float) -> dict:
    import math
    out = {}
    for k, v0, v1, v2, step, log_ in BOLD:
        lo, hi, x = (v0, v1, a / 50) if a <= 50 else (v1, v2, (a - 50) / 50)
        v = math.exp(math.log(lo) + (math.log(hi) - math.log(lo)) * x) if log_ else lo + (hi - lo) * x
        v = round(math.floor(v / step + 0.5) * step, 3)   # arrondi comme Math.round
        out[k] = int(v) if isinstance(step, int) else v
    return out


def boldness_experiment():
    """Titres hors bibliothèque de chaque compte en prudente puis en aventureuse : votes TMDb, notes, sources, extraits."""
    pid = plugin_id("Reco+")
    out = {}
    for label, a in (("prudente", 0), ("aventureuse", 100), ("équilibrée", 50)):
        cfg = call("GET", f"/Plugins/{pid}/Configuration")
        cfg.update(bold_values(a), ExtendedBoldness=a)
        call("POST", f"/Plugins/{pid}/Configuration", cfg)
        if label == "équilibrée":
            break   # réglage par défaut remis pour la suite
        recompute()
        res = {}
        for name in PERSONAS:
            if name not in PASSWORDS:
                continue
            items = call("GET", "/RecoPlus/Extended", token=as_user(name)).get("items", [])
            votes = []
            for it in items[:40]:
                try:
                    d = eco.seerr("GET", f"/api/v1/{'tv' if it['MediaType'] == 'tv' else 'movie'}/{it['TmdbId']}")
                    votes.append(int(d.get("voteCount") or 0))
                except Exception:
                    pass
            votes.sort()
            res[name] = {
                "n": len(items),
                "votes_median": votes[len(votes) // 2] if votes else None,
                "rating_mean": round(sum(it.get("Vote") or 0 for it in items) / max(1, len(items)), 2),
                "titles": [f"{it['Name']} ({it.get('Year') or '?'}) — {it.get('Reason') or it.get('Because') or ''}" for it in items[:10]],
            }
        out[label] = res
        log(f"audace {label} : médiane des votes " + ", ".join(f"{n} {v['votes_median']}" for n, v in res.items()))
    return out


def index_check():
    """index.html servie par Jellyfin : balise du script Reco+, et revérifiée à chaque chargement (ticket #50)."""
    with urllib.request.urlopen(BASE + "/web/index.html", timeout=30) as r:
        page, cc = r.read().decode("utf-8", "replace"), r.headers.get("Cache-Control", "")
    if "RecoPlus/client.js" not in page:
        problems.append("index.html : balise du script Reco+ absente")
    if "no-cache" not in cc:
        problems.append(f"index.html : Cache-Control « {cc} » (no-cache attendu : sinon un navigateur garde l'ancien script)")
    # langues : chaque dictionnaire incorporé dans la DLL et servi (allemand, espagnol : complets, pas un repli anglais)
    langs = {}
    for lang, probe, want in (("de", "Jetzt neu berechnen", "Recompute now"), ("es", "Recalcular ahora", "Recompute now")):
        r = call("GET", "/RecoPlus/I18n", query={"lang": lang})
        d = r.get("dict") or {}
        langs[lang] = len(d)
        if r.get("lang") != lang or d.get("Recalculer maintenant") != probe:
            problems.append(f"langue {lang} : réponse {r.get('lang')}, « Recalculer maintenant » → {d.get('Recalculer maintenant')!r} (attendu {probe!r})")
    return {"script": "RecoPlus/client.js" in page, "cache": cc, "je": "JellyfinEnhanced/script" in page, "langues": langs}


def t_poster(kind: str) -> bool:
    """Affiches téléchargées pour cette sorte de bibliothèque (captures seulement)."""
    return kind in ("movies", "tvshows")


def shots_take():
    """Captures du README et du wiki (anglais puis français) ; dans l'artifact, dossier shots/."""
    import shots
    out = OUT / "shots"
    out.mkdir(exist_ok=True)
    info = call("GET", "/System/Info/Public")
    ids = {u["Name"]: u["Id"] for u in call("GET", "/Users")}
    pid = plugin_id("Reco+")
    made = []
    for lang, value in (("en", "English"), ("fr", "French")):
        cfg = call("GET", f"/Plugins/{pid}/Configuration")
        cfg["Language"] = value
        call("POST", f"/Plugins/{pid}/Configuration", cfg)
        time.sleep(2)
        made += shots.take(BASE, info.get("Id", ""), (ids["admin"], TOKEN, "admin", ADMIN_PASSWORD),
                           (ids["anime"], as_user("anime"), "anime", PASSWORDS["anime"]), lang, out, log)
    return made


def browser_check():
    """Accueil d'un compte dans un vrai navigateur, mode étendu actif puis coupé (ticket #50) ; captures dans l'artifact."""
    try:
        import browser
    except ImportError:
        return {"absent": "Playwright non installé"}
    name = "anime"
    info = call("GET", "/System/Info/Public")
    uid = {u["Name"]: u["Id"] for u in call("GET", "/Users")}[name]
    pid = plugin_id("Reco+")
    out = {}
    for label, ext in (("mode étendu", True), ("sans mode étendu", False), ("mode étendu remis", True)):
        cfg = call("GET", f"/Plugins/{pid}/Configuration")
        if cfg.get("ExtendedEnabled") != ext:
            cfg["ExtendedEnabled"] = ext
            call("POST", f"/Plugins/{pid}/Configuration", cfg)
            time.sleep(3)
        errors: list = []
        try:
            res = browser.home(BASE, info.get("Id", ""), uid, as_user(name), name, PASSWORDS[name],
                               OUT / f"accueil-{safe(label)}.png", errors)
        except Exception as e:
            res = {"sections": 0, "echec": str(e)[:300]}
        res["erreurs"] = errors[:15]
        out[label] = res
        log(f"navigateur, {label} : {res.get('sections')} sections, {res.get('reco')} rangées Reco+, {res.get('ext')} cartes hors bibliothèque, erreurs {len(errors)}")
    on, off = out["mode étendu"].get("sections", 0), out["sans mode étendu"].get("sections", 0)
    m = out["mode étendu"]
    if "seerr" in EXPECT and "je" in EXPECT and m.get("je_obj") and m.get("own", 0) > 0:
        problems.append(f"navigateur : Jellyfin Enhanced relié à Seerr, mais {m.get('own')} cartes de secours Reco+ (ses cartes attendues)")
    if off == 0:
        problems.append(f"navigateur : aucune section affichée même sans mode étendu ({out['sans mode étendu']})")
    elif on < off:
        problems.append(f"navigateur : {on} sections avec le mode étendu contre {off} sans (ticket #50)")
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
    for k, v in (r.get("seerr") or {}).items():
        L.append(f"Seerr, {k} : {v}  ")
    if r.get("nouvelles_sorties") is not None:
        L.append(f"Nouvelles sorties (cartes par compte) : {r.get('nouvelles_sorties')}  ")
        L.append(f"Listes TMDb (renvoyés, gardés, bibliothèque, hors bibliothèque) : {r['status']['engine'].get('TmdbListCounts')} — {r['status']['engine'].get('TmdbLists')}  ")
        L.append("Listes, par compte (trouvés, détaillés, proposés, placés) : " + "; ".join(f"{n} {a.get('source_listes')}" for n, a in r["accounts"].items()) + "  ")
    for k, v in (r.get("page utilisateur") or {}).items():
        L.append(f"Page de l'utilisateur, {k} : {v}  ")
    for label, res in (r.get("audace") or {}).items():
        L.append(f"**Audace {label}** : " + " ; ".join(f"{n} {v['n']} titres, votes médians {v['votes_median']}, note moyenne {v['rating_mean']}" for n, v in res.items()) + "  ")
        for n, v in res.items():
            L.append(f"  - {n} : " + " · ".join(v["titles"][:6]) + "  ")
    if r.get("index"):
        i = r["index"]
        L.append(f"index.html : script Reco+ {'présent' if i['script'] else 'absent'}, Jellyfin Enhanced {'présent' if i['je'] else 'absent'}, Cache-Control « {i['cache']} » ; textes servis : {i.get('langues')}  ")
    for k, v in (r.get("navigateur") or {}).items():
        if isinstance(v, dict):
            L.append(f"Navigateur, {k} : {v.get('sections')} sections ({v.get('reco')} Reco+), {v.get('ext')} cartes hors bibliothèque ({v.get('own')} de Reco+, Jellyfin Enhanced {'prêt' if v.get('je') else 'chargé sans Seerr' if v.get('je_obj') else 'balise sans script' if v.get('je_tag') else 'absent'}), "
                     f"script {'chargé' if v.get('script') else 'absent'}, {len(v.get('erreurs') or [])} erreur(s) JavaScript  ")
            L += [f"  - {e}  " for e in (v.get("erreurs") or [])[:5]]
        else:
            L.append(f"Navigateur : {v}  ")
    bench_rows = [(n, a["mesure"]) for n, a in r["accounts"].items() if a.get("mesure") and a["mesure"]["gardes"]]
    if bench_rows:
        def hit(m, key, k):
            ranks = [x for mm in m for x in mm[key]]
            return sum(1 for x in ranks if x is not None and x <= k) / max(1, len(ranks))
        def mrr(m, key):
            ranks = [x for mm in m for x in mm[key]]
            return sum(1 / x for x in ranks if x) / max(1, len(ranks))
        ms = [m for _, m in bench_rows]
        shares = [m["dans_ses_gouts"] for m in ms if m["dans_ses_gouts"] is not None]
        L += ["", "### Banc : mesure objective",
              f"Titres gardés de côté (les {len(ms[0]['gardes'])} derniers vraiment regardés de chaque compte), retrouvés parmi les 20 premiers : "
              f"**Reco+ {hit(ms, 'reco', 20):.0%}** contre popularité dans le foyer {hit(ms, 'popularite', 20):.0%} ; parmi les 10 premiers : "
              f"Reco+ {hit(ms, 'reco', 10):.0%} contre {hit(ms, 'popularite', 10):.0%} ; rang réciproque moyen {mrr(ms, 'reco'):.3f} contre {mrr(ms, 'popularite'):.3f}. "
              f"Début de « Pour toi » dans les goûts du compte : {sum(shares) / max(1, len(shares)):.0%} en moyenne.", "",
              "| Compte | Gardés | Rang Reco+ | Rang popularité | Pour toi dans ses goûts |", "|---|---|---|---|---|"]
        for n, m in bench_rows:
            L.append(f"| {n} | {', '.join(m['gardes'])} | {', '.join(str(x or '—') for x in m['reco'])} | {', '.join(str(x or '—') for x in m['popularite'])} | "
                     f"{'—' if m['dans_ses_gouts'] is None else format(m['dans_ses_gouts'], '.0%')} |")
    L.append("")
    L.append("### Problèmes" if problems else "### Aucun problème détecté")
    L += [f"- {p}" for p in problems]
    for name, a in r["accounts"].items():
        L += ["", f"### {name}", f"*Attendu : {a['attendu']}*", "", f"**Vu** : {', '.join(a['vu']) or '—'}", "",
              f"**Pour toi** : {', '.join(a['pour_toi'][:15])}", ""]
        for row, titles in a["rangees"].items():
            L.append(f"- **{row}** : {', '.join(titles[:10]) or '(vide)'}")
        if a.get("raisons"):
            L.append("- **Pourquoi (fiche du titre)** : " + "; ".join(f"{t} — {w}" for t, w in a["raisons"].items()))
        L.append(f"- **/Items/Suggestions** : {', '.join(a['suggestions'][:10])}")
        for c in a["recommandations_films"]:
            L.append(f"- **/Movies/Recommendations — parce que {c['parce_que']}** : {', '.join(c['titres'])}")
        if a.get("hors_bibliotheque") is not None:
            L.append(f"- **Hors bibliothèque (Seerr)** : {'; '.join(a['hors_bibliotheque'][:12]) or '(aucun)'}")
        if a.get("hss_pour_toi") is not None:
            L.append(f"- **Home Screen Sections, « Pour toi » vu par le compte** : {', '.join(a['hss_pour_toi'][:10]) or '(vide)'}")
        if a["plus_comme_ceci"]:
            L.append(f"- **Plus comme ceci ({a['plus_comme_ceci']['de']})** : {', '.join(a['plus_comme_ceci']['titres'])}")
    return "\n".join(L) + "\n"


def run():
    OUT.mkdir(parents=True, exist_ok=True)
    setup()
    lib = library()
    log(f"bibliothèque indexée : {len(lib)} titres")
    if os.environ.get("SIM_EXPORT_NFO") or not OFFLINE:   # pas encore de métadonnées figées : on les exporte (artifact)
        export_nfo(OUT / "nfo")
    users = {u["Name"]: u["Id"] for u in call("GET", "/Users")}
    simulate(lib, users)
    if "je" in EXPECT:
        setup_je(lib, users)
    if "hss" in EXPECT:
        setup_hss()
        USERS_IDS.update(users)
        try:
            hss_own_choice_check()
        except Exception as e:
            problems.append(f"choix propre d'un compte dans Home Screen Sections : {e}")
    if "seerr" in EXPECT:
        WISHED.update(setup_seerr(users))
        if "hss" in EXPECT:
            # Seerr relié : les rangées des listes TMDb existent maintenant ; l'assistant les ajoute au tableau de HSS
            r = call("POST", "/RecoPlus/HomeSections/Setup", {}, query={"lang": "fr"})
            log(f"Home Screen Sections, avec Seerr : {r.get('message')}")
    # Reco+ : vérifications de compatibilité 20 s après le démarrage, planificateur 60 s après
    wait("vérifications de Reco+", lambda: call("GET", "/RecoPlus/Status").get("checks"), timeout=300)
    recompute()
    r = report(lib, users)
    try:
        r["page utilisateur"] = user_page_check()
    except Exception as e:
        problems.append(f"page de l'utilisateur : {e}")
    try:
        r["index"] = index_check()
    except Exception as e:
        problems.append(f"index.html : {e}")
    if "seerr" in EXPECT:
        try:
            r["seerr"] = after_report(lib, users)
        except Exception as e:   # le rapport s'écrit quand même ; l'erreur est un problème du test
            problems.append(f"Seerr, après le rapport : {e}")
    if "seerr" in EXPECT and os.environ.get("SIM_BOLDNESS"):
        try:
            r["audace"] = boldness_experiment()
        except Exception as e:
            problems.append(f"expérience d'audace : {e}")
    if "hss" in EXPECT and os.environ.get("SIM_BROWSER"):
        r["navigateur"] = browser_check()
    if os.environ.get("SIM_SHOTS"):
        try:
            r["captures"] = shots_take()
        except Exception as e:
            problems.append(f"captures : {e}")
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
