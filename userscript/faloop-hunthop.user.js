// ==UserScript==
// @name         Faloop → HuntHop bridge
// @namespace    hunthop
// @version      0.2.0
// @description  Forwards Faloop's new-spawn alerts (in-page toasts, and desktop notifications if on) to the HuntHop Dalamud plugin on localhost.
// @match        https://faloop.app/*
// @run-at       document-start
// @sandbox      JavaScript
// @inject-into  page
// @grant        GM_xmlhttpRequest
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_registerMenuCommand
// @grant        unsafeWindow
// @connect      localhost
// ==/UserScript==

/*
 * Sources (bundle main.e61aaf46d6b9ab26.js; both fire only after Faloop's own world/expansion filters):
 *
 * 1. In-page toast (default source, no browser permission needed).
 *    react-toastify element .Toastify__toast with:
 *      header span   = world name, upper-cased, "AEGIS" or "AEGIS (ELEMENTAL)" for another DC
 *      svg[data-icon="bullhorn"]  = spawn / SS event begun / scheduled spawn released
 *      span.lead.fw-bold          = mob name (English UI: slug = name lower-cased, non-alnum → "_")
 *      span.badge                 = instance number, if any
 *    Needs Faloop Settings → Notifications → website toasts ON (Faloop's default).
 *    Mob names are only mapped for the English UI.
 *
 * 2. Desktop notification (optional backup). registration.showNotification(<world>, {tag:
 *    "newSpawn.<world>.<mob>.<instance>"}). Only fires if Faloop's desktop notifications are ON and
 *    the browser granted permission.
 *
 * The same spawn from both sources is sent once. Nothing is sent anywhere except http://localhost:<port>.
 */
