/* Reco+ — rangée « À découvrir pour toi » (mode étendu) sur l'accueil de l'interface web.
   Titres absents de la bibliothèque : clic = fiche (Jellyfin Enhanced) ou page Jellyseerr ; bouton = demande au nom
   de l'utilisateur. Tout le texte est inséré en textContent (aucun HTML venant du serveur). */
(function () {
    'use strict';
    if (window.__recoPlusClient) { return; }
    window.__recoPlusClient = true;

    var ROW_ID = 'recoplus-extended-row';
    var cache = { at: 0, user: null, data: null, pending: null };
    var TTL = 5 * 60 * 1000;

    function api() { return window.ApiClient; }

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

    function poster(p) {
        return p ? 'https://image.tmdb.org/t/p/w300' + p : '';
    }

    function openItem(d, it) {
        var je = window.JellyfinEnhanced;
        if (je && je.jellyseerrMoreInfo && typeof je.jellyseerrMoreInfo.open === 'function') {
            je.jellyseerrMoreInfo.open(it.TmdbId, it.MediaType);
            return;
        }
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
        }, function (err) {
            btn.disabled = false;
            btn.textContent = 'Demander';
            var m = 'Demande impossible';
            try { if (err && err.json) { err.json().then(function (j) { btn.title = j.message || m; }); } } catch (e) { /* rien */ }
            btn.title = m;
        });
    }

    function card(d, it) {
        var c = el('div', 'card overflowPortraitCard card-hoverable');
        c.style.cssText = 'display:inline-block;vertical-align:top;';
        var box = el('div', 'cardBox cardBox-bottompadded');
        var sc = el('div', 'cardScalable');
        sc.appendChild(el('div', 'cardPadder cardPadder-overflowPortrait'));
        var img = el('div', 'cardImageContainer coveredImage cardContent');
        img.style.cursor = 'pointer';
        var p = poster(it.PosterPath);
        if (p) { img.style.backgroundImage = 'url("' + p.replace(/["\\\n\r]/g, '') + '")'; }
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

    function render(sections, d) {
        var old = document.getElementById(ROW_ID);
        if (old) { old.remove(); }
        if (!d || !d.homeRow || !d.items || !d.items.length) { return; }
        var sec = el('div', 'verticalSection');
        sec.id = ROW_ID;
        var head = el('div', 'sectionTitleContainer sectionTitleContainer-cards padded-left');
        head.appendChild(el('h2', 'sectionTitle sectionTitle-cards', d.title || 'À découvrir pour toi'));
        sec.appendChild(head);
        var row = el('div', 'itemsContainer padded-left padded-right');
        row.style.cssText = 'display:flex;flex-wrap:nowrap;overflow-x:auto;gap:0;scroll-snap-type:x proximity;';
        d.items.forEach(function (it) { row.appendChild(card(d, it)); });
        sec.appendChild(row);
        // après la première rangée (souvent « Reprendre »), sinon en tête
        var first = sections.firstElementChild;
        if (first && first.nextSibling) { sections.insertBefore(sec, first.nextSibling); } else { sections.appendChild(sec); }
    }

    var busy = false;
    function check() {
        if (busy) { return; }
        var sections = document.querySelector('#homeTab .sections, #indexPage .tabContent[data-index="0"] .sections');
        if (!sections || !sections.children.length || document.getElementById(ROW_ID)) { return; }
        busy = true;
        load().then(function (d) {
            busy = false;
            if (document.getElementById(ROW_ID)) { return; }
            var s = document.querySelector('#homeTab .sections, #indexPage .tabContent[data-index="0"] .sections');
            if (s && s.children.length) { render(s, d); }
        }, function () { busy = false; });
    }

    var scheduled = false;
    new MutationObserver(function () {
        if (scheduled) { return; }
        scheduled = true;
        setTimeout(function () { scheduled = false; check(); }, 400);
    }).observe(document.documentElement, { childList: true, subtree: true });
})();
