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
        # cartes sans aucune image (Jellyfin n'a pas encore téléchargé les images de la bibliothèque : `defaultCardBackground`,
        # sans `data-src`, donc invisibles pour le contrôle des affiches floues ; constaté sur les captures de 1.1.2 et 1.1.3,
        # cases grises) : on recharge l'accueil tant qu'il en reste, puis on abandonne (l'ancienne capture reste en place)
        # chaque image de carte visible des rangées doit se charger vraiment : image de fond (cartes de Jellyfin et de Home
        # Screen Sections), `data-src` (chargement paresseux) ou balise img (cartes de Jellyfin Enhanced) ; sans aucune
        # adresse, ou adresse en échec (image pas encore téléchargée par Jellyfin), la carte est grise
        blank = """() => new Promise(done => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight && r.width > 40; };
          const cards = [...document.querySelectorAll('.verticalSection .card')].filter(vis);
          const urlOf = c => {
            const img = c.querySelector('img[src]');
            if (img && img.getAttribute('src')) { return img.src; }
            for (const e of c.querySelectorAll('.cardImageContainer, .cardImage, [data-src]')) {
              const d = e.getAttribute('data-src');
              if (d) { return d; }
              const m = /url\\(["']?([^"')]+)["']?\\)/.exec(getComputedStyle(e).backgroundImage || '');
              if (m) { return m[1]; }
            }
            return null;
          };
          let bad = 0;
          const one = c => new Promise(res => {
            const u = urlOf(c);
            if (!u) { bad++; res(); return; }
            const i = new Image();
            i.onload = () => { if (!i.naturalWidth) { bad++; } res(); };
            i.onerror = () => { bad++; res(); };
            i.src = u;
          });
          const t = setTimeout(() => done(bad + 1000), 30000);
          Promise.all(cards.map(one)).then(() => { clearTimeout(t); done(bad); });
        })"""
        for attempt in range(8):
            empty = page.evaluate(blank)
            if empty == 0:
                break
            log(f"accueil ({lang}) : {empty} carte(s) sans image, nouvel essai dans 20 s")
            time.sleep(20)
            page.reload(wait_until="domcontentloaded")
            page.wait_for_selector(".sections .verticalSection, .homeSectionsContainer .verticalSection", state="attached", timeout=90000)
            time.sleep(6)
        else:
            page.screenshot(path=str(out / f"echec-home-{lang}.jpg"), type="jpeg", quality=70)
            b.close()
            raise RuntimeError(f"home-{lang}: cards without image")
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
        # Jellyfin montre un aperçu flou (blurhash) tant qu'une affiche n'est pas chargée, et son chargeur paresseux ne
        # se déclenche pas dans ce navigateur sans écran (constaté : 23 affiches jamais demandées en 60 s). La capture les
        # charge donc elle-même, comme le chargeur : image téléchargée, posée en fond, aperçu flou masqué. Les rangées
        # peuvent être redessinées après coup (constaté en français : 23/23 chargées puis capture floue) : on recommence
        # jusqu'à ce qu'aucune carte visible des rangées n'attende encore son image (`data-src` ; `lazy-hidden` seul ne dit
        # rien : 15 éléments hors affiches la gardent), et on vérifie juste avant la capture.
        load = """() => new Promise(done => {
          const cards = [...document.querySelectorAll('[data-src]')]
            .filter(e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight && r.width > 0; });
          let ok = 0, bad = 0;
          const one = e => new Promise(res => {
            const url = e.getAttribute('data-src'), img = new Image();
            img.onload = () => {
              e.style.backgroundImage = "url('" + url + "')";
              e.removeAttribute('data-src');
              e.classList.remove('lazy-hidden');
              (e.parentElement || e).querySelectorAll('.blurhash-canvas').forEach(c => { c.style.display = 'none'; });
              ok++; res();
            };
            img.onerror = () => { bad++; res(); };
            img.src = url;
          });
          const t = setTimeout(() => done({ ok, bad, total: cards.length }), 30000);
          Promise.all(cards.map(one)).then(() => { clearTimeout(t); done({ ok, bad, total: cards.length }); });
        })"""
        pending = """() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight && r.width > 0
            && getComputedStyle(e).display !== 'none'; };
          return [...document.querySelectorAll('.verticalSection [data-src], .verticalSection .blurhash-canvas')].filter(vis).length;
        }"""
        # le fondu de Jellyfin laisse l'aperçu flou par-dessus l'affiche déjà posée pendant un moment (constaté : capture
        # floue avec toutes les images en place) : sans animation, aperçus flous masqués, affiches visibles d'emblée
        page.add_style_tag(content="*, *::before, *::after { transition: none !important; animation: none !important; } "
                                   ".blurhash-canvas { display: none !important; } .lazy-hidden { opacity: 1 !important; }")
        left = -1
        for attempt in range(6):
            loaded = page.evaluate(load)
            time.sleep(2)
            left = page.evaluate(pending)
            log(f"accueil ({lang}) : passe {attempt + 1}, affiches chargées {loaded['ok']}/{loaded['total']}, "
                f"en échec {loaded['bad']}, encore floues {left}")
            if left == 0:
                break
        if left != 0:
            # pas d'image floue : l'ancienne capture reste en place
            log(f"accueil ({lang}) : {left} affiche(s) encore floue(s), capture abandonnée")
            page.screenshot(path=str(out / f"echec-home-{lang}.jpg"), type="jpeg", quality=70)
            b.close()
            raise RuntimeError(f"home-{lang}: posters still blurred")
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
        # le menu du niveau a les couleurs des autres menus de la page (thème de Jellyfin), liste ouverte comprise
        colors = page.evaluate("""() => {
          const c = (s) => { const e = document.querySelector(s); if (!e) return null; const st = getComputedStyle(e); return [st.color, st.backgroundColor]; };
          return { level: c('#rpLevel'), levelOption: c('#rpLevel option'), other: c('#rpCalMethod'), otherOption: c('#rpCalMethod option') };
        }""")
        log(f"couleurs des menus ({lang}) : {colors}")
        # variables du thème de Jellyfin lues par la page (accent, fond des bulles) : vides = repli de Reco+
        theme = page.evaluate("""() => { const r = getComputedStyle(document.documentElement);
          return ['--jf-palette-primary-main', '--jf-palette-background-paper', '--jf-palette-text-primary'].map((v) => v + '=' + (r.getPropertyValue(v).trim() || '∅')); }""")
        log(f"variables du thème ({lang}) : {theme}")
        if colors["levelOption"] != colors["otherOption"] or colors["level"] != colors["other"]:
            raise RuntimeError(f"menu du niveau pas aux couleurs des autres menus : {colors}")
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
