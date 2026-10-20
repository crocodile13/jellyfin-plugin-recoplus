// Tests du script client (mode étendu) dans un DOM simulé : node tests/client/client.test.mjs (jsdom requis).
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const here = dirname(fileURLToPath(import.meta.url));
const SCRIPT = readFileSync(join(here, '../../src/Jellyfin.Plugin.RecoPlus/Web/client.js'), 'utf8');
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;

async function test(name, fn) {
    try { await fn(); console.log('ok   ' + name); } catch (e) { failures++; console.log('ÉCHEC ' + name + '\n     ' + (e.stack || e)); }
}

function ext(n) {
    return Array.from({ length: n }, (_, i) => ({ MediaType: i % 3 === 2 ? 'tv' : 'movie', TmdbId: 1000 + i, Name: 'Ext ' + i, Year: 2020, PosterPath: '/p' + i + '.jpg', Vote: 7.1, Because: 'Aimé ' + i }));
}

// Page d'accueil : une section « Reprendre », puis éventuellement la section HSS « Pour toi » avec `lib` cartes.
// jeHttp : statut HTTP renvoyé par le proxy Seerr de Jellyfin Enhanced pour une fiche (200 par défaut)
function page({ hssCards = 0, data, je = null, io = null, jeWait = 0, shape = 'Portrait', jeHttp = {} }) {
    const dom = new JSDOM('<!doctype html><html><head></head><body><div id="indexPage"><div id="homeTab" class="tabContent" data-index="0"><div class="sections"></div></div></div></body></html>', { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const calls = { getJSON: 0, posts: [], je: [] };
    w.ApiClient = {
        getCurrentUserId: () => 'u1',
        getUrl: (p) => '/' + p,
        getJSON: (url) => {
            const m = /JellyfinEnhanced\/jellyseerr\/(tv|movie)\/(\d+)$/.exec(url);
            if (m) { calls.je.push(url); const st = jeHttp[m[2]] || 200; return st === 200 ? Promise.resolve({}) : Promise.reject({ status: st }); }
            calls.getJSON++;
            return Promise.resolve(JSON.parse(JSON.stringify(data)));
        },
        ajax: (o) => { calls.posts.push(o); return Promise.resolve({}); },
    };
    if (je) { w.JellyfinEnhanced = je(w); }
    if (io) { w.IntersectionObserver = io; }
    w.__recoPlusJeWaitMs = jeWait;
    const sections = w.document.querySelector('.sections');
    sections.innerHTML = '<div class="verticalSection ContinueWatchingNextUp section0"><div class="itemsContainer"><div class="card">r</div></div></div>';
    const fill = (n) => {
        let s = sections.querySelector('.RecoPlusPourToi');
        if (!s) {
            s = w.document.createElement('div');
            s.className = 'verticalSection RecoPlusPourToi section1';
            s.innerHTML = '<div is="emby-scroller"><div is="emby-itemscontainer" class="itemsContainer scrollSlider"></div></div>';
            sections.appendChild(s);
        }
        s.querySelector('.itemsContainer').innerHTML = Array.from({ length: n }, (_, i) => `<div class="card overflow${shape}Card" data-lib="${i}" data-id="${String(i).padStart(32, 'a')}">L${i}</div>`).join('');
    };
    if (hssCards) { fill(hssCards); }
    w.eval(SCRIPT);
    const row = () => Array.from(sections.querySelectorAll('.RecoPlusPourToi .itemsContainer > *')).map((c) => (c.classList.contains('recoplus-ext') ? 'E:' + (c.getAttribute('data-rp-id') || '').split(':').pop() : 'L'));
    return { w, sections, calls, fill, row };
}

// fail : 'null' (fiche introuvable, comme JE après un 403) ou 'reject' (erreur remontée), pour chaque titre listé
function jeStub({ active = true, userFound = true, status = {}, fail = { 1003: 'null' } } = {}) {
    const answer = (id, d) => (fail[id] === 'reject' ? Promise.reject(new Error('HTTP 403')) : Promise.resolve(fail[id] === 'null' ? null : d));
    return (w) => ({
        jellyseerrAPI: {
            checkUserStatus: () => Promise.resolve({ active, userFound }),
            fetchMovieDetails: (id) => answer(id, { id, title: 'M' + id, backdropPath: '/b' + id + '.jpg', mediaInfo: status[id] ? { status: status[id] } : undefined }),
            fetchTvShowDetails: (id) => answer(id, { id, name: 'T' + id, backdropPath: '/b' + id + '.jpg', mediaInfo: status[id] ? { status: status[id] } : undefined }),
        },
        jellyseerrUI: {
            created: [],
            createJellyseerrCard(item, a, u) {
                this.created.push({ item, a, u });
                const c = w.document.createElement('div');
                c.className = 'card overflowPortraitCard jellyseerr-card';
                c.dataset.tmdb = String(item.id);
                c.innerHTML = '<div class="cardBox"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait"></div><div class="cardImageContainer" data-je-poster="https://image.tmdb.org/t/p/w400/p.jpg"></div></div></div>';
                return c;
            },
        },
    });
}

const base = { enabled: true, homeRow: true, hss: true, mixEvery: 3, skip: 0, canRequest: true, title: 'À découvrir', seerrUrl: 'https://seerr.example', items: ext(8) };

await test('positions : un hors bibliothèque toutes les 3 cartes, après les épinglés', async () => {
    const { w } = page({ hssCards: 0, data: { ...base, homeRow: false } });
    const slots = (...a) => Array.from(w.__recoPlusSlots(...a)); // tableau de la fenêtre jsdom → tableau Node
    assert.deepEqual(slots(10, 99, 3, 0), [1, 3, 5, 7, 9]);
    assert.deepEqual(slots(10, 2, 3, 0), [1, 3]);
    assert.deepEqual(slots(10, 99, 3, 2), [3, 5, 7, 9]);
    assert.deepEqual(slots(6, 99, 2, 0), [0, 1, 2, 3, 4, 5]);
    assert.deepEqual(slots(6, 99, 1, 0), [0, 1, 2, 3, 4, 5], 'every < 2 ramené à 2');
    assert.deepEqual(slots(0, 5, 3, 0), []);
    assert.deepEqual(slots(3, 5, 3, 9), [], 'skip au-delà de la rangée');
});

await test('HSS + Jellyfin Enhanced : cartes JE mélangées, titres demandés/disponibles écartés', async () => {
    const p = page({ hssCards: 10, data: base, je: jeStub({ status: { 1001: 2, 1004: 5 } }) });
    await wait(700);
    // 1001 (en attente) et 1004 (disponible) écartés ; 1003 sans fiche JE gardé (carte Reco+) → 1000, 1002, 1003, 1005, 1006
    assert.deepEqual(p.row(), ['L', 'L', 'E:1000', 'L', 'L', 'E:1002', 'L', 'L', 'E:1003', 'L', 'L', 'E:1005', 'L', 'L', 'E:1006']);
    const miss = p.sections.querySelector('[data-rp-id="tmdb:movie:1003"]');
    assert.ok(miss.classList.contains('recoplus-jemiss') && !miss.classList.contains('recoplus-own'));
    assert.ok(miss.querySelector('button'), 'bouton Demander de Reco+');
    const created = p.w.JellyfinEnhanced.jellyseerrUI.created;
    assert.equal(created[0].item.mediaType, 'movie');
    assert.equal(created.find((c) => c.item.id === 1002).item.mediaType, 'tv');
    assert.equal(created[0].a, true);
    assert.equal(created[0].u, true);
});

await test('aucun doublon quand la page change, réinsertion quand HSS recharge la rangée', async () => {
    const p = page({ hssCards: 6, data: base, je: jeStub() });
    await wait(700);
    const n1 = p.sections.querySelectorAll('.recoplus-ext').length;
    assert.equal(n1, 3);
    p.sections.appendChild(p.w.document.createElement('div'));
    await wait(700);
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, n1);
    p.fill(6); // HSS réécrit la rangée
    await wait(700);
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, 3);
    assert.equal(p.calls.getJSON, 1, 'réponse serveur gardée en cache');
});

