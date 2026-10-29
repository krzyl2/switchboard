#!/usr/bin/env node
// Minimal Home Assistant WebSocket client for deploy/release.ps1. No dependencies (Node 22+ global WebSocket).
// The long-lived access token arrives on STDIN (first line) when --token-stdin is given (release.ps1 and
// set-ha-token.ps1 do this; the token is DPAPI-protected at rest). Without the flag it falls back to the
// env var SWITCHBOARD_HA_TOKEN (CI-style). Never in argv, never printed, never written anywhere.
// SWITCHBOARD_HA_URL (not secret) selects the instance. Every command prints ONE
// JSON object on stdout and exits 0 (ok) or 1 (failed); progress goes to stderr.
//
//   node deploy/ha-supervisor.mjs ping
//   node deploy/ha-supervisor.mjs info                         add-on info (version, state, ingress_url)
//   node deploy/ha-supervisor.mjs deploy --target 1.2.3 [--timeout 300]
//   node deploy/ha-supervisor.mjs logs                         add-on log text
//   node deploy/ha-supervisor.mjs states <entity_id>...        read-only get_states, filtered
//   node deploy/ha-supervisor.mjs status                       GET api/status through ingress (best effort)
//
// Writes only: supervisor /store/reload and POST /addons/<slug>/update. Nothing else, ever.

const SLUG = 'fb9ed8c0_switchboard';
import { readFileSync } from 'node:fs';
const useStdin = process.argv.includes('--token-stdin');
const token = (useStdin ? readFileSync(0, 'utf8').split(/\r?\n/)[0] : (process.env.SWITCHBOARD_HA_TOKEN ?? '')).trim();
const baseUrl = (process.env.SWITCHBOARD_HA_URL || 'http://192.168.10.21:55285').replace(/\/+$/, '');

const redact = (s) => (token ? String(s).split(token).join('***') : String(s));
const log = (s) => process.stderr.write(redact(s) + '\n');
const out = (o, code = 0) => { process.stdout.write(redact(JSON.stringify(o)) + '\n'); process.exit(code); };
const fail = (m) => out({ ok: false, error: String(m) }, 1);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

if (!token) fail(useStdin ? 'no token on stdin' : 'SWITCHBOARD_HA_TOKEN is not set');
if (typeof WebSocket === 'undefined') fail('global WebSocket missing: Node 22+ required');
if (!/^https?:\/\//.test(baseUrl)) fail('SWITCHBOARD_HA_URL must start with http:// or https://');

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(baseUrl.replace(/^http/, 'ws') + '/api/websocket');
    let nextId = 1;
    const pending = new Map();
    let haVersion = null;
    const timer = setTimeout(() => reject(new Error('websocket connect/auth timeout')), 15000);
    ws.addEventListener('error', () => reject(new Error('websocket error (HA unreachable?)')));
    ws.addEventListener('close', () => { for (const p of pending.values()) p.reject(new Error('websocket closed')); });
    ws.addEventListener('message', (ev) => {
      const m = JSON.parse(ev.data);
      if (m.type === 'auth_required') ws.send(JSON.stringify({ type: 'auth', access_token: token }));
      else if (m.type === 'auth_invalid') { clearTimeout(timer); reject(new Error('HA rejected the token (auth_invalid)')); }
      else if (m.type === 'auth_ok') {
        clearTimeout(timer);
        haVersion = m.ha_version;
        resolve({
          ws, haVersion,
          call(msg) {
            return new Promise((res, rej) => {
              const id = nextId++;
              pending.set(id, { resolve: res, reject: rej });
              ws.send(JSON.stringify({ id, ...msg }));
            });
          },
          close() { ws.close(); },
        });
      } else if (m.type === 'result' && pending.has(m.id)) {
        const p = pending.get(m.id); pending.delete(m.id);
        if (m.success) p.resolve(m.result);
        else p.reject(new Error(`${m.error?.code ?? 'error'}: ${m.error?.message ?? 'unknown'}`));
      }
    });
  });
}

const supervisor = (c, endpoint, method = 'get', timeout = 30) =>
  c.call({ type: 'supervisor/api', endpoint, method, timeout });

async function addonInfo(c) { return supervisor(c, `/addons/${SLUG}/info`); }

