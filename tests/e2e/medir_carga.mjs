// Mide el indicador del acta "catalogo en menos de 3 s" en la web levantada con docker compose (http://localhost:3000).
//
//     node tests/e2e/medir_carga.mjs [veces]        (por defecto 5 veces por red)
//
// Abre el catalogo en un Edge/Chrome sin ventana, siempre con la cache vacia y sin sesion, y espera a que la web marque
// performance.mark('catalogo-visible') (ver web/src/app/core/load-time.ts): el catalogo ya se ve con productos.
// Se toman dos tiempos: el del reloj del script (desde que se pide la pagina, incluye redirecciones) y el que mide la
// pagina (desde el inicio de su navegacion). Se repite con tres redes: sin limite, 4G y 4G lento (como los perfiles de DevTools).
// Requiere Node 22 o superior (WebSocket incluido) y Edge o Chrome; otra ruta se indica con BROWSER=ruta/al/navegador.
// Sale con codigo 1 si la mediana de alguna red supera los 3 s.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const URL_CATALOGO = process.env.WEB_URL ?? 'http://localhost:3000/tienda';
const RUNS = Number(process.argv[2] ?? 5);
const TARGET_MS = 3000;
const PORT = 9400 + Math.floor(Math.random() * 400);
const NETWORKS = [
  { name: 'Sin limite', conditions: null },
  { name: '4G (9 Mbps, 60 ms)', conditions: { latency: 60, downloadThroughput: (9000 * 1024) / 8, uploadThroughput: (1500 * 1024) / 8 } },
  { name: '4G lento (1,6 Mbps, 150 ms)', conditions: { latency: 150, downloadThroughput: (1600 * 1024) / 8, uploadThroughput: (750 * 1024) / 8 } },
];

function findBrowser() {
  const candidates = [
    process.env.BROWSER,
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/microsoft-edge',
  ];
  const found = candidates.find((p) => p && existsSync(p));
  if (!found) throw new Error('No encontre Edge ni Chrome. Indicar la ruta con BROWSER=...');
  return found;
}

const median = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  return s.length % 2 ? s[(s.length - 1) / 2] : Math.round((s[s.length / 2 - 1] + s[s.length / 2]) / 2);
};

const profile = mkdtempSync(join(tmpdir(), 'mercadoya-carga-'));
const browser = spawn(findBrowser(), [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, '--no-first-run',
  '--no-default-browser-check', '--disable-gpu', '--window-size=1366,900', 'about:blank',
], { stdio: 'ignore' });

let exitCode = 0;
try {
  let targets;
  for (let i = 0; i < 80 && !targets; i++) {
    try { const r = await fetch(`http://127.0.0.1:${PORT}/json/list`); if (r.ok) targets = await r.json(); } catch { /* todavia no */ }
    if (!targets) await sleep(250);
  }
  if (!targets) throw new Error('El navegador no respondio');
  const ws = new WebSocket(targets.find((t) => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
  let seq = 0;
  const pending = new Map();
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.error) p.fail(new Error(JSON.stringify(msg.error))); else p.ok(msg.result);
  };
  const send = (method, params = {}) => new Promise((ok, fail) => {
    const id = ++seq;
    pending.set(id, { ok, fail });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result.value;

  await send('Page.enable');
  await send('Network.enable');
  await send('Network.setCacheDisabled', { cacheDisabled: true });

  console.log(`Carga del catalogo (${URL_CATALOGO}), cache vacia y sin sesion, ${RUNS} veces por red. Objetivo: menos de ${TARGET_MS} ms.\n`);
  for (const net of NETWORKS) {
    await send('Network.emulateNetworkConditions', net.conditions
      ? { offline: false, ...net.conditions }
      : { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    const wall = [];
    const page = [];
    for (let i = 0; i < RUNS; i++) {
      await send('Network.clearBrowserCache');
      await send('Page.navigate', { url: 'about:blank' });
      await sleep(200);
      const t0 = Date.now();
      await send('Page.navigate', { url: URL_CATALOGO });
      let ms = null;
      while (ms === null && Date.now() - t0 < 30000) {
        await sleep(25);
        ms = await evaluate(`performance.getEntriesByName('catalogo-visible')[0]?.detail?.ms ?? null`).catch(() => null);
      }
      if (ms === null) throw new Error(`El catalogo no se mostro en 30 s (${net.name}). La web esta levantada y actualizada?`);
      wall.push(Date.now() - t0);
      page.push(ms);
    }
    const m = median(wall);
    const ok = m < TARGET_MS;
    if (!ok) exitCode = 1;
    console.log(`${ok ? 'OK ' : 'MAL'} ${net.name.padEnd(28)} mediana ${String(m).padStart(5)} ms (reloj del script) / ${String(median(page)).padStart(5)} ms (medido por la pagina)   [${wall.join(', ')}]`);
  }
  ws.close();
} catch (e) {
  console.error('ERROR: ' + e.message);
  exitCode = 2;
} finally {
  browser.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* el navegador puede tardar en soltar la carpeta */ }
}
process.exit(exitCode);
