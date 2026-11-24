"""Captures pour le README et le wiki : accueil d'un compte et chaque onglet de la page de configuration, en anglais et
en français, sur le serveur du test d'intégration (bibliothèque de vrais titres, comptes simulés, vrai Seerr).

Les affiches sont floutées (choix des captures du README : pas d'illustration de tiers en clair). Les comptes sont
ceux du banc (noms génériques), le serveur est jetable : rien de réel n'apparaît. Les sessions s'ouvrent par jeton
injecté (jamais écrit sur disque).
"""
from __future__ import annotations

import json
import time

import browser

BLUR = """
.cardImageContainer, .cardImage, .cardImageIcon, .itemDetailImage, .backdropImage, .rp-pv-img img,
.cardScalable img, .jellyseerr-card img { filter: blur(9px) saturate(1.1) !important; }
"""

TABS = [("etat", "status"), ("general", "general"), ("accueil", "home"), ("listes", "lists"), ("moteur", "engine"),
        ("etendu", "extended"), ("calibrage", "calibration"), ("apercu", "preview")]


def _context(p, url, server_id, user_id, token, locale):
    creds = {"Servers": [{"Id": server_id, "ManualAddress": url, "LocalAddress": url, "UserId": user_id,
                          "AccessToken": token, "LastConnectionMode": 2, "DateLastAccessed": int(time.time() * 1000)}]}
    b = p.chromium.launch()
    ctx = b.new_context(viewport={"width": 1440, "height": 900}, device_scale_factor=2, locale=locale)
    ctx.add_init_script("localStorage.setItem('jellyfin_credentials', %s);" % json.dumps(json.dumps(creds)))
    ctx.add_init_script("document.addEventListener('DOMContentLoaded', () => { const s = document.createElement('style'); "
                        "s.textContent = %s; document.head.appendChild(s); });" % json.dumps(BLUR))
    return b, ctx


def take(url: str, server_id: str, admin: tuple, account: tuple, lang: str, out, log=print) -> list:
    """admin / account = (identifiant, jeton). Écrit les captures dans `out` ; renvoie leurs noms."""
    from playwright.sync_api import sync_playwright

    locale = "fr-FR" if lang == "fr" else "en-US"
    made = []
    with sync_playwright() as p:
        # accueil du compte : sections composées, cartes hors bibliothèque comprises
        b, ctx = _context(p, url, server_id, account[0], account[1], locale)
        page = ctx.new_page()
        page.goto(f"{url}/web/#/home", wait_until="domcontentloaded")
        page.wait_for_selector(".sections .verticalSection, .homeSectionsContainer .verticalSection", timeout=90000)
        browser._stable(page)
        time.sleep(4)                                            # affiches chargées
        name = f"home-{lang}.png"
        page.screenshot(path=str(out / name), full_page=False)
        made.append(name)
        name = f"home-full-{lang}.png"
        page.screenshot(path=str(out / name), full_page=True)
        made.append(name)
        b.close()

        # page de configuration, niveau avancé, chaque onglet
        b, ctx = _context(p, url, server_id, admin[0], admin[1], locale)
        page = ctx.new_page()
        page.goto(f"{url}/web/#/configurationpage?name=RecoPlus", wait_until="domcontentloaded")
        page.wait_for_selector("#RecoPlusConfigPage .rp-tab", timeout=90000)
        time.sleep(3)
        page.select_option("#rpLevel", "2")
        page.dispatch_event("#rpLevel", "change")
        for tab, label in TABS:
            page.click(f'.rp-tab[data-tab="{tab}"]')
            time.sleep(1.5)
            if tab == "apercu":
                try:
                    page.select_option("#rpPreviewUser", label=account[2])
                except Exception:
                    pass
                page.click("#rpPreviewBtn")
                page.wait_for_selector("#rpPreviewCards .rp-pv-card", timeout=60000)
                time.sleep(4)
            name = f"config-{label}-{lang}.png"
            el = page.query_selector("#RecoPlusConfigPage .content-primary") or page.query_selector("#RecoPlusConfigPage")
            el.screenshot(path=str(out / name))
            made.append(name)
        b.close()
    log(f"captures ({lang}) : {', '.join(made)}")
    return made
