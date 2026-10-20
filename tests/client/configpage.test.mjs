// Tests de la page de configuration dans un DOM simulé : node tests/client/configpage.test.mjs (jsdom requis).
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '../..');
const HTML = readFileSync(join(root, 'src/Jellyfin.Plugin.RecoPlus/Web/configPage.html'), 'utf8');
const csProps = (file) => new Set([...readFileSync(join(root, file), 'utf8').matchAll(/public\s+[\w<>?.,\s]+?\s+(\w+)\s*\{\s*get;\s*set;/g)].map((m) => m[1]));
const PROPS = {
    c: csProps('src/Jellyfin.Plugin.RecoPlus/Configuration/PluginConfiguration.cs'),
    e: csProps('src/RecoPlus.Engine/Settings.cs'),
    l: csProps('src/RecoPlus.Engine/ListPolicy.cs'),
};
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
async function test(name, fn) {
    try { await fn(); console.log('ok   ' + name); } catch (e) { failures++; console.log('ÉCHEC ' + name + '\n     ' + (e.stack || e)); }
}

const users = [{ id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', name: 'alice', ready: true }, { id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', name: 'bob', ready: false }];
const status = { version: '1.0.3.0', engine: { UsersWithRecs: 1 }, alerts: [], checks: [], disabled: {}, hss: 'ok', secrets: { seerrApiKey: 'non défini', webhookUrl: 'non défini', webhookToken: 'non défini' }, calibration: {}, canRollback: false, users };

function open(cfg) {
    const dom = new JSDOM(HTML, { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const saved = [];
    const store = JSON.parse(JSON.stringify(cfg));
    w.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert(m) { throw new Error(m); }, processPluginConfigurationUpdateResult() {} };
    w.ApiClient = {
        getUrl: (p) => '/' + p,
        ajax: (o) => Promise.resolve(o.url === '/RecoPlus/Status' ? status : o.url === '/RecoPlus/Genres' ? ['Animation', 'Documentaire', 'Science-Fiction'] : {}),
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(store))),
        updatePluginConfiguration: (id, c) => { saved.push(JSON.parse(JSON.stringify(c))); return Promise.resolve({}); },
    };
    w.confirm = () => true;
    const script = HTML.match(/<script>([\s\S]*?)<\/script>/)[1];
    w.eval(script);
    w.document.querySelector('#RecoPlusConfigPage').dispatchEvent(new w.Event('pageshow'));
    return { w, d: w.document, saved };
}

function fieldsOf(html) {
    const src = html.match(/var F = \[([\s\S]*?)\n\s*\];/)[1];
    return [...src.matchAll(/^\s*\['(\w+)',\s*'((?:[^'\\]|\\.)*)',\s*'([^']+)',\s*'((?:[^'\\]|\\.)*)',\s*[\d.]+,\s*[\d.]+,\s*[\d.]+,\s*'(\w+)',\s*'([cel])'/gm)]
        .map((m) => ({ key: m[1], label: m[2], type: m[3], tip: m[4], group: m[5], target: m[6] }));
}

const cfg = {
    Enabled: true, SimilarPersonalization: 0.25, ExcludedUsers: ['bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'],
    Engine: { HalfLifeDays: 90, Mmr: 0.3, Saga: true, Decay: 'Exponential' },
    Lists: { Window: 20, RandomPercent: 0, MinMovies: 0, MinSeries: 0, ExcludedGenres: ['documentaire', 'Western'], MinCommunityRating: 0, MinYear: 0 },
};

await test('chaque réglage de la page existe côté serveur (aucune faute de nom)', async () => {
    const f = fieldsOf(HTML);
    assert.ok(f.length > 60, 'champs lus : ' + f.length);
    const missing = f.filter((x) => x.type !== 'note' && !PROPS[x.target].has(x.key)).map((x) => x.target + ':' + x.key);
    assert.deepEqual(missing, []);
    const dup = f.map((x) => x.target + x.key).filter((k, i, a) => a.indexOf(k) !== i);
    assert.deepEqual(dup, []);
});