await test('mélange libre : chaque titre à la place du classement commun, rien au-delà de la rangée', async () => {
    const items = ext(5).map((it, i) => ({ ...it, After: [0, 3, 3, 9, -1][i] }));
    const p = page({ hssCards: 6, data: { ...base, mixMode: 'score', skip: 1, items }, je: jeStub() });
    await wait(700);
    // 1 demande en tête, puis : 1000 devant tout, 1001 et 1002 (dans cet ordre) après 3 titres, 1003 (après 9) et 1004 (non calculé) absents
    assert.deepEqual(p.row(), ['L', 'E:1000', 'L', 'L', 'L', 'E:1001', 'E:1002', 'L', 'L']);
    p.sections.appendChild(p.w.document.createElement('div'));
    await wait(700);
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, 3, 'pas de doublon');
});

await test('rien ne trouve sa place : la rangée n\'est plus retraitée à chaque changement de page', async () => {
    const items = ext(2).map((it) => ({ ...it, After: 50 }));
    const p = page({ hssCards: 4, data: { ...base, mixMode: 'score', items }, je: jeStub() });
    await wait(700);
    const n = p.w.JellyfinEnhanced.jellyseerrUI.created.length;
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, 0);
    p.sections.appendChild(p.w.document.createElement('div'));
    await wait(700);
    assert.equal(p.w.JellyfinEnhanced.jellyseerrUI.created.length, n, 'cartes non reconstruites');
    p.fill(4); // nouveau rendu HSS → nouvel essai
    await wait(700);
    assert.ok(p.w.JellyfinEnhanced.jellyseerrUI.created.length > n);
});

