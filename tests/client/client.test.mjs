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
function page({ hssCards = 0, data, je = null }) {
    const dom = new JSDOM('<!doctype html><html><head></head><body><div id="indexPage"><div id="homeTab" class="tabContent" data-index="0"><div class="sections"></div></div></div></body></html>', { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const calls = { getJSON: 0, posts: [] };
    w.ApiClient = {
        getCurrentUserId: () => 'u1',
        getUrl: (p) => '/' + p,
        getJSON: () => { calls.getJSON++; return Promise.resolve(JSON.parse(JSON.stringify(data))); },
        ajax: (o) => { calls.posts.push(o); return Promise.resolve({}); },
    };
    if (je) { w.JellyfinEnhanced = je(w); }
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
        s.querySelector('.itemsContainer').innerHTML = Array.from({ length: n }, (_, i) => `<div class="card" data-lib="${i}">L${i}</div>`).join('');
    };
    if (hssCards) { fill(hssCards); }
    w.eval(SCRIPT);
    const row = () => Array.from(sections.querySelectorAll('.RecoPlusPourToi .itemsContainer > *')).map((c) => (c.classList.contains('recoplus-ext') ? 'E:' + (c.dataset.tmdb || c.textContent.split('Ext ')[1]?.split(/\D/)[0]) : 'L'));
    return { w, sections, calls, fill, row };
}

function jeStub({ active = true, userFound = true, status = {} } = {}) {
    return (w) => ({
        jellyseerrAPI: {
            checkUserStatus: () => Promise.resolve({ active, userFound }),
            fetchMovieDetails: (id) => Promise.resolve(id === 1003 ? null : { id, title: 'M' + id, mediaInfo: status[id] ? { status: status[id] } : undefined }),
            fetchTvShowDetails: (id) => Promise.resolve({ id, name: 'T' + id, mediaInfo: status[id] ? { status: status[id] } : undefined }),
        },
        jellyseerrUI: {
            created: [],
            createJellyseerrCard(item, a, u) {
                this.created.push({ item, a, u });
                const c = w.document.createElement('div');
                c.className = 'card overflowPortraitCard jellyseerr-card';
                c.dataset.tmdb = String(item.id);
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
    // 1001 (en attente) et 1004 (disponible) écartés, 1003 sans fiche écarté → 1000, 1002, 1005, 1006, 1007
    assert.deepEqual(p.row(), ['L', 'L', 'E:1000', 'L', 'L', 'E:1002', 'L', 'L', 'E:1005', 'L', 'L', 'E:1006', 'L', 'L', 'E:1007']);
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

console.log(failures ? `\n${failures} test(s) en échec` : '\ntous les tests passent');
process.exit(failures ? 1 : 0);
