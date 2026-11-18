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
w.GM_registerMenuCommand = () => {};
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

  check('url + token header', hunts().every(s => s.url === 'http://localhost:47821/hunt' && s.headers['X-HuntHop-Token'] === TOKEN));

  await new Promise(r => setTimeout(r, 3300)); // initial ping fires after 3 s
  check('initial ping sent', sent.some(s => s.url.endsWith('/ping')));

  console.log(failed ? `\n${failed} FAILED` : '\nall passed');
  process.exit(failed ? 1 : 0);
})();
