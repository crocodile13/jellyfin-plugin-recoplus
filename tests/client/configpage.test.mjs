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

async function open(cfg, extra) {
    const dom = new JSDOM(HTML, { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const saved = [], calls = [];
    const store = JSON.parse(JSON.stringify(cfg));
    w.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert(m) { throw new Error(m); }, processPluginConfigurationUpdateResult() {} };
    w.ApiClient = {
        getUrl: (p) => '/' + p,
        ajax: (o) => {
            const u = o.url.split('?')[0];
            calls.push(o.url);
            return Promise.resolve(extra && extra[u] !== undefined ? (typeof extra[u] === 'function' ? extra[u](o) : extra[u]) : u === '/RecoPlus/Status' ? status : u === '/RecoPlus/Genres' ? ['Animation', 'Documentaire', 'Science-Fiction'] : {});
        },
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(store))),
        updatePluginConfiguration: (id, c) => { saved.push(JSON.parse(JSON.stringify(c))); return Promise.resolve({}); },
    };
    w.confirm = () => true;
    const script = HTML.match(/<script>([\s\S]*?)<\/script>/)[1];
    w.eval(script);
    const page = w.document.querySelector('#RecoPlusConfigPage');
    page.dispatchEvent(new w.Event('pageshow'));
    await page.__rpReady;           // langue chargée, page construite
    return { w, d: w.document, saved, calls };
}

function fieldsOf(html) {
    const src = html.match(/var F = \[([\s\S]*?)\n\s*\];/)[1];
    return [...src.matchAll(/^\s*\['(\w+)',\s*'((?:[^'\\]|\\.)*)',\s*'([^']+)',\s*'((?:[^'\\]|\\.)*)',\s*[\d.]+,\s*[\d.]+,\s*[\d.]+,\s*'(\w+)',\s*'([cel])'/gm)]
        .map((m) => ({ key: m[1], label: m[2], type: m[3], tip: m[4], group: m[5], target: m[6] }));
}

const cfg = {
    Enabled: true, SeerrEnabled: true, ExtendedEnabled: true, SourcePourToi: 'Mix', SimilarPersonalization: 0.25, ExcludedUsers: ['bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'],
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
    const { d } = await open(cfg);
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
    const { d, w } = await open(cfg);
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
    const { d } = await open(cfg);
    for (const det of d.querySelectorAll('details.rp-adv')) { assert.equal(det.open, false); }
    assert.equal(d.getElementById('rp_l_RandomPercent').closest('details'), null);
    assert.equal(d.getElementById('rp_l_MinMovies').closest('details'), null);
    assert.ok(d.getElementById('rp_e_Bm25K1').closest('details'));
    assert.ok(d.getElementById('rp_e_GateTop').closest('details'));
});

await test('aller-retour : comptes ignorés, genres exclus, nouveaux réglages de liste', async () => {
    const { d, saved } = await open(cfg);
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
    const { d, saved } = await open(c);
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
    const { d } = await open(c);
    await wait(50);
    assert.match(d.querySelector('[data-pane="listes"] .rp-sub').textContent, /« Contenu que vous devriez aimer »/);
    const input = d.getElementById('rp_c_HssSectionTitle');
    input.value = 'Pour vous';
    input.dispatchEvent(new d.defaultView.Event('input'));
    assert.match(d.querySelector('[data-pane="listes"] .rp-sub').textContent, /« Pour vous »/);
    assert.match(d.getElementById('rp_c_SourcePourToi').closest('.rp-field').querySelector('label').textContent, /« Pour vous »/);
    const ui = d.querySelector('.content-primary');
    assert.ok(!ui.textContent.includes('{ROW}'), 'modèle non remplacé');
    assert.ok(!ui.textContent.includes('« Pour toi »'), 'nom codé en dur');
    assert.ok([...ui.querySelectorAll('.rp-tip')].every((t) => !t.getAttribute('aria-label').includes('{ROW}')));
});