await test('chaque réglage a une aide au survol', async () => {
    const { d } = open(cfg);
    await wait(50);
    const f = fieldsOf(HTML).filter((x) => x.type !== 'users' && x.type !== 'genres');
    const noTip = f.filter((x) => !x.tip).map((x) => x.key);
    assert.deepEqual(noTip, []);
    for (const x of f) {
        const field = d.getElementById(`rp_${x.target}_${x.key}`).closest('.rp-field');
        const t = field.querySelector('.rp-tip');
        assert.ok(t, 'pas de bulle : ' + x.key);
        assert.equal(t.getAttribute('tabindex'), '0', 'bulle inaccessible au clavier/toucher : ' + x.key);
        assert.ok(t.querySelector('.rp-bubble').textContent.length > 10);
    }
});

await test('chaque groupe de champs a sa place dans la page, et les onglets réglables affichent Enregistrer', async () => {
    const { d, w } = open(cfg);
    const groups = new Set(fieldsOf(HTML).map((x) => x.group));
    for (const g of groups) { assert.ok(d.querySelector(`[data-fields="${g}"]`), 'groupe sans place : ' + g); }
    const bar = d.getElementById('rpSaveBar');
    d.querySelector('[data-tab="listes"]').click();
    assert.equal(bar.style.display, '');
    d.querySelector('[data-tab="apercu"]').click();
    assert.equal(bar.style.display, 'none');
    assert.ok(d.querySelector('[data-pane="listes"]').classList.contains('on') === false);
    void w;
});

await test('les réglages avancés sont repliés par défaut, les essentiels visibles', async () => {
    const { d } = open(cfg);
    for (const det of d.querySelectorAll('details.rp-adv')) { assert.equal(det.open, false); }
    assert.equal(d.getElementById('rp_l_RandomPercent').closest('details'), null);
    assert.equal(d.getElementById('rp_l_MinMovies').closest('details'), null);
    assert.ok(d.getElementById('rp_e_Bm25K1').closest('details'));
    assert.ok(d.getElementById('rp_e_GateTop').closest('details'));
});

await test('aller-retour : comptes ignorés, genres exclus, nouveaux réglages de liste', async () => {
    const { d, saved } = open(cfg);
    await wait(50);
    const userBoxes = [...d.querySelectorAll('#rp_c_ExcludedUsers input')];
    assert.deepEqual(userBoxes.map((c) => [c.value, c.checked]), [[users[0].id, false], [users[1].id, true]]);
    const genreBoxes = [...d.querySelectorAll('#rp_l_ExcludedGenres input')];
    // « Western » n'est plus dans la bibliothèque mais reste exclu, et reste décochable
    assert.deepEqual(genreBoxes.map((c) => [c.value, c.checked]), [['Animation', false], ['Documentaire', true], ['Science-Fiction', false], ['Western', true]]);

    userBoxes[0].checked = true;
    genreBoxes[3].checked = false;
    d.getElementById('rp_l_RandomPercent').value = '10';
    d.getElementById('rp_l_MinSeries').value = '4';
    d.getElementById('rp_l_MinCommunityRating').value = '6.5';
    d.getElementById('rp_e_HalfLifeDays').value = '';
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved.length, 1);
    const s = saved[0];
    assert.deepEqual(s.ExcludedUsers, [users[0].id, users[1].id]);
    assert.deepEqual(s.Lists.ExcludedGenres, ['Documentaire']);
    assert.equal(s.Lists.RandomPercent, 10);
    assert.equal(s.Lists.MinSeries, 4);
    assert.equal(s.Lists.MinCommunityRating, 6.5);
    assert.equal(s.Engine.HalfLifeDays, 0, 'champ vidé → 0 (le serveur borne), jamais NaN');
    assert.equal(s.Engine.Mmr, 0.3, 'facteur inchangé');
});