await test('demandes arrivées en tête : jamais précédées d\'un titre hors bibliothèque', async () => {
    const p = page({ hssCards: 6, data: { ...base, skip: 2 }, je: jeStub() });
    await wait(700);
    assert.deepEqual(p.row().slice(0, 4), ['L', 'L', 'L', 'L']);
    assert.equal(p.row()[4], 'E:1000');
});

await test('sans Jellyfin Enhanced : cartes Reco+ avec bouton Demander', async () => {
    const p = page({ hssCards: 4, data: base });
    await wait(700);
    const cards = p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-ext');
    assert.equal(cards.length, 2);
    const btn = cards[0].querySelector('button');
    assert.equal(btn.textContent, 'Demander');
    btn.click();
    await wait(50);
    assert.equal(p.calls.posts.length, 1);
    assert.deepEqual(JSON.parse(p.calls.posts[0].data), { MediaType: 'movie', TmdbId: 1000 });
    assert.equal(btn.textContent, '✓ demandé');
});

await test('Jellyfin Enhanced non relié à Seerr : cartes Reco+', async () => {
    const p = page({ hssCards: 4, data: base, je: jeStub({ active: false }) });
    await wait(700);
    const cards = p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-ext');
    assert.equal(cards.length, 2);
    assert.ok(cards[0].querySelector('button'));
    assert.equal(p.w.JellyfinEnhanced.jellyseerrUI.created.length, 0);
});

await test('sans Home Screen Sections : une seule rangée à part, sous la première', async () => {
    const p = page({ data: { ...base, hss: false } });
    await wait(700);
    p.sections.appendChild(p.w.document.createElement('div'));
    await wait(700);
    const rows = p.sections.querySelectorAll('#recoplus-extended-row');
    assert.equal(rows.length, 1);
    assert.equal(p.sections.children[1].id, 'recoplus-extended-row');
    assert.equal(rows[0].querySelectorAll('.recoplus-ext').length, 8);
    assert.match(rows[0].querySelector('.itemsContainer').style.cssText, /scrollbar-width: none/);
});

await test('option « Sur l\'accueil » coupée : rien n\'est ajouté', async () => {
    const p = page({ hssCards: 6, data: { ...base, homeRow: false }, je: jeStub() });
    await wait(700);
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, 0);
});

await test('affiche refusée si le chemin n\'est pas un chemin TMDb', async () => {
    const items = [{ ...ext(1)[0], PosterPath: '/x.jpg") ; background:url(https://evil' }];
    const p = page({ hssCards: 3, data: { ...base, items } });
    await wait(700);
    const img = p.sections.querySelector('.recoplus-ext .cardImageContainer');
    assert.equal(img.style.backgroundImage, '');
});

// IntersectionObserver simulé : les tests décident de ce qui est visible
function ioStub() {
    const all = [];
    class IO {
        constructor(cb) { this.cb = cb; this.els = []; all.push(this); }
        observe(e) { this.els.push(e); }
        unobserve() {}
        disconnect() {}
    }
    IO.show = (el, ratio = 1) => all.forEach((o) => { if (o.els.includes(el)) { o.cb([{ target: el, isIntersecting: ratio > 0, intersectionRatio: ratio }]); } });
    IO.watched = () => all.flatMap((o) => o.els);
    return IO;
}