await test('valeur hors curseur (calibrage) : affichée en entier et jamais modifiée sans action', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c.Engine, { WCollab: 1.0, WRelease: 0, FeatLanguage: 0.75, FavEpisodeShare: 0.3 });
    const { d, saved } = await open(c);
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
    const { d, saved } = await open(c);
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
    const { d } = await open(cfg);
    await wait(50);
    const panes = [...d.querySelectorAll('#rpForm .rp-pane')];
    const withAdv = panes.filter((p) => p.querySelector('details.rp-adv'));
    assert.ok(withAdv.length >= 5, 'onglets avec avancé : ' + withAdv.length);
    withAdv.forEach((p) => p.querySelectorAll('details.rp-adv').forEach((x) => assert.equal(x.open, false)));
});

await test('calibrage : méthode enregistrée au changement, avertissement si l\'historique ne suffit pas', async () => {
    const { w, d, saved } = await open({ ...cfg, CalibrationMethod: 'Auto' });
    const data = { cases: 70, methods: { Auto: { level: 'limite', message: '70 cas…' }, ChoiceModel: { level: 'limite', message: '70 cas pour 10 réglages' }, Coordinate: { level: 'insuffisant', message: '70 cas pour 36 réglages : pas assez' } } };
    const ajax = w.ApiClient.ajax;
    w.ApiClient.ajax = (o) => (o.url.split('?')[0] === '/RecoPlus/Calibration/Data' ? Promise.resolve(data) : ajax(o));
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
    const { d } = await open({ ...cfg, RowNewTitle: 'Nouveautés pour vous', ExtendedTitle: 'À découvrir' });
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
    const { d, saved } = await open(c);
    await wait(50);
    const hidden = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
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

await test('mode étendu : contenu de chaque rangée, titres des rangées à jour, réglages du mélange seulement s\'il sert', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { SourcePourToi: 'Mix', SourceNouveautes: 'Local', SourceParceQue: 'Local', SourceFilms: 'Seerr', SourceSeries: 'Local', RowMoviesTitle: 'Mes films', RowNewReleasedDays: 200, ExtendedMixMode: 'Score' });
    const { d, saved } = await open(c);
    await wait(50);
    const box = d.querySelector('[data-fields="exthome"]');
    const sel = [...box.querySelectorAll('select')].map((e) => e.id);
    assert.deepEqual(sel, ['rp_c_SourcePourToi', 'rp_c_SourceNouveautes', 'rp_c_SourceParceQue', 'rp_c_SourceFilms', 'rp_c_SourceSeries']);
    assert.deepEqual([...d.getElementById('rp_c_SourceFilms').options].map((o) => o.value), ['Local', 'Mix', 'Seerr']);
    const label = (id) => d.getElementById(id).closest('.rp-field').querySelector('label').textContent;
    assert.equal(label('rp_c_SourceFilms'), 'Rangée « Mes films »');
    assert.equal(label('rp_c_SourceParceQue'), 'Rangées « Parce que tu as aimé… »');
    const t = d.getElementById('rp_c_RowMoviesTitle'); t.value = 'Films à voir'; t.dispatchEvent(new d.defaultView.Event('input'));
    assert.equal(label('rp_c_SourceFilms'), 'Rangée « Films à voir »');
    assert.ok(!d.getElementById('rp_c_ExtendedHomeRow'), 'ancien interrupteur retiré');
    const hidden = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
    const mixBox = () => d.getElementById('rpMixBox').classList.contains('rp-box-off');
    assert.ok(!mixBox(), 'une rangée mélangée : réglages du mélange visibles');
    assert.ok(hidden('rp_c_RowNewReleasedDays'), 'nouveautés en bibliothèque seule');
    assert.ok(!hidden('rp_c_ExtendedTitle'), 'rangée à part possible');
    const change = (id, v) => { const e = d.getElementById(id); e.value = v; e.dispatchEvent(new d.defaultView.Event('change', { bubbles: true })); };
    change('rp_c_SourcePourToi', 'Local');
    assert.ok(mixBox(), 'plus aucune rangée mélangée');
    assert.ok(hidden('rp_c_ExtendedTitle') && hidden('rp_c__ExtendedRowNote'), 'pas de rangée à part');
    change('rp_c_SourceNouveautes', 'Mix');
    assert.ok(!mixBox() && !hidden('rp_c_RowNewReleasedDays'));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].SourcePourToi, 'Local');
    assert.equal(saved[0].SourceNouveautes, 'Mix');
    assert.equal(saved[0].SourceFilms, 'Seerr');
    assert.equal(saved[0].RowNewReleasedDays, 200);
});