await test('facteurs : interrupteur + force, enregistrés comme poids (coupé = 0)', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c.Engine, { WMaxSim: 2, WCollab: 0, RequestWeight: 1.2, WFresh: 4, FeatActor: 0.7 });
    const { d, saved } = open(c);
    await wait(50);
    const f = (k) => ({ on: d.getElementById(`rp_e_${k}_on`), r: d.getElementById(`rp_e_${k}`), m: d.getElementById(`rp_e_${k}`).closest('.rp-factor').querySelector('.rp-mult') });
    assert.equal(f('WMaxSim').on.checked, true);
    assert.equal(f('WMaxSim').r.value, '1');
    assert.match(f('WMaxSim').m.textContent, /×1,00 · normal/);
    assert.equal(f('WCollab').on.checked, false);
    assert.equal(f('WCollab').r.disabled, true);
    assert.equal(f('WCollab').m.textContent, 'coupé');
    assert.equal(f('RequestWeight').r.value, '2', '1,2 = ×2 de 0,6');
    assert.match(f('RequestWeight').m.textContent, /renforcé/);
    assert.equal(f('WFresh').r.max, '2', 'nouveauté plafonnée à ×2 (4)');

    f('WMaxSim').on.checked = false; f('WMaxSim').on.dispatchEvent(new d.defaultView.Event('change'));
    f('WCollab').on.checked = true; f('WCollab').on.dispatchEvent(new d.defaultView.Event('change'));
    f('WCollab').r.value = '1.5'; f('WCollab').r.dispatchEvent(new d.defaultView.Event('input'));
    assert.match(f('WCollab').m.textContent, /×1,50 · renforcé/);
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    const e = saved[0].Engine;
    assert.equal(e.WMaxSim, 0);
    assert.equal(e.WCollab, 0.45);
    assert.equal(e.RequestWeight, 1.2);
    assert.equal(e.WFresh, 4);
    assert.equal(e.FeatActor, 0.7);
});

await test('le nom de la rangée suit le titre saisi, partout', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    c.HssSectionTitle = 'Contenu que vous devriez aimer';
    const { d } = open(c);
    await wait(50);
    assert.match(d.querySelector('[data-pane="listes"] .rp-sub').textContent, /« Contenu que vous devriez aimer »/);
    const input = d.getElementById('rp_c_HssSectionTitle');
    input.value = 'Pour vous';
    input.dispatchEvent(new d.defaultView.Event('input'));
    assert.match(d.querySelector('[data-pane="listes"] .rp-sub').textContent, /« Pour vous »/);
    assert.match(d.getElementById('rp_c_ExtendedMixMode').closest('.rp-field').querySelector('label').textContent, /« Pour vous »/);
    const ui = d.querySelector('.content-primary');
    assert.ok(!ui.textContent.includes('{ROW}'), 'modèle non remplacé');
    assert.ok(!ui.textContent.includes('« Pour toi »'), 'nom codé en dur');
    assert.ok([...ui.querySelectorAll('.rp-tip')].every((t) => !t.getAttribute('aria-label').includes('{ROW}')));
});

await test('valeur hors curseur (calibrage) : affichée en entier et jamais modifiée sans action', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c.Engine, { WCollab: 1.0, WRelease: 0, FeatLanguage: 0.75, FavEpisodeShare: 0.3 });
    const { d, saved } = open(c);
    await wait(50);
    const r = d.getElementById('rp_e_WCollab');
    assert.ok(Number(r.max) >= 3.33, 'curseur élargi : ' + r.max);
    assert.equal(d.getElementById('rp_e_WRelease_on').checked, false, 'sortie récente coupée par défaut');
    assert.match(d.getElementById('rp_e_FeatLanguage').closest('.rp-factor').querySelector('.rp-mult').textContent, /×1,50/);
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].Engine.WCollab, 1.0, 'pas de troncature à ×3 ni d\'arrondi au pas du curseur');
    assert.equal(saved[0].Engine.FeatLanguage, 0.75);
    assert.equal(saved[0].Engine.WRelease, 0);
    // activée par l'utilisateur : valeur de référence × force
    const on = d.getElementById('rp_e_WRelease_on');
    on.checked = true; on.dispatchEvent(new d.defaultView.Event('change'));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[1].Engine.WRelease, 1);
});

