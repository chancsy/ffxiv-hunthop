// Tests the userscript's parsing of Faloop alerts under jsdom. No network: requests are captured.
//
//   cd tests/userscript && npm install && npm test
//
// The toast markup below mirrors Faloop's react-toastify toast (bundle main.e61aaf46d6b9ab26.js,
// functions aR / dR / uR). If Faloop changes its markup, update toast() here first, then the
// selectors in the userscript, and rerun.
const path = require('path');
const fs = require('fs');
const { JSDOM } = require('jsdom');

const src = fs.readFileSync(path.join(__dirname, '..', '..', 'userscript', 'faloop-hunthop.user.js'), 'utf8');
const TOKEN = 'test-token';

const dom = new JSDOM('<!doctype html><html><head></head><body><div class="Toastify"></div></body></html>',
  { runScripts: 'outside-only', url: 'https://faloop.app/' });
const w = dom.window;
const sent = [];
const store = { token: TOKEN };
w.GM_getValue = (k, d) => (k in store ? store[k] : d);
w.GM_setValue = (k, v) => { store[k] = v; };
const menu = {};
let clipboard = null;
w.GM_registerMenuCommand = (name, fn) => { menu[name] = fn; };
w.GM_setClipboard = text => { clipboard = text; };
w.alert = () => {};
// Minimal WebSocket stand-in: the userscript wraps window.WebSocket in a Proxy.
w.WebSocket = class extends w.EventTarget { constructor(url) { super(); this.url = url; } };
w.GM_xmlhttpRequest = o => { sent.push(o); if (o.onload) o.onload({ status: 204 }); };
w.ServiceWorkerRegistration = class { showNotification() { return Promise.resolve(); } };
w.unsafeWindow = w;
w.console.info = () => {};
w.eval(src);

const toast = (world, mob, instance, icon = 'bullhorn', verb = 'has spawned!') => `
<div class="Toastify__toast Toastify__toast--info"><div class="Toastify__toast-body">
 <div class="d-flex flex-column h-100">
  <div class="d-none d-sm-block fw-bold text-dark ps-1"><svg data-icon="globe"></svg><span>${world}</span></div>
  <div class="d-block d-sm-none fw-bold text-dark ps-1 small"><svg data-icon="globe"></svg><span>${world}</span></div>
  <div class="d-flex"><div><svg data-icon="${icon}"></svg></div>
   <div class="w-100"><div><span class="lead fw-bold text-small-caps">${mob}</span>${instance ? `<span class="badge rounded-pill bg-info ms-1">${instance}</span>` : ''} ${verb}</div></div>
  </div></div></div></div>`;

const container = w.document.querySelector('.Toastify');
const add = html => {
  const d = w.document.createElement('div');
  d.className = 'Toastify__toast-container';
  d.innerHTML = html;
  container.appendChild(d);
};
const tick = () => new Promise(r => setTimeout(r, 300));
const hunts = () => sent.filter(s => s.url.endsWith('/hunt'));
const last = () => JSON.parse(hunts().slice(-1)[0].data);

