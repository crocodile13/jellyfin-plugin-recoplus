"""Accueil vu dans un vrai navigateur (Chromium sans écran, Playwright) : ce que voit un compte, avec et sans le mode étendu.

Vérifie que les sections de l'accueil s'affichent (ticket #50 : plus aucune section avec le mode étendu), compte les
rangées Reco+ et les cartes hors bibliothèque, relève les erreurs JavaScript, et garde une capture d'écran de chaque état.
Le compte se connecte avec un jeton obtenu par l'API (jamais écrit sur disque ; masqué dans le journal de la CI).
"""
from __future__ import annotations

import json
import time

SECTIONS_JS = """() => {
  const secs = [...document.querySelectorAll('.sections .verticalSection, .homeSectionsContainer .verticalSection')]
    .filter(s => s.offsetParent !== null && s.querySelector('.card'));
  return {
    sections: secs.length,
    reco: secs.filter(s => [...s.classList].some(c => c.startsWith('RecoPlus'))).length,
    ext: document.querySelectorAll('.recoplus-ext').length,
    own: document.querySelectorAll('.recoplus-own').length,
    je: !!(window.JellyfinEnhanced && window.JellyfinEnhanced.jellyseerrUI),
    titles: secs.map(s => (s.querySelector('.sectionTitle') || {}).textContent || '').map(t => t.trim()).slice(0, 30),
  };
}"""


def _stable(page, timeout=45):
    """Attend que le nombre de sections et de cartes hors bibliothèque ne bouge plus (accueil fini de se composer)."""
    last, same, end = None, 0, time.time() + timeout
    while time.time() < end:
        cur = page.evaluate(SECTIONS_JS)
        key = (cur["sections"], cur["reco"], cur["ext"])
        same = same + 1 if key == last else 0
        last = key
        if same >= 4 and cur["sections"] > 0:
            return cur
        time.sleep(1.5)
    return page.evaluate(SECTIONS_JS)


def home(url: str, server_id: str, user_id: str, token: str, name: str, password: str, shot, errors: list) -> dict:
    """Ouvre l'accueil du compte et renvoie ce qui s'y affiche."""
    from playwright.sync_api import sync_playwright

    creds = {"Servers": [{"Id": server_id, "ManualAddress": url, "LocalAddress": url, "UserId": user_id,
                          "AccessToken": token, "LastConnectionMode": 2, "DateLastAccessed": int(time.time() * 1000)}]}
    with sync_playwright() as p:
        b = p.chromium.launch()
        ctx = b.new_context(viewport={"width": 1600, "height": 2400}, locale="fr-FR")
        ctx.add_init_script("localStorage.setItem('jellyfin_credentials', %s);" % json.dumps(json.dumps(creds)))
        page = ctx.new_page()
        page.on("pageerror", lambda e: errors.append(f"erreur JavaScript : {e}"))
        page.on("console", lambda m: errors.append(f"console : {m.text}"[:400]) if m.type == "error" and "Failed to load resource" not in m.text else None)
        page.on("response", lambda r: errors.append(f"HTTP {r.status} : {r.url.split('?')[0]}") if r.status >= 400 else None)
        page.goto(f"{url}/web/#/home", wait_until="domcontentloaded")
        try:
            page.wait_for_selector(".sections .verticalSection, .homeSectionsContainer .verticalSection, #txtManualName", timeout=60000)
        except Exception:
            pass
        login = False
        if page.query_selector("#txtManualName") is not None:   # jeton injecté non repris : le formulaire de connexion
            login = True
            if not page.is_visible("#txtManualName") and page.query_selector(".btnManual") is not None:
                page.click(".btnManual")                      # comptes en vignettes : connexion manuelle
            page.fill("#txtManualName", name)
            page.fill("#txtManualPassword", password)
            page.keyboard.press("Enter")
            page.wait_for_url("**/home**", timeout=60000)
            try:
                page.wait_for_selector(".sections .verticalSection, .homeSectionsContainer .verticalSection", timeout=60000)
            except Exception:
                pass
        res = _stable(page)
        res["login_form"] = login
        res["script"] = page.evaluate("() => !!window.__recoPlusClient")
        page.screenshot(path=str(shot), full_page=True)
        b.close()
    return res
