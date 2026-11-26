// Tests de la page Reco+ de chaque utilisateur (Plugin Pages) dans un DOM simulé : node tests/client/userpage.test.mjs.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '../..');
const HTML = readFileSync(join(root, 'src/Jellyfin.Plugin.RecoPlus/Web/userPage.html'), 'utf8');
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
async function test(name, fn) {
    try { await fn(); console.log('ok   ' + name); } catch (e) { failures++; console.log('ÉCHEC ' + name + '\n     ' + (e.stack || e)); }
}

const ME = {
    open: true,
    region: { canSet: true, chosen: null, current: 'BE', auto: 'BE', detected: true, server: null, clues: [{ source: 'timezone', country: 'BE' }, { source: 'browser', country: 'FR' }] },
    extended: { canSet: true, server: true, mine: null },
    countries: ['BE', 'CA', 'FR'],
};

// la page arrive comme Plugin Pages l'insère : un fragment dans la page de Jellyfin, ses scripts exécutés
async function open(me, opts) {
    const dom = new JSDOM('<!doctype html><html lang="' + ((opts && opts.lang) || 'fr') + '"><body><div class="c"></div></body></html>', { runScripts: 'dangerously', pretendToBeVisual: true });
    const w = dom.window;
    const posts = [];
    w.ApiClient = {
        getUrl: (p, q) => '/' + p + (q ? '?' + new URLSearchParams(q) : ''),
        ajax: (o) => {
            const u = o.url.split('?')[0];
            if (o.type === 'POST') { posts.push({ url: u, body: JSON.parse(o.data) }); return Promise.resolve({ message: 'ok' }); }
            if (u === '/RecoPlus/I18n') { return Promise.resolve((opts && opts.i18n) || { lang: 'fr', dict: {} }); }
            if (u === '/RecoPlus/Me') { return Promise.resolve(JSON.parse(JSON.stringify(me))); }
            return Promise.reject(new Error('inattendu : ' + u));
        },
    };
    const frag = w.document.createRange().createContextualFragment(HTML);
    w.document.querySelector('.c').append(frag);
    // un navigateur exécute les scripts d'un fragment de createContextualFragment à l'insertion ; jsdom non : on les réinsère
    for (const old of [...w.document.querySelectorAll('.c script')]) {
        const s = w.document.createElement('script');
        s.textContent = old.textContent;
        old.replaceWith(s);
    }
    await wait(30);
    return { w, d: w.document, posts };
}

await test('pays : automatique avec la raison, puis la liste des pays ; le choix et la case s\'enregistrent', async () => {
    const { d, posts } = await open(ME);
    assert.equal(d.getElementById('rpuRegionBox').hidden, false);
    assert.equal(d.getElementById('rpuClosed').hidden, true);
    const opts = [...d.getElementById('rpuRegion').options];
    assert.equal(opts[0].value, '');
    assert.match(opts[0].textContent, /^Automatique \(/);
    assert.deepEqual(opts.slice(1).map((o) => o.value).sort(), ['BE', 'CA', 'FR']);
    // la raison ne cite que les indices du pays retenu
    const why = d.getElementById('rpuRegionWhy').textContent;
    assert.match(why, /fuseau horaire du navigateur/);
    assert.doesNotMatch(why, /langue du navigateur/);
    assert.equal(d.getElementById('rpuExtended').checked, true);
    d.getElementById('rpuRegion').value = 'CA';
    d.getElementById('rpuExtended').checked = false;
    d.getElementById('rpuSave').click();
    await wait(20);
    assert.deepEqual(posts, [{ url: '/RecoPlus/Me', body: { Region: 'CA', Extended: false } }]);
    // automatique et case cochée : rien de fixé (le serveur décide)
    d.getElementById('rpuRegion').value = '';
    d.getElementById('rpuExtended').checked = true;
    d.getElementById('rpuSave').click();
    await wait(20);
    assert.deepEqual(posts[1].body, { Region: null, Extended: null });
});

await test('réglages fermés : seulement le message, rien à enregistrer', async () => {
    const { d } = await open({ ...ME, open: false, region: { ...ME.region, canSet: false }, extended: { ...ME.extended, canSet: false } });
    assert.equal(d.getElementById('rpuClosed').hidden, false);
    assert.equal(d.getElementById('rpuRegionBox').hidden, true);
    assert.equal(d.getElementById('rpuExtBox').hidden, true);
    assert.equal(d.getElementById('rpuSaveBox').hidden, true);
});

await test('sans Seerr ou sans le droit : la case des titres hors bibliothèque n\'apparaît pas ; pays fixé par l\'administrateur dit', async () => {
    const { d } = await open({ ...ME, region: { ...ME.region, server: 'CH', auto: 'CH', detected: false }, extended: { canSet: false, server: false, mine: null } });
    assert.equal(d.getElementById('rpuExtBox').hidden, true);
    assert.match(d.getElementById('rpuRegionWhy').textContent, /fixé par l'administrateur/);
});

await test('en anglais : textes traduits par le dictionnaire du serveur', async () => {
    const EN = JSON.parse(readFileSync(join(root, 'src/RecoPlus.Engine/english.json'), 'utf8'));
    const { d } = await open(ME, { lang: 'en', i18n: { lang: 'en', dict: EN } });
    assert.equal(d.querySelector('h2').textContent, 'Reco+: my settings');
    assert.match(d.getElementById('rpuRegionWhy').textContent, /^Automatic: .*browser time zone/);
});

await test('langue : chaque texte de la page a sa traduction anglaise, allemande et espagnole (mêmes paramètres)', async () => {
    const texts = new Set();
    for (const m of HTML.matchAll(/data-t="([^"]+)"/g)) { texts.add(m[1].replace(/&amp;/g, '&')); }
    for (const m of HTML.matchAll(/\btrf?\('((?:[^'\\]|\\.)*)'/g)) { texts.add(m[1].replace(/\\'/g, '\'')); }
    const src = HTML.match(/var SOURCES = \{([\s\S]*?)\};/)[1];
    for (const m of src.matchAll(/:\s*'((?:[^'\\]|\\.)*)'/g)) { texts.add(m[1]); }
    assert.ok(texts.size > 15, 'textes trouvés : ' + texts.size);
    const ph = (s) => [...s.matchAll(/\{\d+\}/g)].map((m) => m[0]).sort().join();
    for (const f of ['english', 'german', 'spanish']) {
        const dict = JSON.parse(readFileSync(join(root, 'src/RecoPlus.Engine/' + f + '.json'), 'utf8'));
        const missing = [...texts].filter((s) => !dict[s] || ph(dict[s]) !== ph(s));
        assert.deepEqual(missing, [], f);
    }
});

await test('aucun texte venu du serveur n\'est inséré comme du HTML', async () => {
    const { d } = await open({ ...ME, countries: ['<img src=x onerror=alert(1)>'] });
    assert.equal(d.querySelectorAll('#RecoPlusUserPage img').length, 0);
    assert.doesNotMatch(HTML, /innerHTML|insertAdjacentHTML|document\.write/);
});

if (failures) { console.log(failures + ' test(s) en échec'); process.exit(1); }
console.log('tous les tests passent');