await test('1.0.5 : rangées, diversité, affichages — aller-retour', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { RowSize: 20, RowBecauseTitle: 'Parce que tu as aimé {titre}', TrackImpressions: true });
    Object.assign(c.Engine, { Diversity: 'Personalized', WImpression: 0.4, WShortTerm: 0, CalibrationStrength: 0.5 });
    const { d, saved } = open(c);
    await wait(50);
    assert.equal(d.getElementById('rp_e_Diversity').value, 'Personalized');
    assert.equal(d.getElementById('rp_e_WShortTerm_on').checked, false, 'goût du moment coupé par défaut');
    assert.ok(d.getElementById('rp_c_RowSize').closest('details.rp-adv'), 'taille des rangées : réglage avancé');
    assert.ok(!d.getElementById('rp_c_RowBecauseTitle').closest('details.rp-adv'), 'titres : réglage de base');
    d.getElementById('rp_e_Diversity').value = 'Calibrated';
    d.getElementById('rp_c_RowSeriesTitle').value = 'Séries à voir';
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    const s = saved[0];
    assert.equal(s.Engine.Diversity, 'Calibrated');
    assert.equal(s.RowSeriesTitle, 'Séries à voir');
    assert.ok(!('RowSeries' in s) && !('RowBecause' in s) && !('HssSection' in s), 'plus d\'interrupteurs de rangées : c\'est le rôle de HSS');
    assert.equal(s.Engine.WImpression, 0.4);
    assert.equal(s.Engine.WShortTerm, 0);
    assert.equal(s.TrackImpressions, true);
});

await test('chaque onglet de réglages : l\'avancé est replié par défaut', async () => {
    const { d } = open(cfg);
    await wait(50);
    const panes = [...d.querySelectorAll('#rpForm .rp-pane')];
    const withAdv = panes.filter((p) => p.querySelector('details.rp-adv'));
    assert.ok(withAdv.length >= 5, 'onglets avec avancé : ' + withAdv.length);
    withAdv.forEach((p) => p.querySelectorAll('details.rp-adv').forEach((x) => assert.equal(x.open, false)));
});

await test('calibrage : méthode enregistrée au changement, avertissement si l\'historique ne suffit pas', async () => {
    const { w, d, saved } = open({ ...cfg, CalibrationMethod: 'Auto' });
    const data = { cases: 70, methods: { Auto: { level: 'limite', message: '70 cas…' }, ChoiceModel: { level: 'limite', message: '70 cas pour 10 réglages' }, Coordinate: { level: 'insuffisant', message: '70 cas pour 36 réglages : pas assez' } } };
    const ajax = w.ApiClient.ajax;
    w.ApiClient.ajax = (o) => (o.url === '/RecoPlus/Calibration/Data' ? Promise.resolve(data) : ajax(o));
    await wait(50);
    assert.equal(d.getElementById('rpCalMethod').value, 'Auto');
    d.querySelector('.rp-tab[data-tab="calibrage"]').click();
    await wait(50);
    const sel = d.getElementById('rpCalMethod');
    sel.value = 'Coordinate';
    sel.dispatchEvent(new w.Event('change'));
    await wait(50);
    assert.equal(saved.at(-1).CalibrationMethod, 'Coordinate');
    assert.match(d.getElementById('rpCalData').textContent, /^⚠ Historique actuel : 70 cas pour 36 réglages/);
    assert.match(d.getElementById('rpCalData').className, /ko/);
    assert.match([...sel.options].find((o) => o.value === 'ChoiceModel').textContent, /historique limite$/);
    assert.ok([...sel.options].some((o) => o.value === 'Neural'), 'réseau de neurones proposé');
});