await test('rangées « Parce que tu as aimé … » renommées pour le compte', async () => {
    const p = page({ data: { ...base, homeRow: false, rowTitles: { RecoPlusParceQue1: 'Parce que tu as aimé Dune', 'bad id': 'x' } } });
    const s = p.w.document.createElement('div');
    s.className = 'verticalSection RecoPlusParceQue1 section2';
    s.innerHTML = '<div class="sectionTitleContainer"><h2 class="sectionTitle sectionTitle-cards">Parce que tu as aimé…</h2></div>';
    p.sections.appendChild(s);
    await wait(700);
    assert.equal(s.querySelector('.sectionTitle').textContent, 'Parce que tu as aimé Dune');
    assert.equal(s.querySelector('.sectionTitle').children.length, 0, 'texte seul, aucun HTML');
});

await test('affichages : carte visible au moins 1 s dans une rangée Reco+, une fois par jour ; ouverte = remise à zéro', async () => {
    const IO = ioStub();
    const p = page({ hssCards: 3, data: { ...base, homeRow: false, track: true }, io: IO });
    await wait(700);
    const other = p.sections.querySelector('.ContinueWatchingNextUp .card');
    const cards = Array.from(p.sections.querySelectorAll('.RecoPlusPourToi .card'));
    assert.equal(IO.watched().length, 3, 'seules les cartes Reco+ sont observées');
    assert.ok(!IO.watched().includes(other));
    IO.show(cards[0]);
    IO.show(cards[1]);
    await wait(300);
    IO.show(cards[1], 0);          // sortie de l'écran avant 1 s : pas vue
    await wait(900);
    IO.show(cards[0], 0);
    IO.show(cards[0]);             // revue le même jour : pas renvoyée
    await wait(1100);
    cards[2].dispatchEvent(new p.w.MouseEvent('click', { bubbles: true }));
    await wait(20);
    const posts = p.calls.posts.filter((o) => o.url === '/RecoPlus/Impressions');
    assert.equal(posts.length, 1);
    assert.deepEqual(JSON.parse(posts[0].data), { Shown: ['a'.repeat(31) + '0'], Opened: ['a'.repeat(31) + '2'] });
});

await test('affichages coupés : rien n\'est observé ni envoyé', async () => {
    const IO = ioStub();
    const p = page({ hssCards: 3, data: { ...base, homeRow: false, track: false }, io: IO });
    await wait(700);
    assert.equal(IO.watched().length, 0);
    p.sections.querySelector('.RecoPlusPourToi .card').dispatchEvent(new p.w.MouseEvent('click', { bubbles: true }));
    await wait(20);
    assert.equal(p.calls.posts.length, 0);
});

await test('identifiants de carte : Jellyfin (avec ou sans tirets) et TMDb seulement', async () => {
    const p = page({ data: { ...base, homeRow: false } });
    const mk = (attrs) => { const c = p.w.document.createElement('div'); Object.entries(attrs).forEach(([k, v]) => c.setAttribute(k, v)); return c; };
    assert.equal(p.w.__recoPlusCardId(mk({ 'data-id': '0123456789ABCDEF0123456789abcdef' })), '0123456789abcdef0123456789abcdef');
    assert.equal(p.w.__recoPlusCardId(mk({ 'data-id': '01234567-89ab-cdef-0123-456789abcdef' })), '0123456789abcdef0123456789abcdef');
    assert.equal(p.w.__recoPlusCardId(mk({ 'data-rp-id': 'tmdb:tv:42' })), 'tmdb:tv:42');
    assert.equal(p.w.__recoPlusCardId(mk({ 'data-id': '../../x' })), null);
});