await test('réglages sans effet : grisés, désactivés, avec la raison (une fois par boîte si elle est commune)', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { SeerrEnabled: false, SourcePourToi: 'Mix', TrackImpressions: true });
    Object.assign(c.Engine, { WImpression: 0.5, WShortTerm: 0, ShortTermTitles: 5 });
    const { d, saved } = await open(c);
    await wait(50);
    const off = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
    const why = (id) => d.getElementById(id).closest('.rp-field').getAttribute('data-why');
    assert.ok(off('rp_c_ExtendedEnabled') && d.getElementById('rp_c_ExtendedEnabled').disabled);
    assert.equal(why('rp_c_SourceFilms'), 'Seerr non utilisé (onglet Seerr)');
    const box = d.querySelector('[data-fields="exthome"]').closest('.rp-box');
    assert.ok(box.classList.contains('rp-box-off'));
    assert.equal(box.querySelector('.rp-boxwhy').textContent, 'Sans effet : Seerr non utilisé (onglet Seerr).');
    assert.ok(off('rp_e_RequestWeight'), 'demandes Seerr sans Seerr');
    assert.ok(!off('rp_c_SeerrUrl'), 'la connexion reste réglable');
    // facteur coupé : son réglage fin est grisé, avec le nom du facteur
    assert.match(why('rp_e_ShortTermTitles'), /« Goût du moment » coupé/);
    const fo = d.getElementById('rp_e_WShortTerm_on'); fo.checked = true; fo.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!off('rp_e_ShortTermTitles') && !d.getElementById('rp_e_ShortTermTitles').disabled);
    // Seerr activé : tout se rallume
    const se = d.getElementById('rp_c_SeerrEnabled'); se.checked = true; se.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!off('rp_c_SourceFilms') && !box.classList.contains('rp-box-off'));
    // plugin coupé : tout est grisé sauf l'interrupteur
    const en = d.getElementById('rp_c_Enabled'); en.checked = false; en.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!off('rp_c_Enabled') && off('rp_c_ListSize') && off('rp_e_HalfLifeDays'));
    en.checked = true; en.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].Engine.ShortTermTitles, 5, 'valeur grisée puis rallumée gardée');
    assert.equal(saved[0].SourcePourToi, 'Mix');
});

await test('export du jeu de calibrage : téléchargé avec la session, en JSON', async () => {
    const calls = [];
    const { w, d } = await open(cfg, { '/RecoPlus/Calibration/Export': (o) => { calls.push(o); return '{"format":"reco-plus-calibration-1"}'; } });
    const made = [];
    w.URL.createObjectURL = (b) => { made.push(b); return 'blob:x'; };
    w.URL.revokeObjectURL = () => {};
    let clicked = null;
    w.HTMLAnchorElement.prototype.click = function () { clicked = this.getAttribute('download'); };
    await wait(50);
    d.getElementById('rpCalExport').click();
    await wait(50);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].type, 'GET');
    assert.equal(calls[0].dataType, 'text');
    assert.equal(made.length, 1);
    assert.equal(made[0].type, 'application/json');
    assert.match(clicked, /^reco-calibrage-\d{4}-\d{2}-\d{2}\.json$/);
    assert.match(d.getElementById('rpCalExportState').textContent, /téléchargé/);
});

await test('réglages du calibrage : 0 = automatique, enregistrés aussitôt (eux seuls), grisés selon la méthode', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { CalibrationMethod: 'ChoiceModel', CalibrationFolds: 0, CalibrationPrudence: 'Auto', ListSize: 200 });
    const { d, saved } = await open(c);
    await wait(50);
    const box = d.querySelector('[data-fields="calib"]');
    assert.ok(box.closest('[data-pane="calibrage"]'));
    const ids = [...box.querySelectorAll('input, select')].map((e) => e.id);
    assert.deepEqual(ids, ['rp_c_CalibrationFolds', 'rp_c_CalibrationSlowFolds', 'rp_c_CalibrationInnerFolds', 'rp_c_CalibrationPrudence', 'rp_c_CalibrationPasses', 'rp_c_CalibrationRefine', 'rp_c_CalibrationMinGain']);
    const off = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
    assert.ok(off('rp_c_CalibrationPasses') && off('rp_c_CalibrationSlowFolds') && !off('rp_c_CalibrationInnerFolds'), 'modèle de choix : pas de curseurs');
    const m = d.getElementById('rpCalMethod'); m.value = 'Coordinate'; m.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    await wait(30);
    assert.ok(!off('rp_c_CalibrationPasses') && off('rp_c_CalibrationInnerFolds'));
    d.getElementById('rp_c_ListSize').value = '999';                // modifié sans enregistrer : ne doit pas partir
    const f = d.getElementById('rp_c_CalibrationFolds'); f.value = '4'; f.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    await wait(50);
    const last = saved.at(-1);
    assert.equal(last.CalibrationFolds, 4);
    assert.equal(last.ListSize, 200, 'le reste du formulaire n\'est pas enregistré');
    assert.equal(d.getElementById('rpCalOptState').textContent, 'enregistré');
});