await test('rangées d\'accueil : seulement les titres, tous au même endroit (le choix des rangées est celui de HSS)', async () => {
    const { d } = open({ ...cfg, RowNewTitle: 'Nouveautés pour vous', ExtendedTitle: 'À découvrir' });
    await wait(50);
    const rows = d.querySelector('[data-fields="rows"]');
    const ids = [...rows.querySelectorAll('input, select')].map((e) => e.id);
    assert.deepEqual(ids, ['rp_c_HssSectionTitle', 'rp_c_RowNewTitle', 'rp_c_RowBecauseTitle', 'rp_c_RowMoviesTitle', 'rp_c_RowSeriesTitle', 'rp_c_ExtendedTitle']);
    assert.ok(!rows.closest('details'), 'de base, pas replié');
    assert.equal(d.getElementById('rp_c_RowNewTitle').value, 'Nouveautés pour vous');
    assert.equal(d.querySelectorAll('[id$="Title"]:not([data-fields="rows"] *)').length, 0, 'aucun titre de rangée ailleurs');
    assert.ok(!d.querySelector('.content-primary').textContent.includes('prochain démarrage'), 'plus de redémarrage à annoncer');
});

await test('réglages sans objet masqués selon le mode, valeurs gardées', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { ExtendedHomeRow: true, ExtendedMixMode: 'Score', ExtendedMixEvery: 4 });
    Object.assign(c.Engine, { Diversity: 'Personalized', CalibrationStrength: 0.5, Weighting: 'TfIdf', WShortTerm: 0, SeriesByTime: true });
    const { d, saved } = open(c);
    await wait(50);
    const hidden = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-hidden');
    assert.ok(hidden('rp_c_ExtendedMixEvery'), 'mélange libre : pas d\'intervalle');
    assert.ok(!hidden('rp_c_ExtendedMinFree'), 'mélange libre : minimum visible');
    assert.ok(hidden('rp_e_CalibrationStrength') && !hidden('rp_e_Mmr'));
    assert.ok(hidden('rp_e_Bm25K1') && hidden('rp_e_ShortTermTitles') && hidden('rp_e_SeriesFullEpisodes') && !hidden('rp_e_SeriesFullHours'));
    const sel = d.getElementById('rp_c_ExtendedMixMode'); sel.value = 'Interleave'; sel.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!hidden('rp_c_ExtendedMixEvery') && hidden('rp_c_ExtendedMinFree'));
    const div = d.getElementById('rp_e_Diversity'); div.value = 'Calibrated'; div.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!hidden('rp_e_CalibrationStrength') && hidden('rp_e_Mmr'));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].ExtendedMixEvery, 4, 'valeur masquée gardée');
    assert.equal(saved[0].Engine.Mmr, 0.3);
});

await test('calibrage : écarts en %, méthode sans changement signalée, application non recommandée confirmée à part', async () => {
    const st = JSON.parse(JSON.stringify(status));
    st.calibration = { running: true, progress: 21.6, at: '2026-10-20T12:00:00Z', report: {
        current: { gain: 0.25, hit10: 0.2, hit20: 0.3, events: 155, accounts: 13 }, proposed: { gain: 0.251, hit10: 0.2, hit20: 0.3 },
        cvDelta: 0.00075, ciLow: 0.0000, ciHigh: 0.002, recommended: false, verdict: 'négligeable', method: 'ChoiceModel', steps: [],
        methods: [{ method: 'ChoiceModel', name: 'Modèle de choix appris', gain: 0.251, cvDelta: 0.00075, ciLow: 0, ciHigh: 0.002, sufficiency: 'suffisant', recommended: false, noChange: false },
                  { method: 'Neural', name: 'Réseau de neurones (expérimental)', gain: 0.25, cvDelta: 0, ciLow: 0, ciHigh: 0, sufficiency: 'insuffisant', recommended: false, noChange: true }] } };
    const { w, d } = open(cfg);
    const ajax = w.ApiClient.ajax;
    w.ApiClient.ajax = (o) => (o.url === '/RecoPlus/Status' ? Promise.resolve(st) : ajax(o));
    w.document.querySelector('#RecoPlusConfigPage').dispatchEvent(new w.Event('pageshow'));
    await wait(80);
    const t = d.getElementById('rpCalReport').textContent;
    assert.match(t, /calcul précédent/);
    assert.match(t, /\+0,3 %/);
    assert.match(t, /aucun changement appris/);
    assert.ok(!/\+0\.0008/.test(t), 'pas de valeur brute');
});

console.log(failures ? `\n${failures} test(s) en échec` : '\ntous les tests passent');
process.exit(failures ? 1 : 0);
