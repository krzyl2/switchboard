import type { ConnectionState, Status } from './api/status';
import { PRODUCT_NAME } from './config';
import { messages, type Lang, type Messages } from './i18n';
import { useStatus } from './useStatus';

type Tone = 'ok' | 'warn' | 'crit';

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

function StateLabel({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span class="state" data-tone={tone}>
      <span class="dot" aria-hidden="true" />
      {label}
    </span>
  );
}

// Local wall-clock time only here, at the presentation edge; the machine-readable instant stays in dateTime.
function Timestamp({ at, lang }: { at: Date; lang: Lang }) {
  const text = new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
  return <time dateTime={at.toISOString()}>{text}</time>;
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

export function App({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = useStatus();

  return (
    <main class="page">
      <h1>{PRODUCT_NAME}</h1>
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
      <aside class="card disclaimer" role="note">
        <strong>{t.notProtectiveTitle}</strong> {t.notProtectiveBody}
      </aside>
    </main>
  );
}
