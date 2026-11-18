// ==UserScript==
// @name         Faloop → HuntHop bridge
// @namespace    hunthop
// @version      0.3.1
// @description  Forwards Faloop's new-spawn alerts (in-page toasts, and desktop notifications if on) and their spawn points to the HuntHop Dalamud plugin on localhost.
// @match        https://faloop.app/*
// @run-at       document-start
// @sandbox      JavaScript
// @inject-into  page
// @grant        GM_xmlhttpRequest
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_registerMenuCommand
// @grant        GM_setClipboard
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
  // Spawn points from the live feed (key → spot) and spawns we alerted on (key → time); 1 h each.
  const KEEP_MS = 60 * 60 * 1000;
  const spots = new Map();
  const alerted = new Map();
  const prune = (map, now) => { for (const [k, v] of map) if (now - (v.at || v) > KEEP_MS) map.delete(k); };

  function send(alert, source) {
    const now = Date.now();
    for (const [k, t] of recent) if (now - t > DEDUP_MS) recent.delete(k);
    const key = `${alert.world}.${alert.mob}.${alert.instance}`;
    if (recent.has(key)) return;
    recent.set(key, now);
    alert.ts = now;
    // The spawn point is usually known by now (it comes with the first report): send it inside
    // the alert, so the plugin's alert line can already show it.
    if (spots.has(key)) {
      const s = spots.get(key).spot;
      alert.spot = { zone: s.zone, pois: s.pois, location: s.location };
    }
    console.info(LOG, `spawn (${source})`, alert);
    post('/hunt', alert);
    prune(alerted, now);
    alerted.set(key, now);
  }

  // A socket "spawn" / "spawn_location" report → { world, mob, instance, zone, pois, location }.
  function spotFromReport(data) {
    if (!data || !data.id || !data.data) return null;
    if (data.action !== 'spawn' && data.action !== 'spawn_location') return null;
    const d = data.data;
    const pois = Array.isArray(d.zonePoiIds) ? d.zonePoiIds.filter(Number.isInteger)
      : Number.isInteger(d.zonePoiId) ? [d.zonePoiId] : [];
    const location = typeof d.location === 'string' ? d.location : null;
    if (!pois.length && !location) return null;
    return {
      world: String(data.id.worldId || ''), mob: String(data.id.mobId || ''),
      instance: Number(data.id.zoneInstance) || 0,
      zone: typeof (d.zoneId2 || d.zoneId) === 'string' ? (d.zoneId2 || d.zoneId) : null,
      pois: pois.slice(0, 64), location,
    };
  }

  function onReport(data) {
    const spot = spotFromReport(data);
    if (!spot || !spot.world || !spot.mob) return;
    const now = Date.now();
    const key = `${spot.world}.${spot.mob}.${spot.instance}`;
    prune(spots, now);
    spots.set(key, { spot, at: now });
    if (alerted.has(key)) post('/spot', spot); // update for a spawn already in game chat
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

  // ── Source 3: Faloop's live data feed (socket.io on /messaging) ───────────
  // Spawn points come from mob "report" messages, e.g.
  //   {"type":"mob","subType":"report","data":{"action":"spawn","id":{"mobId":"sphatika",
  //    "worldId":"belias"},"data":{"zoneId2":"thavnair","zonePoiIds":[1074],…}}}
  // The feed carries every world; spots are only sent to the plugin for spawns we alerted on.
  // The last 30 mob messages are also kept for the "copy captured socket messages" menu item.
  const captured = [];
  function parseSocketFrame(frame) {
    // engine.io message "4" + socket.io event "2": 42[/namespace,][ackId]["message",{...}]
    if (typeof frame !== 'string' || !frame.startsWith('42')) return null;
    let rest = frame.slice(2);
    if (rest.startsWith('/')) {
      const comma = rest.indexOf(',');
      if (comma < 0) return null;
      rest = rest.slice(comma + 1);
    }
    rest = rest.replace(/^\d+/, '');
    try {
      const arr = JSON.parse(rest);
      return Array.isArray(arr) ? { event: arr[0], payload: arr[1] } : null;
    } catch (e) { return null; }
  }
  function captureFrames(raw, via) {
    if (typeof raw !== 'string') return;
    for (const frame of raw.split('\x1e')) { // polling batches packets with a record separator
      const msg = parseSocketFrame(frame);
      if (!msg || msg.event !== 'message' || !msg.payload || msg.payload.type !== 'mob') continue;
      const entry = { at: new Date().toISOString(), via, payload: msg.payload };
      captured.push(entry);
      if (captured.length > 30) captured.shift();
      if (msg.payload.subType === 'report') {
        try { onReport(msg.payload.data); } catch (e) { console.warn(LOG, e); }
      }
    }
  }
  const SOCKET_RE = /\/messaging\/socket\.io\//;
  if (win.WebSocket) {
    win.WebSocket = new Proxy(win.WebSocket, {
      construct(target, args) {
        const ws = Reflect.construct(target, args);
        try {
          if (SOCKET_RE.test(String(args[0]))) ws.addEventListener('message', ev => captureFrames(ev.data, 'ws'));
        } catch (e) { console.warn(LOG, e); }
        return ws;
      },
    });
  }
  if (win.XMLHttpRequest) {
    const xhrOpen = win.XMLHttpRequest.prototype.open;
    win.XMLHttpRequest.prototype.open = function (method, url) {
      try {
        if (SOCKET_RE.test(String(url))) this.addEventListener('load', () => captureFrames(this.responseText, 'polling'));
      } catch (e) { console.warn(LOG, e); }
      return xhrOpen.apply(this, arguments);
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
  GM_registerMenuCommand('HuntHop: copy captured socket messages', () => {
    GM_setClipboard(JSON.stringify(captured, null, 2), 'text');
    alert(`Copied ${captured.length} captured Faloop mob message(s) to the clipboard.`);
  });
  GM_registerMenuCommand('HuntHop: send test (Sansheya @ a world you type)', () => {
    const w = prompt('World name, e.g. aegis');
    if (w) {
      recent.delete(`${slug(w)}.sansheya.0`);
      send({ world: slug(w), mob: 'sansheya', instance: 0, title: w, body: 'Test from the userscript' }, 'test');
    }
  });

  console.info(LOG, `hooked (toasts + desktop + spawn points); forwarding ${cfg.forward && cfg.token ? 'ON' : 'OFF (no token yet)'} → localhost:${cfg.port}`);
})();