let failed = 0;
const check = (name, cond) => {
  if (!cond) failed++;
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}`);
};

(async () => {
  add(toast('AEGIS', 'Sansheya', 0));
  await tick();
  check('spawn toast forwarded', hunts().length === 1 && last().world === 'aegis' && last().mob === 'sansheya' && last().instance === 0);

  add(toast('TONBERRY (ELEMENTAL)', 'Burfurlur the Canny', 2));
  await tick();
  check('other-DC header + instance + multi-word name', hunts().length === 2 && last().world === 'tonberry' && last().mob === 'burfurlur_the_canny' && last().instance === 2);

  add(toast('AEGIS', 'Narrow-rift', 0));
  await tick();
  check('hyphenated name -> narrow_rift', last().mob === 'narrow_rift');

  add(toast('AEGIS', 'Sansheya', 0, 'skull-crossbones', 'was just killed.'));
  await tick();
  check('death toast ignored', hunts().length === 3);

  add(toast('AEGIS', 'Sansheya', 0));
  await tick();
  check('same spawn again within 2 min: deduplicated', hunts().length === 3);

  await new w.ServiceWorkerRegistration().showNotification('Aegis', { tag: 'newSpawn.aegis.sansheya.0' });
  await tick();
  check('desktop copy of a toasted spawn: deduplicated', hunts().length === 3);

  await new w.ServiceWorkerRegistration().showNotification('Gaia', { tag: 'newSpawn.alexander.ker.0' });
  await tick();
  check('desktop-only spawn forwarded', hunts().length === 4 && last().world === 'alexander' && last().mob === 'ker');

  // Socket capture (0.2.1): socket.io frames on Faloop's /messaging socket.
  const ws = new w.WebSocket('wss://faloop.app/messaging/socket.io/?EIO=4&transport=websocket');
  const send = data => ws.dispatchEvent(new w.MessageEvent('message', { data }));
  send('42/legacy,["message",{"type":"mob","subType":"report","data":{"action":"spawn_location","id":{"mobId":"sansheya","worldId":"aegis","zoneInstance":0},"data":{"zonePoiId":1217}}}]');
  send('42/legacy,17["message",{"type":"mob","subType":"report","data":{"action":"death"}}]'); // with ack id
  send('42/legacy,["message",{"type":"user","subType":"x"}]'); // not a mob message
  send('3'); // engine.io pong
  const other = new w.WebSocket('wss://example.com/socket');
  other.dispatchEvent(new w.MessageEvent('message', { data: '42["message",{"type":"mob"}]' }));
  menu['HuntHop: copy captured socket messages']();
  const cap = JSON.parse(clipboard);
  check('socket capture: 2 mob messages kept, others ignored', cap.length === 2 && cap.every(c => c.via === 'ws' && c.payload.type === 'mob'));
  check('socket capture: payload intact', cap[0].payload.data.id.worldId === 'aegis' && cap[0].payload.data.data.zonePoiId === 1217);
  check('socket capture: no extra alerts', hunts().length === 4);
  const spotPosts = () => sent.filter(s => s.url.endsWith('/spot')).map(s => JSON.parse(s.data));
  check('spot update for an alerted spawn (aegis.sansheya) forwarded', spotPosts().length === 1 && spotPosts()[0].pois[0] === 1217);

  // Spawn points (0.3.0), using the real message captured on 2026-11-18 (Sphatika @ Belias).
  const real = '42/legacy,["message",{"type":"mob","subType":"report","data":{"action":"spawn","id":{"mobId":"sphatika","worldId":"belias"},"data":{"zoneId2":"thavnair","zonePoiIds":[1074],"timestamp":"2026-11-18T17:21:49.295Z","window":1,"stage":null}}}]';
  send(real);
  check('spawn point for a not-yet-alerted spawn is held, not sent', spotPosts().length === 1);
  add(toast('BELIAS (LIGHT)', 'Sphatika', 0));
  await tick();
  const a1 = last();
  check('toast alert carries the held spawn point inside it (one request)',
    a1.world === 'belias' && a1.mob === 'sphatika' && a1.spot && a1.spot.zone === 'thavnair'
    && a1.spot.pois.length === 1 && a1.spot.pois[0] === 1074 && a1.spot.location === null && spotPosts().length === 1);
  check('an alert without a known spawn point has no spot field', JSON.parse(hunts()[0].data).spot === undefined);
  send('42/legacy,["message",{"type":"mob","subType":"report","data":{"action":"spawn_location","id":{"mobId":"sphatika","worldId":"belias"},"data":{"zonePoiId":1075}}}]');
  check('later spawn_location for an alerted spawn is sent', spotPosts().length === 2 && spotPosts()[1].pois[0] === 1075);
  send('42/legacy,["message",{"type":"mob","subType":"report","data":{"action":"spawn","id":{"mobId":"ker","worldId":"cerberus","zoneInstance":2},"data":{"zoneId2":"elpis","zonePoiIds":[1150]}}}]');
  check('spawn points for other worlds are not sent', spotPosts().length === 2);
  send('42/legacy,["message",{"type":"mob","subType":"report","data":{"action":"death","id":{"mobId":"sphatika","worldId":"belias"},"data":{"num":1}}}]');
  check('death reports are not spawn points', spotPosts().length === 2);

  check('url + token header', hunts().every(s => s.url === 'http://localhost:47821/hunt' && s.headers['X-HuntHop-Token'] === TOKEN));

  await new Promise(r => setTimeout(r, 3300)); // initial ping fires after 3 s
  check('initial ping sent', sent.some(s => s.url.endsWith('/ping')));

  console.log(failed ? `\n${failed} FAILED` : '\nall passed');
  process.exit(failed ? 1 : 0);
})();
