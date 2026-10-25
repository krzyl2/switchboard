import { useEffect, useState } from 'preact/hooks';
import type { ConnectionState, Status } from './api/status';
import { PRODUCT_NAME } from './config';
import { Dashboard } from './Dashboard';
import { DecisionLog } from './DecisionLog';
import { DeviceCard } from './DeviceCard';
import { Flows } from './Flows';
import { fill, messages, type Lang, type Messages } from './i18n';
import { Limits } from './Limits';
import { SignalsPage } from './Signals';
import { applyTheme, nextTheme, readTheme, saveTheme, type ThemeChoice } from './theme';
import { StateLabel, Timestamp, type Tone } from './ui';
import { useStatus } from './useStatus';

const CONNECTION_TONE: Record<ConnectionState, Tone> = {
  connected: 'ok',
  connecting: 'warn',
  disconnected: 'crit',
};

function connectionLabel(t: Messages, state: ConnectionState): string {
  switch (state) {
    case 'connected':
      return t.connectionConnected;
    case 'connecting':
      return t.connectionConnecting;
    case 'disconnected':
      return t.connectionDisconnected;
  }
}

function StatusList({ status, updatedAt, t, lang }: { status: Status; updatedAt: Date; t: Messages; lang: Lang }) {
  const shadow = status.mode === 'shadow';
  return (
    <>
      <dl class="status">
        <dt>{t.rowMode}</dt>
        <dd>
          <span>{shadow ? t.modeShadow : t.modeActive}</span>
          <span class="sub">{shadow ? t.modeShadowNote : t.modeActiveNote}</span>
        </dd>
        <dt>{t.rowHomeAssistant}</dt>
        <dd>
          <StateLabel tone={CONNECTION_TONE[status.homeAssistant]} label={connectionLabel(t, status.homeAssistant)} />
        </dd>
        <dt>{t.rowMqtt}</dt>
        <dd>
          <StateLabel tone={CONNECTION_TONE[status.mqtt]} label={connectionLabel(t, status.mqtt)} />
        </dd>
        <dt>{t.rowHealth}</dt>
        <dd>
          <StateLabel
            tone={status.healthy ? 'ok' : 'crit'}
            label={status.healthy ? t.healthHealthy : t.healthUnhealthy}
          />
        </dd>
        <dt>{t.rowVersion}</dt>
        <dd class="mono">{status.version}</dd>
      </dl>
      <p class="updated">
        {t.lastUpdated}: <Timestamp at={updatedAt} lang={lang} />
      </p>
    </>
  );
}

function StatusPage({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = useStatus();

  return (
    <section class="card">
      {view.kind === 'loading' && <p>{t.loading}</p>}
      {view.kind === 'live' && <StatusList status={view.status} updatedAt={view.updatedAt} t={t} lang={lang} />}
      {view.kind === 'unavailable' && (
        <div role="alert">
          <p class="unavailable">
            <StateLabel tone="crit" label={t.unavailable} />
          </p>
          <p class="updated">
            {t.lastSuccessfulUpdate}:{' '}
            {view.lastSuccessAt ? <Timestamp at={view.lastSuccessAt} lang={lang} /> : t.never}
          </p>
        </div>
      )}
    </section>
  );
}

export type Route =
  | { page: 'dashboard' }
  | { page: 'flows' }
  | { page: 'device'; id: string | null }
  | { page: 'limits' }
  | { page: 'log' }
  | { page: 'signals' }
  | { page: 'status' };

// Hash routes: ingress serves the page under a per-session path prefix, and a fragment never leaves
// the browser, so no route can address anything outside this add-on. #/guard keeps its Stage 2 name.
const HREF: Record<Route['page'], string> = {
  dashboard: '#/',
  flows: '#/flows',
  device: '#/devices',
  limits: '#/guard',
  log: '#/log',
  signals: '#/signals',
  status: '#/status',
};

export function parseRoute(hash: string): Route {
  if (hash === HREF.flows) return { page: 'flows' };
  if (hash === HREF.limits) return { page: 'limits' };
  if (hash === HREF.log) return { page: 'log' };
  if (hash === HREF.signals) return { page: 'signals' };
  if (hash === HREF.status) return { page: 'status' };
  if (hash === HREF.device) return { page: 'device', id: null };
  if (hash.startsWith(`${HREF.device}/`)) {
    try {
      const id = decodeURIComponent(hash.slice(HREF.device.length + 1));
      return { page: 'device', id: id === '' ? null : id };
    } catch {
      return { page: 'device', id: null };
    }
  }
  return { page: 'dashboard' };
}

function useRoute(): Route {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.hash));
  useEffect(() => {
    const onHashChange = () => setRoute(parseRoute(window.location.hash));
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);
  return route;
}

