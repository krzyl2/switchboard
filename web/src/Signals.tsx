import type {
  ConfigurationInfo,
  ConfigurationState,
  Connection,
  Direction,
  Issue,
  Phase,
  Signal,
  Signals,
  SignalState,
  SourceStatus,
} from './api/signals';
import { formatAge, formatInterval, formatQuantity } from './format';
import { messages, type Lang, type Messages } from './i18n';
import { StateLabel, Timestamp, type Tone } from './ui';
import { useSignals } from './useSignals';

// Stale, unavailable and rejected (an unusable value) all mean the signal is not in use, as bad as
// no data at all. A sample refused by the balance check keeps the previous value in use, so that
// signal stays fresh with the refusal as its detail. An unmapped optional slot is simply not in use.
const SIGNAL_TONE: Record<SignalState, Tone> = {
  fresh: 'ok',
  stale: 'crit',
  unavailable: 'crit',
  rejected: 'crit',
  unmapped: 'muted',
};

const SOURCE_TONE: Record<SourceStatus, Tone> = { connected: 'ok', connecting: 'warn', disconnected: 'crit' };

const CONFIGURATION_TONE: Record<ConfigurationState, Tone> = { valid: 'ok', invalid: 'crit', missing: 'crit' };

const NO_VALUE = '—';

interface Props {
  t: Messages;
  lang: Lang;
}

function quantity(value: number | null, unit: string, lang: Lang): string {
  return value === null ? NO_VALUE : formatQuantity(value, unit, lang);
}

function sourceStatusLabel(t: Messages, status: SourceStatus): string {
  switch (status) {
    case 'connected':
      return t.connectionConnected;
    case 'connecting':
      return t.connectionConnecting;
    case 'disconnected':
      return t.connectionDisconnected;
  }
}

function SourceBanner({ signals, t }: { signals: Signals; t: Messages }) {
  const { kind, status } = signals.source;
  return (
    <>
      {kind === 'replay' && (
        <p class="card banner" data-tone="warn" role="status">
          <StateLabel tone="warn" label={t.sourceReplay} />
        </p>
      )}
      {status === 'disconnected' && (
        <p class="card banner" data-tone="crit" role="alert">
          <StateLabel
            tone="crit"
            label={kind === 'replay' ? t.sourceDisconnectedReplay : t.sourceDisconnectedHomeAssistant}
          />
        </p>
      )}
      {status === 'connecting' && (
        <p class="card banner" data-tone="warn" role="status">
          <StateLabel tone="warn" label={t.sourceConnecting} />
        </p>
      )}
    </>
  );
}

function connectionSummary(connection: Connection, { t, lang }: Props): string {
  const characteristic =
    connection.characteristic === 'unknown' ? t.characteristicUnknown : connection.characteristic;
  return [
    t[`topology.${connection.topology}`],
    formatQuantity(connection.breakerRatingA, 'A', lang),
    `${t.characteristic} ${characteristic}`,
    formatQuantity(connection.voltageV, 'V', lang),
  ].join(' · ');
}

function IssueItem({ issue, t }: { issue: Issue; t: Messages }) {
  const position = [
    issue.line === null ? null : `${t.issueLine} ${issue.line}`,
    issue.column === null ? null : `${t.issueColumn} ${issue.column}`,
  ]
    .filter((part) => part !== null)
    .join(', ');
  return (
    <li>
      <span>{t[`issue.${issue.code}`]}</span>
      <span class="sub">
        {issue.path !== '' && <span class="mono">{issue.path}</span>}
        {issue.path !== '' && position !== '' && ' · '}
        {position}
      </span>
      <span class="sub">{issue.message}</span>
    </li>
  );
}

