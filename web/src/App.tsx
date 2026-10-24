import { useEffect, useState } from 'preact/hooks';
import type { ConnectionState, Status } from './api/status';
import { PRODUCT_NAME } from './config';
import { messages, type Lang, type Messages } from './i18n';
import { SignalsPage } from './Signals';
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

type Route = 'status' | 'signals';

// Hash routes: ingress serves the page under a per-session path prefix, and a fragment never leaves
// the browser, so no route can address anything outside this add-on.
const ROUTES: Record<Route, string> = { status: '#/', signals: '#/signals' };

function currentRoute(): Route {
  return window.location.hash === ROUTES.signals ? 'signals' : 'status';
}

function useRoute(): Route {
  const [route, setRoute] = useState<Route>(currentRoute);
  useEffect(() => {
    const onHashChange = () => setRoute(currentRoute());
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);
  return route;
}

export function App({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const route = useRoute();
  const link = (target: Route, label: string) => (
    <a href={ROUTES[target]} aria-current={route === target ? 'page' : undefined}>
      {label}
    </a>
  );

  // Only the page on screen polls the add-on.
  return (
    <main class={route === 'signals' ? 'page wide' : 'page'}>
      <header class="pagehead">
        <h1>{PRODUCT_NAME}</h1>
        <nav aria-label={t.navLabel}>
          {link('status', t.navStatus)}
          {link('signals', t.navSignals)}
        </nav>
      </header>
      {route === 'signals' ? <SignalsPage lang={lang} /> : <StatusPage lang={lang} />}
      <aside class="card disclaimer" role="note">
        <strong>{t.notProtectiveTitle}</strong> {t.notProtectiveBody}
      </aside>
    </main>
  );
}