await test('fils d\'exécution réglables (0 = automatique)', async () => {
    const { d, saved } = await open({ ...cfg, Threads: 6 });
    await wait(50);
    const i = d.getElementById('rp_c_Threads');
    assert.ok(i.closest('[data-fields="genadv"]'));
    assert.equal(i.value, '6');
    i.value = '0';
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].Threads, 0);
});

await test('mode étendu : sources dans « Où chercher », réglages d\'une source masqués quand elle est coupée', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { ExtendedByGenre: false, ExtendedByPeople: true, ExtendedGenres: 7, ExtendedNoise: 0.3 });
    const { d, saved } = await open(c);
    await wait(50);
    const box = d.querySelector('[data-fields="extsrc"]');
    for (const k of ['ExtendedTopRated', 'ExtendedByGenre', 'ExtendedByKeyword', 'ExtendedSagas', 'ExtendedByPeople', 'ExtendedByStudio', 'ExtendedCrossings', 'ExtendedGems', 'ExtendedByLanguage', 'ExtendedNoise']) {
        assert.ok(box.querySelector('#rp_c_' + k), k + ' dans « Où chercher »');
    }
    const hidden = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
    assert.ok(hidden('rp_c_ExtendedGenres') && !hidden('rp_c_ExtendedPeople'));
    const g = d.getElementById('rp_c_ExtendedByGenre'); g.checked = true; g.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!hidden('rp_c_ExtendedGenres'));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].ExtendedGenres, 7);
    assert.equal(saved[0].ExtendedNoise, 0.3);
    assert.equal(saved[0].ExtendedByGenre, true);
});

await test('mode étendu : réglages des pépites et croisements masqués quand la source est coupée', async () => {
    const c = JSON.parse(JSON.stringify(cfg));
    Object.assign(c, { ExtendedGems: false, ExtendedCrossings: true, ExtendedByLanguage: false, ExtendedGemMaxVotes: 300 });
    const { d, saved } = await open(c);
    await wait(50);
    const hidden = (id) => d.getElementById(id).closest('.rp-field').classList.contains('rp-off');
    for (const k of ['ExtendedGemMinRating', 'ExtendedGemMaxVotes', 'ExtendedGemGenres']) { assert.ok(hidden('rp_c_' + k), k); }
    assert.ok(!hidden('rp_c_ExtendedGemMinVotes'), 'sert aussi aux places découverte');
    assert.ok(!hidden('rp_c_ExtendedCrossingsMax'));
    assert.ok(!hidden('rp_c_ExtendedLanguageMinShare'), 'part des langues : sert aussi aux croisements');
    const g = d.getElementById('rp_c_ExtendedGems'); g.checked = true; g.dispatchEvent(new d.defaultView.Event('change', { bubbles: true }));
    assert.ok(!hidden('rp_c_ExtendedGemMaxVotes'));
    d.getElementById('rpForm').dispatchEvent(new d.defaultView.Event('submit', { cancelable: true }));
    await wait(50);
    assert.equal(saved[0].ExtendedGemMaxVotes, 300);
    assert.equal(saved[0].ExtendedGems, true);
});