function ConfigurationIssues({ configuration, t }: { configuration: ConfigurationInfo; t: Messages }) {
  const unusable = configuration.state !== 'valid';
  return (
    <>
      {unusable && (
        <div class="issues" role="alert">
          <p>
            <StateLabel
              tone="crit"
              label={configuration.state === 'missing' ? t.configurationMissingBody : t.configurationInvalidBody}
            />
          </p>
          {configuration.errors.length > 0 && (
            <>
              <h3>{t.configurationErrors}</h3>
              <ul>
                {configuration.errors.map((issue, i) => (
                  <IssueItem key={i} issue={issue} t={t} />
                ))}
              </ul>
            </>
          )}
        </div>
      )}
      {configuration.warnings.length > 0 && (
        <div class="issues">
          <h3>
            <StateLabel tone="warn" label={t.configurationWarnings} />
          </h3>
          <ul>
            {configuration.warnings.map((issue, i) => (
              <IssueItem key={i} issue={issue} t={t} />
            ))}
          </ul>
        </div>
      )}
    </>
  );
}

function Overview({ signals, ...props }: { signals: Signals } & Props) {
  const { t, lang } = props;
  const { configuration, source, connection } = signals;
  return (
    <section class="card">
      <h2>{t.signalsTitle}</h2>
      <dl class="status">
        <dt>{t.rowConfiguration}</dt>
        <dd>
          <StateLabel
            tone={CONFIGURATION_TONE[configuration.state]}
            label={t[`configurationState.${configuration.state}`]}
          />
        </dd>
        <dt>{t.rowFile}</dt>
        <dd class="mono">{configuration.path}</dd>
        <dt>{t.rowLoaded}</dt>
        <dd>{configuration.loadedAt ? <Timestamp at={configuration.loadedAt} lang={lang} /> : t.never}</dd>
        {connection && (
          <>
            <dt>{t.rowConnection}</dt>
            <dd>{connectionSummary(connection, props)}</dd>
          </>
        )}
        <dt>{t.rowSource}</dt>
        <dd>
          <span>{t[`sourceKind.${source.kind}`]}</span>{' '}
          <StateLabel tone={SOURCE_TONE[source.status]} label={sourceStatusLabel(t, source.status)} />
        </dd>
      </dl>
      <ConfigurationIssues configuration={configuration} t={t} />
    </section>
  );
}

function ReserveCell({ phase, t, lang }: { phase: Phase } & Props) {
  const reserve = phase.reserve;
  if (reserve === null) return <td class="num">{NO_VALUE}</td>;
  // Without a detected refresh the derivation assumes the worst case, max_age.
  const interval = formatInterval(reserve.refreshS, lang);
  const refresh = reserve.refreshDetected
    ? `${t.reserveRefresh} ${interval}, ${t.reserveRefreshDetected}`
    : `max_age ${interval}, ${t.reserveRefreshAssumed}`;
  return (
    <td class="num">
      {formatQuantity(reserve.configuredW, 'W', lang)}
      <span class="sub">
        {t.reserveMinimum} {formatQuantity(reserve.minimumSafeW, 'W', lang)}
      </span>
      <span class="sub">{refresh}</span>
      {reserve.belowMinimum && (
        <span class="sub">
          <StateLabel tone="warn" label={t.reserveBelowMinimum} />
        </span>
      )}
    </td>
  );
}

/** Seconds from `at` to the add-on's answer time; both come from the add-on, so no browser clock is involved. */
function ageAt(at: Date, generatedAt: Date): number {
  return (generatedAt.getTime() - at.getTime()) / 1000;
}