type NavItem = { index: string; page: Route['page'] | null; label: keyof Messages };

// The mockup's rail (docs/mockups/index.html): seven numbered screens. Setup (03, Stage 5) and the
// day plan (06, v3.0) are not built: listed, clearly disabled, never a link.
const SCREENS: readonly NavItem[] = [
  { index: '01', page: 'dashboard', label: 'nav.dashboard' },
  { index: '02', page: 'flows', label: 'nav.flows' },
  { index: '03', page: null, label: 'nav.setup' },
  { index: '04', page: 'device', label: 'nav.device' },
  { index: '05', page: 'limits', label: 'nav.limits' },
  { index: '06', page: null, label: 'nav.plan' },
  { index: '07', page: 'log', label: 'nav.log' },
];

const DIAGNOSTICS: readonly NavItem[] = [
  { index: '', page: 'signals', label: 'navSignals' },
  { index: '', page: 'status', label: 'navStatus' },
];

function NavEntry({ item, route, t }: { item: NavItem; route: Route; t: Messages }) {
  if (item.page === null) {
    return (
      <span class="navitem" aria-disabled="true">
        <span class="idx" aria-hidden="true">
          {item.index}
        </span>
        {t[item.label]}
        <small class="navnote">{t['nav.notInThisVersion']}</small>
      </span>
    );
  }
  return (
    <a class="navitem" href={HREF[item.page]} aria-current={route.page === item.page ? 'page' : undefined}>
      {item.index !== '' && (
        <span class="idx" aria-hidden="true">
          {item.index}
        </span>
      )}
      {t[item.label]}
    </a>
  );
}

/** UI-13: system → light → dark, applied at once and kept for this browser when it allows. */
function ThemeToggle({ t }: { t: Messages }) {
  const [choice, setChoice] = useState<ThemeChoice>(readTheme);
  const mode = t[`theme.${choice}`];
  return (
    <button
      type="button"
      class="themetoggle"
      aria-label={fill(t['theme.change'], { mode })}
      onClick={() => {
        const next = nextTheme(choice);
        applyTheme(next);
        saveTheme(next);
        setChoice(next);
      }}
    >
      {fill(t['theme.button'], { mode })}
    </button>
  );
}

export function App({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const route = useRoute();

  // Only the page on screen polls the add-on; the shell itself requests nothing.
  return (
    <div class="shell">
      <nav class="rail" aria-label={t.navLabel}>
        <div class="brand">
          <span class="name">{PRODUCT_NAME}</span>
          <span class="tagline">{t['shell.tagline']}</span>
        </div>
        {SCREENS.map((item) => (
          <NavEntry key={item.index} item={item} route={route} t={t} />
        ))}
        <span class="navgroup">{t['nav.diagnostics']}</span>
        {DIAGNOSTICS.map((item) => (
          <NavEntry key={item.page} item={item} route={route} t={t} />
        ))}
        <ThemeToggle t={t} />
      </nav>
      <main class="main">
        <div class="page">
          <header class="pagehead">
            <h1>{PRODUCT_NAME}</h1>
            <span class="badge shadowpill" data-tone="muted">
              <span class="dot" aria-hidden="true" />
              {t['shell.shadowPill']}
            </span>
          </header>
          {route.page === 'dashboard' && <Dashboard lang={lang} />}
          {route.page === 'flows' && <Flows lang={lang} />}
          {route.page === 'device' && <DeviceCard lang={lang} id={route.id} />}
          {route.page === 'limits' && <Limits lang={lang} />}
          {route.page === 'log' && <DecisionLog lang={lang} />}
          {route.page === 'signals' && <SignalsPage lang={lang} />}
          {route.page === 'status' && <StatusPage lang={lang} />}
          <aside class="card disclaimer" role="note">
            <strong>{t.notProtectiveTitle}</strong> {t.notProtectiveBody}
          </aside>
        </div>
      </main>
    </div>
  );
}