await test('aperçu : tableau des sources, dans l\'ordre, « — » sans mélange libre, masqué sans exploration', async () => {
    const preview = { items: ['A'], extended: ['X (2001) — Pépite peu connue'], rows: {}, sources: {
        top: { Found: 40, Detailed: 10, Proposed: 1, Placed: null },
        related: { Found: 300, Detailed: 60, Proposed: 9, Placed: null },
        gems: { Found: 25, Detailed: 12, Proposed: 3, Placed: null },
    } };
    const { d } = await open(cfg, { '/RecoPlus/Preview': preview });
    await wait(50);
    assert.ok(d.getElementById('rpPreviewSourcesBox').classList.contains('rp-hidden'));
    d.getElementById('rpPreviewBtn').click();
    await wait(50);
    const rows = [...d.querySelectorAll('#rpPreviewSources tr')].map((tr) => [...tr.children].map((c) => c.textContent));
    assert.deepEqual(rows, [
        ['Source', 'Trouvés', 'Détaillés', 'Proposés', 'Sur l\'accueil'],
        ['Proches des titres aimés', '300', '60', '9', '—'],
        ['Pépites', '25', '12', '3', '—'],
        ['Les mieux notés', '40', '10', '1', '—'],
    ]);
    assert.ok(!d.getElementById('rpPreviewSourcesBox').classList.contains('rp-hidden'));
});

await test('mode développeur : Journal et détail par source cachés par défaut ; Moteur et Calibrage visibles (avancés)', async () => {
    const { d, w } = await open(cfg);
    await wait(50);
    const page = d.getElementById('RecoPlusConfigPage');
    const shown = (sel) => w.getComputedStyle(d.querySelector(sel)).display !== 'none';
    assert.ok(!page.classList.contains('rp-devmode'));
    assert.ok(d.querySelector('.rp-tab[data-tab="journal"]').classList.contains('rp-dev'));
    // outils de développement : export et réglages statistiques du calibrage ; la langue est un réglage de base
    assert.ok(d.getElementById('rpCalExport').closest('.rp-dev'), 'export du jeu de calibrage : développeur');
    assert.ok(d.querySelector('[data-fields="calib"]').closest('.rp-dev'), 'réglages statistiques du calibrage : développeur');
    assert.ok(!d.getElementById('rpCalStart').closest('.rp-dev') && !d.getElementById('rpCalMethod').closest('.rp-dev'), 'lancer et choisir la méthode : avancé');
    assert.ok(!d.getElementById('rp_c_Language').closest('details') && !d.getElementById('rp_c_Language').closest('.rp-dev'), 'langue : de base');
    for (const t of ['moteur', 'calibrage']) {
        const tab = d.querySelector('.rp-tab[data-tab="' + t + '"]');
        assert.ok(!tab.classList.contains('rp-dev') && shown('.rp-tab[data-tab="' + t + '"]'), t + ' visible sans mode développeur');
        assert.ok(tab.querySelector('.rp-tabtag'), t + ' marqué avancé');
    }
    assert.ok(!d.querySelector('.rp-tab[data-tab="general"]').classList.contains('rp-dev'));
    assert.ok(d.getElementById('rpPreviewSourcesBox').classList.contains('rp-dev'));
    const dev = d.getElementById('rpDev');
    dev.checked = true; dev.dispatchEvent(new w.Event('change', { bubbles: true }));
    assert.ok(page.classList.contains('rp-devmode'));
    d.querySelector('.rp-tab[data-tab="journal"]').click();
    assert.ok(d.querySelector('.rp-pane[data-pane="journal"]').classList.contains('on'));
    dev.checked = false; dev.dispatchEvent(new w.Event('change', { bubbles: true }));
    assert.ok(d.querySelector('.rp-pane[data-pane="etat"]').classList.contains('on'), 'onglet caché quitté');
    assert.ok(shown('.rp-tab[data-tab="general"]'));
});

