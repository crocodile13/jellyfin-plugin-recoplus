"""Captures pour le README et le wiki : accueil d'un compte et chaque onglet de la page de configuration, en anglais et
en français, sur le serveur du test d'intégration (bibliothèque de vrais titres, comptes simulés, vrai Seerr).

Les comptes et leurs historiques sont simulés (noms génériques), le serveur est jetable : rien de réel ni de privé
n'apparaît, les affiches restent nettes. Les sessions s'ouvrent par jeton
injecté (jamais écrit sur disque).
"""
from __future__ import annotations

import json
import time

import browser

TABS = [("etat", "status"), ("general", "general"), ("accueil", "home"), ("listes", "lists"), ("moteur", "engine"),
        ("etendu", "extended"), ("calibrage", "calibration"), ("apercu", "preview")]


def _context(p, url, server_id, user_id, token, locale):
    creds = {"Servers": [{"Id": server_id, "ManualAddress": url, "LocalAddress": url, "UserId": user_id,
                          "AccessToken": token, "LastConnectionMode": 2, "DateLastAccessed": int(time.time() * 1000)}]}
    b = p.chromium.launch()
    ctx = b.new_context(viewport={"width": 1440, "height": 900}, device_scale_factor=1, locale=locale)
    ctx.add_init_script("localStorage.setItem('jellyfin_credentials', %s);" % json.dumps(json.dumps(creds)))
    return b, ctx


def _open(page, url, path, name, password, ready, out, tag):
    """Ouvre une page ; si Jellyfin montre le formulaire de connexion (session injectée non reprise), se connecte."""
    page.goto(f"{url}/web/#/{path}", wait_until="domcontentloaded")
    try:
        # « attached » : la première section trouvée peut être une zone vide cachée ; _stable compte ensuite les visibles
        page.wait_for_selector(ready + ", #txtManualName", state="attached", timeout=90000)
        if page.query_selector("#txtManualName") is not None:
            if not page.is_visible("#txtManualName") and page.query_selector(".btnManual") is not None:
                page.click(".btnManual")
            page.fill("#txtManualName", name)
            page.fill("#txtManualPassword", password)
            page.keyboard.press("Enter")
            time.sleep(4)
            page.goto(f"{url}/web/#/{path}", wait_until="domcontentloaded")
            page.wait_for_selector(ready, state="attached", timeout=90000)
    except Exception:
        page.screenshot(path=str(out / f"echec-{tag}.jpg"), quality=70)   # pour voir ce qui bloque
        raise


def take(url: str, server_id: str, admin: tuple, account: tuple, lang: str, out, log=print) -> list:
    """admin / account = (identifiant, jeton, nom, mot de passe). Écrit les captures dans `out` ; renvoie leurs noms."""
    from playwright.sync_api import sync_playwright

    locale = "fr-FR" if lang == "fr" else "en-US"
    made = []
    with sync_playwright() as p:
        # accueil du compte : sections composées, cartes hors bibliothèque comprises
        b, ctx = _context(p, url, server_id, account[0], account[1], locale)
        page = ctx.new_page()
        _open(page, url, "home", account[2], account[3], ".sections .verticalSection, .homeSectionsContainer .verticalSection", out, f"home-{lang}")
        browser._stable(page)
        time.sleep(4)                                            # affiches chargées
        # cadrée à partir de la première rangée remplie (au-dessus : l'en-tête et l'emplacement vide d'un bandeau), sur
        # la largeur de l'écran (les rangées débordent) ; JPEG : surtout des affiches, peu de texte fin
        # après l'agrandissement : la zone vide au-dessus des rangées prend une hauteur proportionnelle à la fenêtre
        page.set_viewport_size({"width": 1440, "height": 2000})
        time.sleep(3)
        top = page.evaluate("""() => {
          const s = [...document.querySelectorAll('.sections .verticalSection, .homeSectionsContainer .verticalSection')]
            .find(x => x.offsetParent !== null && x.querySelector('.card'));
          return s ? s.getBoundingClientRect().top + window.scrollY : 0;
        }""")
        # Jellyfin montre un aperçu flou (blurhash) tant que l'affiche n'est pas chargée : attendre les vraies affiches
        # des rangées visibles (au plus 45 s)
        end = time.time() + 45
        while time.time() < end:
            pending = page.evaluate("""() => [...document.querySelectorAll('.cardImageContainer')]
              .filter(e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight && r.width > 0; })
              .filter(e => getComputedStyle(e).backgroundImage === 'none' && !e.querySelector('img')).length""")
            if pending == 0:
                break
            time.sleep(1.5)
        time.sleep(1)
        name = f"home-{lang}.jpg"
        page.screenshot(path=str(out / name), type="jpeg", quality=82, full_page=True,
                        clip={"x": 0, "y": max(0, top - 24), "width": 1440, "height": 1150})
        made.append(name)
        b.close()

        # page de configuration, niveau avancé, chaque onglet
        b, ctx = _context(p, url, server_id, admin[0], admin[1], locale)
        page = ctx.new_page()
        _open(page, url, "configurationpage?name=RecoPlus", admin[2], admin[3], "#RecoPlusConfigPage .rp-tab", out, f"config-{lang}")
        time.sleep(3)
        # la barre de Jellyfin recouvrirait le haut de la page dans une capture d'élément
        page.add_style_tag(content=".skinHeader { display: none !important; } .mainAnimatedPage { padding-top: 0 !important; }")
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
            # page entière découpée à la position de la page Reco+ : la barre fixe de Jellyfin reste au-dessus, hors cadre
            page.evaluate("window.scrollTo(0, 0)")
            box = page.evaluate("""() => {
              const e = document.querySelector('#RecoPlusConfigPage .content-primary') || document.querySelector('#RecoPlusConfigPage');
              const r = e.getBoundingClientRect();
              return {x: r.left + window.scrollX, y: r.top + window.scrollY, width: r.width, height: r.height};
            }""")
            page.screenshot(path=str(out / name), full_page=True, clip=box)
            made.append(name)
        b.close()
    log(f"captures ({lang}) : {', '.join(made)}")
    return made