(function () {
  'use strict';

  const LOG = '[HuntHop]';
  const TAG_RE = /^newSpawn\.([a-z0-9_-]+)\.([a-z0-9_-]+)\.(\d+)$/;
  const PING_MS = 60 * 1000;
  const DEDUP_MS = 2 * 60 * 1000;

  const cfg = {
    get port() { return Number(GM_getValue('port', 47821)); },
    get token() { return String(GM_getValue('token', '')); },
    get forward() { return GM_getValue('forward', true); },
    get popups() { return GM_getValue('popups', true); },
  };

  const slug = s => String(s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '');

  function post(path, payload) {
    if (!cfg.forward || !cfg.token) return;
    GM_xmlhttpRequest({
      method: 'POST',
      url: `http://localhost:${cfg.port}${path}`,
      headers: { 'Content-Type': 'application/json', 'X-HuntHop-Token': cfg.token },
      data: payload ? JSON.stringify(payload) : '',
      timeout: 3000,
      onload: r => { if (r.status !== 204) console.warn(LOG, `${path} → HTTP ${r.status}`); },
      onerror: () => console.warn(LOG, `${path} → plugin not reachable on port ${cfg.port} (game closed?)`),
      ontimeout: () => console.warn(LOG, `${path} → timeout`),
    });
  }

  const recent = new Map();
  function send(alert, source) {
    const now = Date.now();
    for (const [k, t] of recent) if (now - t > DEDUP_MS) recent.delete(k);
    const key = `${alert.world}.${alert.mob}.${alert.instance}`;
    if (recent.has(key)) return;
    recent.set(key, now);
    alert.ts = now;
    console.info(LOG, `spawn (${source})`, alert);
    post('/hunt', alert);
  }

  // ── Source 1: in-page toasts ──────────────────────────────────────────────
  function parseToast(el) {
    if (!el.querySelector('svg[data-icon="bullhorn"]')) return null;
    const headerSpan = el.querySelector('.fw-bold.text-dark > span');
    const mobSpan = el.querySelector('span.lead.fw-bold');
    if (!headerSpan || !mobSpan) return null;
    const world = slug(headerSpan.textContent.split(' (')[0]);
    const mob = slug(mobSpan.textContent);
    if (!world || !mob) return null;
    const badge = el.querySelector('span.badge');
    const instance = badge ? parseInt(badge.textContent, 10) || 0 : 0;
    const bodyEl = el.querySelector('.w-100') || el;
    return {
      world, mob, instance,
      title: headerSpan.textContent.trim(),
      body: (bodyEl.textContent || '').replace(/\s+/g, ' ').trim(),
    };
  }

  const seenToasts = new WeakSet();
  function handleToast(el, retry) {
    if (seenToasts.has(el)) return;
    const alert = parseToast(el);
    if (alert) { seenToasts.add(el); send(alert, 'toast'); return; }
    if (retry) setTimeout(() => handleToast(el, false), 150); // content may land a tick later
  }

  function scan(node) {
    if (!(node instanceof Element)) return;
    if (node.classList.contains('Toastify__toast')) handleToast(node, true);
    node.querySelectorAll('.Toastify__toast').forEach(t => handleToast(t, true));
  }

  const observer = new MutationObserver(muts => {
    for (const m of muts) m.addedNodes.forEach(scan);
  });
  observer.observe(document, { childList: true, subtree: true });

  // ── Source 2: desktop notifications (backup) ──────────────────────────────
  const win = typeof unsafeWindow !== 'undefined' ? unsafeWindow : window;
  const proto = win.ServiceWorkerRegistration && win.ServiceWorkerRegistration.prototype;
  if (proto && typeof proto.showNotification === 'function') {
    const original = proto.showNotification;
    proto.showNotification = function (title, options) {
      try {
        const m = TAG_RE.exec(options && typeof options.tag === 'string' ? options.tag : '');
        if (m) {
          send({
            world: m[1], mob: m[2], instance: Number(m[3]),
            title: String(title || ''), body: String((options && options.body) || ''),
          }, 'desktop');
        }
      } catch (e) { console.warn(LOG, e); }
      if (!cfg.popups) return Promise.resolve();
      return original.apply(this, arguments);
    };
  }

  // Health ping so the plugin can warn when the tab is closed or the script stops.
  setInterval(() => post('/ping'), PING_MS);
  setTimeout(() => post('/ping'), 3000);

  // ── Menu ──────────────────────────────────────────────────────────────────
  GM_registerMenuCommand('HuntHop: set token', () => {
    const t = prompt('Paste the token from /hunthop → Userscript connection → Copy token', cfg.token);
    if (t !== null) { GM_setValue('token', t.trim()); post('/ping'); }
  });
  GM_registerMenuCommand('HuntHop: set port', () => {
    const p = prompt('Port (must match the plugin)', String(cfg.port));
    if (p !== null && /^\d+$/.test(p.trim())) GM_setValue('port', Number(p.trim()));
  });
  GM_registerMenuCommand('HuntHop: toggle forwarding', () => {
    GM_setValue('forward', !cfg.forward);
    alert(`Forwarding to HuntHop is now ${cfg.forward ? 'ON' : 'OFF (log only)'}`);
  });
  GM_registerMenuCommand('HuntHop: toggle Windows pop-ups', () => {
    GM_setValue('popups', !cfg.popups);
    alert(`Faloop's Windows pop-ups are now ${cfg.popups ? 'shown' : 'hidden (still forwarded)'}`);
  });
  GM_registerMenuCommand('HuntHop: send test (Sansheya @ a world you type)', () => {
    const w = prompt('World name, e.g. aegis');
    if (w) {
      recent.delete(`${slug(w)}.sansheya.0`);
      send({ world: slug(w), mob: 'sansheya', instance: 0, title: w, body: 'Test from the userscript' }, 'test');
    }
  });

  console.info(LOG, `hooked (toasts + desktop); forwarding ${cfg.forward && cfg.token ? 'ON' : 'OFF (no token yet)'} → localhost:${cfg.port}`);
})();