await test('journal : entrées ajoutées en texte brut, curseur suivi, filtre de niveau', async () => {
    const calls = [];
    const chunks = [
        { cursor: 'log_1.log|10', file: 'log_1.log', entries: [
            { t: '18:00:00', l: 'INF', c: 'Jellyfin.Plugin.RecoPlus.Service.RecoWorker', m: 'Reco+ : recalcul <img src=x onerror=alert(1)>' },
            { t: '18:00:01', l: 'ERR', c: 'Jellyfin.Plugin.RecoPlus.Service.RecoService', m: 'échec' } ] },
        { cursor: 'log_1.log|20', file: 'log_1.log', entries: [{ t: '18:00:02', l: 'WRN', c: 'X.Y', m: 'attention' }] },
    ];
    const { d, w } = await open(cfg, { '/RecoPlus/Logs': (o) => { calls.push(o.url); return o.url.includes('cursor=') ? chunks[1] : chunks[0]; } });
    await wait(50);
    const dev = d.getElementById('rpDev'); dev.checked = true; dev.dispatchEvent(new w.Event('change', { bubbles: true }));
    d.querySelector('.rp-tab[data-tab="journal"]').click();
    await wait(50);
    const box = d.getElementById('rpLog');
    assert.equal(box.querySelectorAll('img').length, 0, 'aucun HTML interprété');
    assert.equal(box.children.length, 2);
    assert.match(box.children[0].textContent, /RecoWorker Reco\+ : recalcul <img/);
    d.querySelector('.rp-tab[data-tab="journal"]').click();
    await wait(50);
    assert.equal(box.children.length, 3);
    const lvl = d.getElementById('rpLogLevel'); lvl.value = 'WRN'; lvl.dispatchEvent(new w.Event('change', { bubbles: true }));
    const visible = [...box.children].filter((x) => !x.classList.contains('rp-hidden')).map((x) => x.querySelector('b').textContent.trim());
    assert.deepEqual(visible, ['ERR', 'WRN']);
    assert.match(d.getElementById('rpLogState').textContent, /log_1\.log — 3 entrées/);
    assert.ok(calls[1].includes('cursor=log_1.log%7C10'), 'curseur renvoyé');
});

await test('calibrage : chemin de chaque méthode (historique / validation croisée), étape retenue marquée', async () => {
    const st = JSON.parse(JSON.stringify(status));
    st.calibration = { running: false, progress: 100, at: '2026-10-21T12:00:00Z', report: {
        current: { gain: 0.25, hit10: 0.2, hit20: 0.3, events: 144, accounts: 15 }, proposed: { gain: 0.25, hit10: 0.2, hit20: 0.3 },
        cvDelta: 0, ciLow: 0, ciHigh: 0, recommended: false, verdict: 'on garde l\'actuel', method: 'Coordinate', steps: [],
        methods: [{ method: 'Coordinate', name: 'Recherche par curseurs', gain: 0.25, cvDelta: 0, ciLow: 0, ciHigh: 0, sufficiency: 'insuffisant', recommended: false, noChange: true,
                    fitPath: [0, 0.01, 0.03], cvPath: [0, -0.002, -0.008], picked: 0 }] } };
    const { w, d } = await open(cfg);
    const ajax = w.ApiClient.ajax;
    w.ApiClient.ajax = (o) => (o.url.split('?')[0] === '/RecoPlus/Status' ? Promise.resolve(st) : ajax(o));
    w.document.querySelector('#RecoPlusConfigPage').dispatchEvent(new w.Event('pageshow'));
    await wait(80);
    const rep = d.getElementById('rpCalReport');
    assert.match(rep.textContent, /aucune étape ne tient en validation croisée/);
    const det = rep.querySelector('details');
    assert.match(det.querySelector('summary').textContent, /2 étapes, retenue : aucune/);
    const rows = [...det.querySelectorAll('tr')].map((tr) => [...tr.children].map((c) => c.textContent));
    assert.deepEqual(rows, [['Étape', 'Sur l\'historique', 'Validation croisée'], ['actuel ◀', '+0,0 %', '+0,0 %'], ['1', '+4,0 %', '-0,8 %'], ['2', '+12,0 %', '-3,2 %']]);
});