async function cmdDeploy(c, target, timeoutSec) {
  const deadline = Date.now() + timeoutSec * 1000;
  const before = await addonInfo(c);
  const previous = before.version;
  log(`add-on is ${previous} (${before.state}), target ${target}`);
  if (previous === target && before.state === 'started') {
    return { ok: true, skipped: true, previous, version: previous, state: before.state };
  }
  if (previous !== target) {
    // The Supervisor reads config.yaml from the repository's default branch: reload until it offers the target.
    let latest = before.version_latest;
    for (let i = 0; i < 12 && latest !== target; i++) {
      await supervisor(c, '/store/reload', 'post', 60);
      latest = (await addonInfo(c)).version_latest;
      log(`store reloaded; version_latest ${latest}`);
      if (latest !== target) await sleep(5000);
    }
    if (latest !== target) throw new Error(`store offers ${latest}, not ${target}: is config.yaml pushed to main?`);
    log('updating (the add-on restarts; the go-e is stopped once and resumed with its resume option)');
    // Long call; the poll below is the source of truth if it fails transiently.
    await Promise.race([
      supervisor(c, `/addons/${SLUG}/update`, 'post', null).catch((e) => log(`update call: ${e.message}`)),
      sleep(Math.max(1000, deadline - Date.now())),
    ]);
  }
  let last = null;
  while (Date.now() < deadline) {
    try {
      last = await addonInfo(c);
      if (last.version === target && last.state === 'started') return { ok: true, skipped: false, previous, version: last.version, state: last.state };
      log(`waiting: ${last.version} ${last.state}`);
    } catch (e) { log(`poll: ${e.message}`); }
    await sleep(5000);
  }
  throw new Error(`timeout ${timeoutSec}s: add-on is ${last?.version} ${last?.state}, wanted ${target} started`);
}

async function cmdLogs(c) {
  // Bearer-authenticated REST first (the logs are plain text, which supervisor/api over WS does not carry well).
  const r = await fetch(`${baseUrl}/api/hassio/addons/${SLUG}/logs`, { headers: { Authorization: `Bearer ${token}` } });
  if (r.ok) return { ok: true, via: 'rest', text: await r.text() };
  const ws = await supervisor(c, `/addons/${SLUG}/logs`).catch((e) => { throw new Error(`logs: REST ${r.status}, ws ${e.message}`); });
  return { ok: true, via: 'ws', text: typeof ws === 'string' ? ws : JSON.stringify(ws) };
}

async function cmdStatus(c) {
  const info = await addonInfo(c);
  if (!info.ingress_url) throw new Error('no ingress_url');
  const s = await supervisor(c, '/ingress/session', 'post');
  const r = await fetch(baseUrl + info.ingress_url.replace(/\/?$/, '/') + 'api/status', {
    headers: { Cookie: `ingress_session=${s.session}`, Authorization: `Bearer ${token}` },
  });
  if (!r.ok) throw new Error(`ingress api/status HTTP ${r.status}`);
  return { ok: true, status: await r.json() };
}

(async () => {
  const [cmd, ...rest] = process.argv.slice(2).filter((a) => a !== '--token-stdin');
  const arg = (n, d) => { const i = rest.indexOf(n); return i >= 0 ? rest[i + 1] : d; };
  const c = await connect();
  try {
    let res;
    switch (cmd) {
      case 'ping': res = { ok: true, ha_version: c.haVersion }; break;
      case 'info': { const i = await addonInfo(c); res = { ok: true, version: i.version, version_latest: i.version_latest, state: i.state, ingress_url: i.ingress_url }; break; }
      case 'deploy': {
        const target = arg('--target');
        if (!/^\d+\.\d+\.\d+$/.test(target ?? '')) throw new Error('--target x.y.z required');
        res = await cmdDeploy(c, target, Number(arg('--timeout', 300)));
        break;
      }
      case 'logs': res = await cmdLogs(c); break;
      case 'states': {
        const wanted = new Set(rest);
        const all = await c.call({ type: 'get_states' });
        res = { ok: true, states: all.filter((s) => wanted.has(s.entity_id)).map((s) => ({ entity_id: s.entity_id, state: s.state })) };
        break;
      }
      case 'status': res = await cmdStatus(c); break;
      default: throw new Error(`unknown command '${cmd}'`);
    }
    out(res);
  } catch (e) { fail(e.message); }
})().catch((e) => fail(e.message));