await test('rangée en paysage (HSS) : cartes Jellyfin Enhanced au même format, avec l\'image de fond', async () => {
    const p = page({ hssCards: 6, data: base, je: jeStub(), shape: 'Backdrop' });
    await wait(700);
    const ext = [...p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-ext')];
    assert.ok(ext.length > 0);
    ext.forEach((c) => {
        assert.ok(c.classList.contains('overflowBackdropCard') && !c.classList.contains('overflowPortraitCard'));
        assert.ok(c.querySelector('.cardPadder-overflowBackdrop'));
        assert.match(c.querySelector('.cardImageContainer').dataset.jePoster, /^https:\/\/image\.tmdb\.org\/t\/p\/w780\/b\d+\.jpg$/);
    });
});

await test('rangée en paysage sans Jellyfin Enhanced : cartes Reco+ au format de la rangée', async () => {
    const items = ext(2).map((x, i) => ({ ...x, BackdropPath: i === 0 ? '/bd.jpg' : null }));
    const p = page({ hssCards: 6, data: { ...base, items }, shape: 'Backdrop' });
    await wait(700);
    const c = [...p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-ext')];
    assert.equal(c.length, 2);
    assert.ok(c.every((x) => x.classList.contains('overflowBackdropCard')));
    assert.match(c[0].querySelector('.cardImageContainer').style.backgroundImage, /w780\/bd\.jpg/);
    assert.equal(c[1].querySelector('.cardImageContainer').style.backgroundSize, 'contain', 'sans image de fond : affiche entière');
});

await test('Jellyfin Enhanced chargé après coup : ses cartes remplacent celles de Reco+', async () => {
    const p = page({ hssCards: 6, data: base, jeWait: 300 });
    await wait(1000);
    assert.ok(p.sections.querySelector('.RecoPlusPourToi .recoplus-own'), 'd\'abord les cartes de secours');
    p.w.JellyfinEnhanced = jeStub()(p.w);
    p.sections.appendChild(p.w.document.createElement('div'));   // l'accueil bouge : nouvelle vérification
    await wait(1200);
    assert.equal(p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-own').length, 0);
    assert.ok(p.sections.querySelectorAll('.RecoPlusPourToi .jellyseerr-card').length > 0);
});

await test('Jellyfin Enhanced attendu quelques instants avant de se rabattre sur les cartes Reco+', async () => {
    const p = page({ hssCards: 6, data: base, jeWait: 2000 });
    setTimeout(() => { p.w.JellyfinEnhanced = jeStub()(p.w); }, 500);
    await wait(1500);
    assert.equal(p.sections.querySelectorAll('.RecoPlusPourToi .recoplus-own').length, 0);
    assert.ok(p.sections.querySelectorAll('.RecoPlusPourToi .jellyseerr-card').length > 0);
});

await test('Jellyfin Enhanced refuse toutes les fiches Seerr (403) : cartes Reco+ à leur place, sans boucle', async () => {
    const fail = Object.fromEntries(ext(8).map((x, i) => [x.TmdbId, i % 2 ? 'reject' : 'null']));
    const items = ext(8).map((it, i) => ({ ...it, After: i < 2 ? 1 : 40 }));
    const p = page({ hssCards: 6, data: { ...base, mixMode: 'score', items }, je: jeStub({ fail }) });
    await wait(700);
    assert.deepEqual(p.row(), ['L', 'E:1000', 'E:1001', 'L', 'L', 'L', 'L', 'L']);
    assert.equal(p.w.JellyfinEnhanced.jellyseerrUI.created.length, 0);
    const before = p.sections.querySelector('.recoplus-ext');
    p.sections.appendChild(p.w.document.createElement('div'));
    await wait(700);
    assert.equal(p.sections.querySelector('.recoplus-ext'), before, 'cartes gardées : pas de remplacement en boucle');
    assert.equal(p.sections.querySelectorAll('.recoplus-ext').length, 2);
});

await test('fiche refusée au compte par Jellyfin Enhanced (403, contrôle parental) : titre écarté, pas de carte Reco+', async () => {
    const fail = { 1000: 'null', 1001: 'null', 1002: 'reject' };
    const p = page({ hssCards: 10, data: { ...base, items: ext(4) }, je: jeStub({ fail }), jeHttp: { 1000: 403, 1001: 504, 1002: 403 } });
    await wait(700);
    // 1000 et 1002 refusés (403) → écartés ; 1001 (délai) → carte Reco+ ; 1003 → carte JE
    assert.deepEqual(p.row().filter((x) => x !== 'L'), ['E:1001', 'E:1003']);
    assert.ok(p.sections.querySelector('[data-rp-id="tmdb:movie:1001"]').classList.contains('recoplus-jemiss'));
    assert.equal(p.calls.je.length, 3, 'raison du refus lue seulement pour les fiches manquantes');
});

console.log(failures ? `\n${failures} test(s) en échec` : '\ntous les tests passent');
process.exit(failures ? 1 : 0);