await test('calibrage : écarts en %, méthode sans changement signalée, application non recommandée confirmée à part', async () => {
    const st = JSON.parse(JSON.stringify(status));
    st.calibration = { running: true, progress: 21.6, at: '2026-10-20T12:00:00Z', report: {
        current: { gain: 0.25, hit10: 0.2, hit20: 0.3, events: 155, accounts: 13 }, proposed: { gain: 0.251, hit10: 0.2, hit20: 0.3 },
        cvDelta: 0.00075, ciLow: 0.0000, ciHigh: 0.002, recommended: false, verdict: 'négligeable', method: 'ChoiceModel', steps: [],
        methods: [{ method: 'ChoiceModel', name: 'Modèle de choix appris', gain: 0.251, cvDelta: 0.00075, ciLow: 0, ciHigh: 0.002, sufficiency: 'suffisant', recommended: false, noChange: false },
                  { method: 'Neural', name: 'Réseau de neurones (expérimental)', gain: 0.25, cvDelta: 0, ciLow: 0, ciHigh: 0, sufficiency: 'insuffisant', recommended: false, noChange: true }] } };
    const { w, d } = await open(cfg);
    const ajax = w.ApiClient.ajax;
    w.ApiClient.ajax = (o) => (o.url.split('?')[0] === '/RecoPlus/Status' ? Promise.resolve(st) : ajax(o));
    w.document.querySelector('#RecoPlusConfigPage').dispatchEvent(new w.Event('pageshow'));
    await wait(80);
    const t = d.getElementById('rpCalReport').textContent;
    assert.match(t, /calcul précédent/);
    assert.match(t, /\+0,3 %/);
    assert.match(t, /aucun changement appris/);
    assert.ok(!/\+0\.0008/.test(t), 'pas de valeur brute');
});

const EN = JSON.parse(readFileSync(join(root, 'src/RecoPlus.Engine/english.json'), 'utf8'));