function PhasesTable({ phases, generatedAt, ...props }: { phases: Phase[]; generatedAt: Date } & Props) {
  const { t, lang } = props;
  return (
    <section class="card">
      <h2>{t.phasesTitle}</h2>
      <div class="tablewrap">
        <table>
          <thead>
            <tr>
              <th scope="col">{t.colPhase}</th>
              <th scope="col">{t.colMeterData}</th>
              <th scope="col">{t.colCurrent}</th>
              <th scope="col">{t.colGrid}</th>
              <th scope="col">{t.colGeneration}</th>
              <th scope="col">{t.colLoad}</th>
              <th scope="col">{t.colBalance}</th>
              <th scope="col">{t.colReserve}</th>
            </tr>
          </thead>
          <tbody>
            {phases.map((phase) => (
              <tr key={phase.phase} data-phase={phase.phase}>
                <th scope="row" class="mono">
                  {phase.phase}
                </th>
                <td>
                  <StateLabel
                    tone={phase.protectionDataValid ? 'ok' : 'crit'}
                    label={phase.protectionDataValid ? t.meterDataValid : t.meterDataInvalid}
                  />
                  {phase.invalidReason !== null && (
                    <span class="sub">{t[`invalidReason.${phase.invalidReason}`]}</span>
                  )}
                </td>
                <td class="num">
                  {quantity(phase.currentA, 'A', lang)}
                  {phase.direction !== null && <span class="sub">{t[`direction.${phase.direction}`]}</span>}
                </td>
                <td class="num">{quantity(phase.netImportW, 'W', lang)}</td>
                <td class="num">{quantity(phase.generationW, 'W', lang)}</td>
                <td class="num">{quantity(phase.loadW, 'W', lang)}</td>
                <td class="num">
                  {quantity(phase.residualW, 'W', lang)}
                  {phase.residualAt !== null && (
                    <span class="sub">
                      {t.balanceAge} {formatAge(ageAt(phase.residualAt, generatedAt), lang)}
                    </span>
                  )}
                  <span class="sub">
                    {t.rejected}: {phase.balanceRejections}
                  </span>
                </td>
                <ReserveCell phase={phase} t={t} lang={lang} />
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function DirectionCell({ direction, t }: { direction: Direction | null; t: Messages }) {
  if (direction === null) return <td>{NO_VALUE}</td>;
  const label = t[`direction.${direction.direction}`];
  return (
    <td>
      {direction.direction === 'unknown' ? <StateLabel tone="warn" label={label} /> : <span>{label}</span>}
      {direction.source !== 'signed_signal' || direction.from === null ? (
        <span class="sub">{t[`directionSource.${direction.source}`]}</span>
      ) : null}
      {direction.from !== null && (
        <span class="sub">
          {t.directionFrom} <span class="mono">{direction.from}</span>
        </span>
      )}
    </td>
  );
}

// SIG-05: while a signal is not fresh, every device that depends on it is unsafe (held off). A
// device's own draw is the exception: stale, it is no longer trusted (no relief, no increases), but
// nothing is held off because of it.
function DependentsCell({ signal, t }: { signal: Signal; t: Messages }) {
  if (signal.dependentDevices.length === 0) return <td>{NO_VALUE}</td>;
  const draw = signal.role === 'device_current' || signal.role === 'device_power';
  return (
    <td>
      {signal.dependentDevices.map((device) => (
        <span key={device} class="mono line">
          {device}
        </span>
      ))}
      {signal.state !== 'fresh' && signal.state !== 'unmapped' && (
        <span class="sub">
          {draw ? <StateLabel tone="warn" label={t.dependentsUntrusted} /> : <StateLabel tone="crit" label={t.dependentsUnsafe} />}
        </span>
      )}
    </td>
  );
}

function SignalRow({ signal, t, lang }: { signal: Signal } & Props) {
  const role = t[`role.${signal.role}`];
  let value: string;
  if (signal.value === null) value = NO_VALUE;
  else if (typeof signal.value === 'string') value = signal.value;
  else value = formatQuantity(signal.value, signal.unit ?? '', lang);
  return (
    <tr data-state={signal.state}>
      <td>
        {signal.name === null ? NO_VALUE : <span class="mono">{signal.name}</span>}
        <span class="sub">{signal.phase === null ? role : `${role} · ${signal.phase}`}</span>
      </td>
      <td>{signal.entity === null ? NO_VALUE : <span class="mono">{signal.entity}</span>}</td>
      <td class="num">{value}</td>
      <td class="num">
        {signal.raw === null ? NO_VALUE : signal.raw.unit === null ? signal.raw.state : `${signal.raw.state} ${signal.raw.unit}`}
      </td>
      <td class="num">
        {signal.ageS === null ? NO_VALUE : formatAge(signal.ageS, lang)}
        {signal.maxAgeS !== null && (
          <span class="sub">
            {t.maxAge} {formatAge(signal.maxAgeS, lang)}
          </span>
        )}
      </td>
      <td>
        <StateLabel tone={SIGNAL_TONE[signal.state]} label={t[`signalState.${signal.state}`]} />
        {signal.detail !== 'none' && <span class="sub">{t[`signalDetail.${signal.detail}`]}</span>}
        {signal.rejectedCount > 0 && (
          <span class="sub">
            {t.rejected}: {signal.rejectedCount}
          </span>
        )}
      </td>
      <td class="num">
        {signal.refreshS === null ? (
          <span class="sub">{t.refreshNotDetected}</span>
        ) : (
          formatInterval(signal.refreshS, lang)
        )}
        {signal.refreshIntervals > 0 && <span class="sub">n = {signal.refreshIntervals}</span>}
      </td>
      <DirectionCell direction={signal.direction} t={t} />
      <DependentsCell signal={signal} t={t} />
    </tr>
  );
}

function SignalsTable({ signals, updatedAt, ...props }: { signals: Signals; updatedAt: Date } & Props) {
  const { t, lang } = props;
  const { rejections } = signals;
  return (
    <section class="card">
      <h2>{t.signalsTableTitle}</h2>
      {signals.signals.length === 0 ? (
        <p>{t.noSignals}</p>
      ) : (
        <div class="tablewrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t.colSignal}</th>
                <th scope="col">{t.colEntity}</th>
                <th scope="col">{t.colValue}</th>
                <th scope="col">{t.colRaw}</th>
                <th scope="col">{t.colAge}</th>
                <th scope="col">{t.colState}</th>
                <th scope="col">{t.colRefresh}</th>
                <th scope="col">{t.colDirection}</th>
                <th scope="col">{t.colDependents}</th>
              </tr>
            </thead>
            <tbody>
              {signals.signals.map((signal, i) => (
                <SignalRow key={signal.name ?? `${signal.role}-${signal.phase ?? i}`} signal={signal} {...props} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p class="updated">
        {t.rejectionsTotal}: {rejections.total} ({t.rejectionsBalance} {rejections.balanceResidual},{' '}
        {t.rejectionsInvalid} {rejections.invalidSample})
      </p>
      <p class="updated">
        {t.lastUpdated}: <Timestamp at={updatedAt} lang={lang} />
      </p>
    </section>
  );
}

export function SignalsPage({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = useSignals();

  if (view.kind === 'loading') {
    return (
      <section class="card">
        <p>{t.signalsLoading}</p>
      </section>
    );
  }
  if (view.kind === 'unavailable') {
    return (
      <section class="card">
        <div role="alert">
          <p class="unavailable">
            <StateLabel tone="crit" label={t.signalsUnavailable} />
          </p>
          <p class="updated">
            {t.lastSuccessfulUpdate}:{' '}
            {view.lastSuccessAt ? <Timestamp at={view.lastSuccessAt} lang={lang} /> : t.never}
          </p>
        </div>
      </section>
    );
  }
  return (
    <>
      <SourceBanner signals={view.signals} t={t} />
      <Overview signals={view.signals} t={t} lang={lang} />
      <PhasesTable phases={view.signals.phases} generatedAt={view.signals.generatedAt} t={t} lang={lang} />
      <SignalsTable signals={view.signals} updatedAt={view.updatedAt} t={t} lang={lang} />
    </>
  );
}
