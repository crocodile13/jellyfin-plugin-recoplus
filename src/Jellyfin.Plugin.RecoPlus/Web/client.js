/* Reco+ — script de l'accueil de l'interface web.
   - Rangées « Parce que tu as aimé … » : le titre générique déclaré à Home Screen Sections est remplacé par celui du compte.
   - Affichages : une carte d'une rangée Reco+ visible au moins une seconde, onglet au premier plan, compte connecté,
     est notée « vue » (une fois par jour) ; une carte ouverte est notée « ouverte ». Rien d'autre n'est envoyé.
   Titres hors bibliothèque (mode étendu) :
   - Home Screen Sections présent : les titres sont mélangés DANS la rangée « Pour toi » (ordre et place décidés par HSS).
   - Jellyfin Enhanced présent et relié à Seerr : cartes, fiche et bouton « Demander » de Jellyfin Enhanced, avec l'état
     Seerr du moment (un titre demandé ou arrivé entre-temps n'est plus proposé).
   - Sinon : cartes Reco+ (demande via /RecoPlus/Request), et sans HSS une rangée à part, sous la première.
   Tout le texte est inséré en textContent (aucun HTML venant du serveur). */
(function () {
    'use strict';
    if (window.__recoPlusClient) { return; }
    window.__recoPlusClient = true;

    var ROW_ID = 'recoplus-extended-row';
    var EXT = 'recoplus-ext';
    var HSS_SECTION = 'RecoPlusPourToi';
    var SECTION_PREFIX = 'RecoPlus';
    var SEEN_MS = 1000;
    var FLUSH_MS = 15000;
    var TTL = 5 * 60 * 1000;
    var cache = { at: 0, user: null, data: null, pending: null };

    function api() { return window.ApiClient; }
    function je() { return window.JellyfinEnhanced; }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text !== undefined) { e.textContent = text; }
        return e;
    }

    function load() {
        var a = api();
        if (!a || !a.getCurrentUserId || !a.getCurrentUserId()) { return Promise.resolve(null); }
        var uid = a.getCurrentUserId();
        if (cache.data && cache.user === uid && Date.now() - cache.at < TTL) { return Promise.resolve(cache.data); }
        if (cache.pending) { return cache.pending; }
        cache.pending = a.getJSON(a.getUrl('RecoPlus/Extended')).then(function (d) {
            cache = { at: Date.now(), user: uid, data: d, pending: null };
            return d;
        }, function () { cache.pending = null; return null; });
        return cache.pending;
    }

    // ---- cartes Jellyfin Enhanced

    function jeReady() {
        var j = je();
        return !!(j && j.jellyseerrUI && typeof j.jellyseerrUI.createJellyseerrCard === 'function'
            && j.jellyseerrAPI && typeof j.jellyseerrAPI.checkUserStatus === 'function'
            && typeof j.jellyseerrAPI.fetchMovieDetails === 'function' && typeof j.jellyseerrAPI.fetchTvShowDetails === 'function');
    }

    // Seerr : 2 en attente, 3 en cours, 4 partiellement disponible, 5 disponible, 6 sur liste noire
    function stillProposable(details) {
        var s = details && details.mediaInfo ? details.mediaInfo.status : 0;
        return !(s >= 2 && s <= 6);
    }

    function pool(items, size, fn) {
        var out = new Array(items.length);
        var next = 0;
        function worker() {
            if (next >= items.length) { return Promise.resolve(); }
            var i = next++;
            return Promise.resolve(fn(items[i])).then(function (r) { out[i] = r; }, function () { out[i] = null; }).then(worker);
        }
        var ws = [];
        for (var k = 0; k < Math.min(size, items.length); k++) { ws.push(worker()); }
        return Promise.all(ws).then(function () { return out; });
    }

    function jeCards(items) {
        var j = je();
        return j.jellyseerrAPI.checkUserStatus().then(function (st) {
            if (!st || !st.active) { return null; }
            return pool(items, 4, function (it) {
                var f = it.MediaType === 'tv' ? j.jellyseerrAPI.fetchTvShowDetails : j.jellyseerrAPI.fetchMovieDetails;
                return f(it.TmdbId);
            }).then(function (all) {
                var cards = [];
                all.forEach(function (d, i) {
                    if (!d || !stillProposable(d)) { return; }
                    var item = Object.assign({}, d, { mediaType: items[i].MediaType });
                    var c = j.jellyseerrUI.createJellyseerrCard(item, !!st.active, !!st.userFound);
                    if (!c) { return; }
                    c.classList.add(EXT);
                    c.setAttribute('data-rp-id', 'tmdb:' + (items[i].MediaType === 'tv' ? 'tv' : 'movie') + ':' + items[i].TmdbId);
                    c.__rpAfter = items[i].After;
                    if (items[i].Because) { c.title = 'Parce que tu as aimé ' + items[i].Because; }
                    cards.push(c);
                });
                return cards;
            });
        });
    }

    // ---- cartes Reco+ (sans Jellyfin Enhanced)

    function poster(p) {
        return p && /^\/[A-Za-z0-9._-]+$/.test(p) ? 'https://image.tmdb.org/t/p/w300' + p : '';
    }

    function openItem(d, it) {
        if (d.seerrUrl && /^https?:\/\//i.test(d.seerrUrl)) {
            window.open(d.seerrUrl + '/' + (it.MediaType === 'tv' ? 'tv' : 'movie') + '/' + encodeURIComponent(it.TmdbId), '_blank', 'noopener');
        }
    }

    function request(btn, it) {
        var a = api();
        btn.disabled = true;
        btn.textContent = '…';
        a.ajax({
            type: 'POST',
            url: a.getUrl('RecoPlus/Request'),
            data: JSON.stringify({ MediaType: it.MediaType, TmdbId: it.TmdbId }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function () {
            btn.textContent = '✓ demandé';
            cache.at = 0;
        }, function () {
            btn.disabled = false;
            btn.textContent = 'Demander';
            btn.title = 'Demande impossible';
        });
    }

    function ownCard(d, it) {
        var c = el('div', 'card overflowPortraitCard card-hoverable ' + EXT);
        c.setAttribute('data-rp-id', 'tmdb:' + (it.MediaType === 'tv' ? 'tv' : 'movie') + ':' + it.TmdbId);
        c.__rpAfter = it.After;
        var box = el('div', 'cardBox cardBox-bottompadded');
        var sc = el('div', 'cardScalable');
        sc.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));
        var img = el('div', 'cardImageContainer coveredImage cardContent');
        img.style.cursor = 'pointer';
        var p = poster(it.PosterPath);
        if (p) { img.style.backgroundImage = 'url("' + p + '")'; }
        img.title = it.Because ? ('Parce que tu as aimé ' + it.Because) : (it.Name || '');
        img.addEventListener('click', function () { openItem(d, it); });
        sc.appendChild(img);
        box.appendChild(sc);
        box.appendChild(el('div', 'cardText cardTextCentered cardText-first', it.Name || ''));
        var sub = (it.Year ? it.Year : '') + (it.Vote ? ' · ★ ' + Number(it.Vote).toFixed(1) : '') + (it.MediaType === 'tv' ? ' · série' : '');
        box.appendChild(el('div', 'cardText cardTextCentered cardText-secondary', sub));
        if (d.canRequest) {
            var b = el('button', 'raised emby-button', 'Demander');
            b.type = 'button';
            b.style.cssText = 'display:block;margin:.3em auto 0;padding:.3em .9em;font-size:.85em;';
            b.addEventListener('click', function (ev) { ev.stopPropagation(); request(b, it); });
            box.appendChild(b);
        }
        c.appendChild(box);
        return c;
    }

    function buildCards(d) {
        var items = (d.items || []).slice();
        if (!items.length) { return Promise.resolve([]); }
        var fallback = function () { return items.map(function (it) { return ownCard(d, it); }); };
        if (!jeReady()) { return Promise.resolve(fallback()); }
        return jeCards(items).then(function (c) { return c || fallback(); }, fallback);
    }

    // ---- mélange dans la rangée « Pour toi » (Home Screen Sections)

    // Positions des titres hors bibliothèque : après `skip` cartes de tête, un toutes les `every` cartes.
    function slots(libCount, extCount, every, skip) {
        var out = [];
        var step = Math.max(2, every | 0) - 1;
        var n = 0;
        for (var i = Math.max(0, skip | 0); i < libCount && out.length < extCount; i++) {
            n++;
            if (n % step === 0) { out.push(i); }
        }
        return out;
    }

    // Ordre final du mélange libre : un titre hors bibliothèque classé derrière n titres de la bibliothèque vient juste
    // après le n-ième ; classé derrière toute la rangée, il n'est pas montré (c'est le classement, rien de plus).
    function freeOrder(lib, ext, skip) {
        skip = Math.max(0, skip | 0);
        var byAfter = {};
        ext.forEach(function (c) {
            var a = c.__rpAfter;
            if (typeof a !== 'number' || a < 0) { return; }
            (byAfter[a] = byAfter[a] || []).push(c);
        });
        var seq = [];
        lib.forEach(function (l, i) {
            if (i >= skip) { (byAfter[i - skip] || []).forEach(function (c) { seq.push(c); }); }
            seq.push(l);
        });
        return seq;
    }

    function mix(container, d) {
        var isCard = function (c) { return c.classList.contains('card') && !c.classList.contains(EXT); };
        var first = Array.prototype.find.call(container.children, isCard);
        // déjà traitée pour ce rendu de HSS (même si aucun titre n'a trouvé sa place) : rien à refaire
        if (!first || container.__recoPlusBusy || container.__recoPlusDoneFor === first || container.querySelector('.' + EXT)) { return; }
        container.__recoPlusBusy = true;
        buildCards(d).then(function (ext) {
            container.__recoPlusBusy = false;
            if (!container.isConnected || container.querySelector('.' + EXT)) { return; }
            var lib = Array.prototype.filter.call(container.children, isCard);
            container.__recoPlusDoneFor = lib[0];
            if (!ext.length) { return; }
            if (d.mixMode === 'score') {
                // libre : chaque titre après les `After` premiers titres de la bibliothèque (demandes en tête exclues)
                var seq = freeOrder(lib, ext, d.skip);
                seq.forEach(function (n) { container.appendChild(n); });
                return;
            }
            // insertion de la fin vers le début : les positions calculées restent valables
            var at = slots(lib.length, ext.length, d.mixEvery, d.skip);
            for (var k = at.length - 1; k >= 0; k--) { lib[at[k]].after(ext[k]); }
        }, function () { container.__recoPlusBusy = false; });
    }

    // ---- rangée à part (sans Home Screen Sections)

    function standalone(sections, d) {
        buildCards(d).then(function (cards) {
            if (!cards.length || document.getElementById(ROW_ID)) { return; }
            var sec = el('div', 'verticalSection');
            sec.id = ROW_ID;
            var head = el('div', 'sectionTitleContainer sectionTitleContainer-cards padded-left');
            head.appendChild(el('h2', 'sectionTitle sectionTitle-cards', d.title || 'À découvrir pour toi'));
            sec.appendChild(head);
            var row = el('div', 'itemsContainer padded-left padded-right');
            // défilement horizontal sans barre visible (molette, glisser, clavier)
            row.style.cssText = 'display:flex;flex-wrap:nowrap;overflow-x:auto;overflow-y:hidden;scrollbar-width:none;';
            cards.forEach(function (c) { row.appendChild(c); });
            sec.appendChild(row);
            var first = sections.firstElementChild;
            if (first && first.nextSibling) { sections.insertBefore(sec, first.nextSibling); } else { sections.appendChild(sec); }
        });
    }

    function homeSections() {
        return document.querySelector('#homeTab .sections, #indexPage .tabContent[data-index="0"] .sections');
    }

    // ---- titres des rangées « Parce que tu as aimé … »

    function renameRows(d) {
        var t = d && d.rowTitles;
        if (!t) { return; }
        Object.keys(t).forEach(function (id) {
            if (!/^RecoPlus[A-Za-z0-9]+$/.test(id)) { return; }
            var h = document.querySelector('.verticalSection.' + id + ' .sectionTitle');
            if (h && h.textContent !== t[id]) { h.textContent = t[id]; }
        });
    }

    // ---- affichages (vu à l'écran / ouvert)

    var track = { on: false, day: '', sent: {}, shown: [], opened: [], timers: new WeakMap(), io: null };

    function today() { return new Date().toISOString().slice(0, 10); }

    function cardId(card) {
        var id = card.getAttribute('data-rp-id') || card.getAttribute('data-id') || '';
        return /^(?:[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}|tmdb:(?:movie|tv):[0-9]{1,9})$/.test(id) ? id.replace(/-/g, '').toLowerCase() : null;
    }

    function recoSection(node) {
        var s = node && node.closest ? node.closest('.verticalSection') : null;
        if (!s) { return null; }
        if (s.id === ROW_ID) { return s; }
        for (var i = 0; i < s.classList.length; i++) {
            if (s.classList[i].indexOf(SECTION_PREFIX) === 0) { return s; }
        }
        return null;
    }

    function markShown(card) {
        var id = cardId(card);
        if (!id) { return; }
        var d = today();
        if (track.day !== d) { track.day = d; track.sent = {}; }
        if (track.sent[id]) { return; }
        track.sent[id] = true;
        track.shown.push(id);
    }

    function flush() {
        var a = api();
        if (!a || !track.on || (!track.shown.length && !track.opened.length)) { return; }
        var body = { Shown: track.shown.splice(0, 100), Opened: track.opened.splice(0, 100) };
        a.ajax({ type: 'POST', url: a.getUrl('RecoPlus/Impressions'), data: JSON.stringify(body), contentType: 'application/json' })
            .then(null, function () { /* perdu : sans importance */ });
    }

    function observer() {
        if (track.io || typeof IntersectionObserver !== 'function') { return track.io; }
        track.io = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) {
                var card = e.target;
                var t = track.timers.get(card);
                if (t) { clearTimeout(t); track.timers.delete(card); }
                if (e.isIntersecting && e.intersectionRatio >= 0.5) {
                    track.timers.set(card, setTimeout(function () {
                        track.timers.delete(card);
                        if (document.visibilityState === 'visible' && card.isConnected) { markShown(card); }
                    }, SEEN_MS));
                }
            });
        }, { threshold: [0, 0.5, 1] });
        return track.io;
    }

    function watchCards() {
        if (!track.on) { return; }
        var io = observer();
        if (!io) { return; }
        var cards = document.querySelectorAll('.verticalSection[class*="' + SECTION_PREFIX + '"] .card, #' + ROW_ID + ' .card');
        Array.prototype.forEach.call(cards, function (c) {
            if (c.__rpObserved || !recoSection(c)) { return; }
            c.__rpObserved = true;
            io.observe(c);
        });
    }

    document.addEventListener('click', function (ev) {
        if (!track.on) { return; }
        var card = ev.target && ev.target.closest ? ev.target.closest('.card') : null;
        if (!card || !recoSection(card)) { return; }
        var id = cardId(card);
        if (id) { track.opened.push(id); flush(); }
    }, true);
    document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden') { flush(); } });
    setInterval(flush, FLUSH_MS);

    var busy = false;
    function check() {
        if (busy) { return; }
        var sections = homeSections();
        if (!sections || !sections.children.length) { return; }
        busy = true;
        load().then(function (d) {
            busy = false;
            if (!d) { return; }
            renameRows(d);
            track.on = !!d.track;
            watchCards();
            if (!d.homeRow || !d.items || !d.items.length) { return; }
            if (d.hss) {
                var c = document.querySelector('.verticalSection.' + HSS_SECTION + ' .itemsContainer');
                if (c) { mix(c, d); }
            } else if (!document.getElementById(ROW_ID)) {
                var s = homeSections();
                if (s && s.children.length) { standalone(s, d); }
            }
        }, function () { busy = false; });
    }

    window.__recoPlusSlots = slots; // pour les tests
    window.__recoPlusFreeOrder = freeOrder;
    window.__recoPlusCardId = cardId;

    var style = el('style');
    style.textContent = '#' + ROW_ID + ' .itemsContainer::-webkit-scrollbar{display:none}';
    (document.head || document.documentElement).appendChild(style);

    var scheduled = false;
    new MutationObserver(function () {
        if (scheduled) { return; }
        scheduled = true;
        setTimeout(function () { scheduled = false; check(); }, 400);
    }).observe(document.documentElement, { childList: true, subtree: true });
    setTimeout(check, 0); // accueil déjà affiché au chargement du script
})();