// textes français de la page : nœuds texte et attributs du HTML, champs F, littéraux t('…') / tf('…'), tables de libellés
function pageTexts() {
    const keys = new Set();
    const add = (x, raw) => { x = raw ? String(x) : String(x).trim(); if (x && /[A-Za-zÀ-ÿ]/.test(x)) keys.add(x); };
    const dom = new JSDOM(HTML.replace(/<script>[\s\S]*?<\/script>/, '').replace(/<style>[\s\S]*?<\/style>/, ''));
    const doc = dom.window.document;
    const walker = doc.createTreeWalker(doc.body, dom.window.NodeFilter.SHOW_TEXT);
    let n; while ((n = walker.nextNode())) add(n.nodeValue);
    doc.querySelectorAll('[title],[placeholder],[aria-label],[data-tpl]').forEach((e) => ['title', 'placeholder', 'aria-label', 'data-tpl'].forEach((a) => e.getAttribute(a) && add(e.getAttribute(a))));
    const js = HTML.match(/<script>([\s\S]*?)<\/script>/)[1];
    const unq = (x) => x.replace(/\\'/g, "'");
    for (const f of fieldsOf(HTML)) { add(unq(f.label)); add(unq(f.tip)); if (f.type.startsWith('enum:')) unq(f.type).slice(5).split(',').forEach((o) => add(o.split('=')[1])); }
    for (const m of js.matchAll(/^\s*\['\w+',[^\n]*'[cel]',\s*'((?:[^'\\]|\\.)*)'/gm)) add(unq(m[1]));
    for (const m of js.matchAll(/\bt[f]?\(\s*'((?:[^'\\]|\\.)*)'/g)) add(unq(m[1]), true);
    for (const m of js.matchAll(/\bt[f]?\(\s*[^()']*\?\s*'((?:[^'\\]|\\.)*)'\s*:\s*'((?:[^'\\]|\\.)*)'/g)) { add(unq(m[1]), true); add(unq(m[2]), true); }
    for (const m of js.matchAll(/\[[\d.]+, '((?:[^'\\]|\\.)*)'\]/g)) add(unq(m[1]));
    for (const m of js.match(/var SOURCE_NAMES = \{([^}]*)\}/)[1].matchAll(/'((?:[^'\\]|\\.)*)'/g)) add(unq(m[1]));
    return keys;
}

await test('langue : chaque texte de la page a sa traduction anglaise (mêmes paramètres)', async () => {
    const keys = pageTexts();
    assert.ok(keys.size > 400, 'textes trouvés : ' + keys.size);
    const missing = [...keys].filter((k) => !EN[k]);
    assert.deepEqual(missing, []);
    for (const k of keys) {
        const ph = (x) => [...new Set((x.match(/\{[\w:]+\}/g) || []).map((p) => (p === '{title}' ? '{titre}' : p)))].sort().join();
        assert.equal(ph(EN[k]), ph(k), 'paramètres : ' + k);
    }
    // tout littéral français du script (hors commentaires, champs F et classes CSS) passe par le dictionnaire
    const js = HTML.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/ [^'\n]*$/gm, '');
    const body = js.replace(/var F = \[[\s\S]*?\n\s*\];/, '');
    const stray = [...body.matchAll(/'((?:[^'\\\n]|\\.)*)'/g)].map((m) => m[1].replace(/\\'/g, "'"))
        .filter((x) => /[a-zà-ÿ]{2,}[ '’][a-zà-ÿ«»]|[à-ÿ]/i.test(x) && !EN[x] && !/^(rp-|use strict|Playback Reporting)/.test(x));
    assert.deepEqual(stray, [], 'textes du script hors dictionnaire');
    // une option de liste sans libellé (virgule dans un libellé) donnerait « undefined »
    for (const f of fieldsOf(HTML).filter((x) => x.type.startsWith('enum:'))) {
        for (const o of f.type.slice(5).split(',')) { assert.equal(o.split('=').length, 2, f.key + ' : option mal formée « ' + o + ' »'); }
    }
});

await test('langue : page en anglais (réglage ou langue de Jellyfin), textes du serveur demandés en anglais', async () => {
    // textes d'état : rendus par le serveur dans la langue demandée
    const st = { ...status, secrets: { seerrApiKey: 'not set', webhookUrl: 'not set', webhookToken: 'not set' } };
    const { d, calls } = await open({ ...cfg, Language: 'English' }, { '/RecoPlus/I18n': { lang: 'en', dict: EN }, '/RecoPlus/Status': st });
    await wait(50);
    const ui = d.querySelector('.content-primary');
    assert.equal(d.querySelector('.rp-tab[data-tab="general"]').textContent, 'General');
    assert.equal(d.querySelector('#rpRecompute span').textContent, 'Recompute now');
    assert.match(d.getElementById('rp_c_Enabled').closest('label').textContent, /Plugin enabled/);
    assert.equal(d.getElementById('rp_c_HssSectionTitle').getAttribute('placeholder'), 'For you');
    assert.match(d.getElementById('rp_c_SourceFilms').closest('.rp-field').querySelector('label').textContent, /“Movies for you” row/);
    assert.match(d.getElementById('rp_c_SourceParceQue').closest('.rp-field').querySelector('label').textContent, /“Because you liked…” rows/);
    assert.equal(d.getElementById('rp_e_Mmr').closest('.rp-factor').querySelector('.rp-mult').textContent, '×1.00 · normal');
    assert.deepEqual([...d.getElementById('rp_c_Language').options].map((o) => o.textContent), ["Jellyfin's", 'French', 'English']);
    // plus aucun texte de la page resté en français
    const fr = [];
    const walker = d.createTreeWalker(ui, d.defaultView.NodeFilter.SHOW_TEXT, { acceptNode: (x) => (x.parentNode.nodeName === 'SCRIPT' || x.parentNode.nodeName === 'STYLE' ? 2 : 1) });
    let n; while ((n = walker.nextNode())) { const k = n.nodeValue.trim(); if (k && EN[k] && EN[k] !== k) fr.push(k); }
    ui.querySelectorAll('[title],[placeholder],[aria-label]').forEach((e) => ['title', 'placeholder', 'aria-label'].forEach((a) => { const v = e.getAttribute(a); if (v && EN[v] && EN[v] !== v) fr.push(v); }));
    assert.deepEqual(fr, []);
    const left = (ui.textContent.match(/.{0,60}(Sans effet|Par défaut :|Valeur évaluée).{0,60}/g) || []);
    assert.deepEqual(left, [], 'texte composé resté en français');
    // chaque appel à l'API porte la langue de la page
    assert.ok(calls.filter((u) => !u.startsWith('/RecoPlus/I18n')).every((u) => /[?&]lang=en(&|$)/.test(u)), calls.join(' '));
});

await test('langue : sans dictionnaire (serveur injoignable ou français), la page reste en français', async () => {
    const { d } = await open(cfg, { '/RecoPlus/I18n': { lang: 'fr', dict: {} } });
    assert.equal(d.querySelector('.rp-tab[data-tab="general"]').textContent, 'Général');
    assert.equal(d.getElementById('rp_c_HssSectionTitle').getAttribute('placeholder'), 'Pour toi');
});

console.log(failures ? `\n${failures} test(s) en échec` : '\ntous les tests passent');
process.exit(failures ? 1 : 0);
