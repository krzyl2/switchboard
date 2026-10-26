import { Fragment } from 'preact';
import type { Economics, EconomicsDevice, PriceBasis, PriceSignal, Savings, SavingsDevice, SavingsTotal } from './api/economics';
import { formatAge, formatFixed, formatKwh, roundTo } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { localClock } from './localTime';
import { PollStatus, StateLabel } from './ui';
import type { PollView } from './usePoll';

// The economics cards (ECO-12, 14-ECONOMICS "UI"): tariff status and savings, as GET api/economics
// reports them, and the pieces the device card and the decision-log summary share (a price, an amount,
// the sources in words). Every amount and price carries the currency label exactly as the
// configuration wrote it: nothing here names a currency. It only reads; savings are an estimate and
// are never worded as a bill, a settlement or a guarantee.

export type NameOf = (id: string) => string;

interface Ctx {
  t: Messages;
  lang: Lang;
  /** The IANA zone local times are shown in; only the presentation edge knows it. */
  zone: string;
}

/** A price per kWh: two decimals when that is exact ("1.10"), four otherwise ("0.3547"), never rounded to a wrong price. */
export function priceNumber(value: number, lang: Lang): string {
  return formatFixed(value, lang, Math.abs(roundTo(value, 2) - value) < 1e-9 ? 2 : 4);
}

export function priceText(t: Messages, lang: Lang, value: number, currency: string): string {
  return fill(t['dashboard.eco.perKwh'], { value: priceNumber(value, lang), currency });
}

/** An amount of money, two decimals, with the label as written; a negative amount keeps its minus. */
export function amountText(t: Messages, lang: Lang, value: number, currency: string): string {
  return fill(t['dashboard.eco.amount'], { value: formatFixed(value, lang, 2), currency });
}

/** The economics answer when it can drive a card: a section is configured and carries its currency label. */
export function usable(view: PollView<Economics>): Economics | null {
  return view.kind === 'live' && view.data.configured && view.data.currency !== null ? view.data : null;
}

/** The savings, only when an economics section yields them (the cards and the summary otherwise say nothing new). */
export function savingsOf(view: PollView<Economics>): { economics: Economics; savings: Savings; currency: string } | null {
  const economics = usable(view);
  if (economics === null || economics.savings === null) return null;
  return { economics, savings: economics.savings, currency: economics.savings.currency ?? economics.currency! };
}

function BasisPill({ basis, t }: { basis: PriceBasis; t: Messages }) {
  return (
    <span class="badge" data-tone={basis === 'fallback' ? 'warn' : 'muted'} data-basis={basis}>
      <span class="dot" aria-hidden="true" />
      {t[`dashboard.eco.basis.${basis}`]}
    </span>
  );
}

/** "13:00", or "Tue 13:00" when the change is not today in the zone shown. */
function changeTime(at: Date, now: Date, lang: Lang, zone: string): string {
  const day = (d: Date) => new Intl.DateTimeFormat('en-CA', { timeZone: zone }).format(d);
  if (day(at) === day(now)) return localClock(at, lang, zone);
  return `${new Intl.DateTimeFormat(lang, { timeZone: zone, weekday: 'short' }).format(at)} ${localClock(at, lang, zone)}`;
}

function SignalRow({ signal, t, lang }: { signal: PriceSignal; t: Messages; lang: Lang }) {
  const state = t[`signalState.${signal.state}`];
  return (
    <>
      <dt>{t['dashboard.eco.signal']}</dt>
      <dd data-row="eco-signal">
        <span class="mono">{signal.name}</span>
        <span class="sub">
          {signal.ageS === null ? fill(t['dashboard.eco.signalNever'], { state }) : fill(t['dashboard.eco.signalState'], { state, age: formatAge(signal.ageS, lang) })}
        </span>
      </dd>
    </>
  );
}

function TariffRows({ economics, currency, ...ctx }: { economics: Economics; currency: string } & Ctx) {
  const { t, lang, zone } = ctx;
  const tariff = economics.tariff!;
  const exportPrice = economics.settlement?.exportPrice ?? null;
  const fallback = tariff.fallbackInForce || exportPrice?.fallbackInForce === true;
  const signals = [tariff.signal, exportPrice?.signal ?? null].filter((s): s is PriceSignal => s !== null);
  return (
    <>
      <dl class="dash-kv">
        <dt>{t['dashboard.eco.zone']}</dt>
        <dd data-row="eco-zone">{tariff.zone !== null ? <span class="mono">{tariff.zone}</span> : <span class="sub-inline">{t['dashboard.eco.noZone']}</span>}</dd>
        <dt>{t['dashboard.eco.importPrice']}</dt>
        <dd data-row="eco-import">
          {tariff.importPrice !== null ? <span class="mono">{priceText(t, lang, tariff.importPrice, currency)}</span> : <span class="unknown">{t['dashboard.eco.priceUnknown']}</span>}
          {tariff.priceBasis !== null && <BasisPill basis={tariff.priceBasis} t={t} />}
        </dd>
        <dt>{t['dashboard.eco.exportPrice']}</dt>
        <dd data-row="eco-export">
          {exportPrice === null ? (
            <span class="sub-inline">{t['dashboard.eco.exportNone']}</span>
          ) : (
            <>
              {exportPrice.perKwh !== null ? <span class="mono">{priceText(t, lang, exportPrice.perKwh, currency)}</span> : <span class="unknown">{t['dashboard.eco.priceUnknown']}</span>}
              {exportPrice.priceBasis !== null && <BasisPill basis={exportPrice.priceBasis} t={t} />}
            </>
          )}
        </dd>
        <dt>{t['dashboard.eco.nextChange']}</dt>
        <dd data-row="eco-next">
          {tariff.nextChangeAt === null ? (
            <span class="sub-inline">{t['dashboard.eco.noChange']}</span>
          ) : (
            <>
              <span class="mono">{changeTime(tariff.nextChangeAt, economics.at, lang, zone)}</span>
              <span class="sub-inline"> ({fill(t['dashboard.eco.nextIn'], { countdown: formatAge((tariff.nextChangeAt.getTime() - economics.at.getTime()) / 1000, lang) })})</span>
              {tariff.nextZone !== null && (
                <span class="sub">
                  {t['dashboard.eco.nextZone']} <span class="mono">{tariff.nextZone}</span>
                </span>
              )}
            </>
          )}
        </dd>
        {signals.map((signal) => (
          <SignalRow key={signal.name} signal={signal} t={t} lang={lang} />
        ))}
      </dl>
      {fallback && <p class="note">{t['dashboard.eco.fallbackNote']}</p>}
      {economics.holidaysListStale && <p class="note">{t['dashboard.eco.holidaysStale']}</p>}
      <p class="dash-why">{t['dashboard.eco.tariff.why']}</p>
    </>
  );
}

export function TariffCard({ view, ...ctx }: { view: PollView<Economics> } & Ctx) {
  const economics = usable(view);
  // No economics section, no tariff in it, or no answer: no card (nothing to say that the page does not already).
  if (economics === null || economics.tariff === null) return null;
  const { t } = ctx;
  return (
    <section class="card" aria-labelledby="eco-tariff-title" data-card="tariff">
      <header class="cardhead">
        <h2 id="eco-tariff-title">{t['dashboard.eco.tariff.title']}</h2>
        <p class="sub">{t['dashboard.eco.tariff.sub']}</p>
      </header>
      <PollStatus view={view} evaluatedAt={economics.at} now={economics.at} lang={ctx.lang} t={t} />
      <TariffRows economics={economics} currency={economics.currency!} {...ctx} />
    </section>
  );
}

/** A savings figure with its minus kept: "+" is not added, a negative number is never hidden. */
export function SavingsNumber({ value, currency, t, lang }: { value: number; currency: string; t: Messages; lang: Lang }) {
  return <span class="mono">{amountText(t, lang, value, currency)}</span>;
}

function DeviceRow({ device, currency, name, t, lang }: { device: SavingsDevice; currency: string; name: NameOf } & Omit<Ctx, 'zone'>) {
  const negative = roundTo(device.savings, 2) < 0;
  return (
    <>
      <dt>{name(device.id)}</dt>
      <dd data-row={`eco-savings-${device.id}`} data-negative={negative ? 'true' : undefined}>
        <SavingsNumber value={device.savings} currency={currency} t={t} lang={lang} />
        <span class="sub">
          {fill(t['dashboard.eco.savings.deviceDetail'], {
            energy: formatKwh(device.energyKwh, lang),
            value: formatFixed(device.valueGained, lang, 2),
            cost: formatFixed(device.extraCost, lang, 2),
            currency,
          })}
        </span>
        {negative && (
          <span class="sub">
            <StateLabel tone="warn" label={t['dashboard.eco.savings.negative']} />
          </span>
        )}
      </dd>
    </>
  );
}

/** Today's figure with what it is measured against and what it counts: shared by the card and the decision-log summary. */
export function SavingsBody({ total, savings, currency, ...ctx }: { total: SavingsTotal; savings: Savings; currency: string } & Ctx) {
  const { t, lang, zone } = ctx;
  return (
    <>
      <span data-savings="today">
        {total.periods === 0 ? <span class="unknown">{t['common.none']}</span> : <SavingsNumber value={total.savings} currency={currency} t={t} lang={lang} />}
      </span>
      <span class="sub">{t['dashboard.eco.savings.basis']}</span>
      <span class="sub" data-savings="since">
        {savings.since === null ? t['dashboard.eco.savings.noPeriod'] : fill(t['dashboard.eco.savings.since'], { time: `${dateOf(savings.since, lang, zone)} ${localClock(savings.since, lang, zone)}` })}
      </span>
      {total.fallback && (
        <span class="sub" data-savings="fallback">
          <span class="badge" data-tone="warn">
            <span class="dot" aria-hidden="true" />
            {t['dashboard.eco.basis.fallback']}
          </span>{' '}
          {fill(t['dashboard.eco.savings.fallback'], { count: String(total.fallbackPeriods) })}
        </span>
      )}
    </>
  );
}

function dateOf(at: Date, lang: Lang, zone: string): string {
  return new Intl.DateTimeFormat(lang, { timeZone: zone, day: '2-digit', month: '2-digit' }).format(at);
}

export function SavingsCard({ view, name, ...ctx }: { view: PollView<Economics>; name: NameOf } & Ctx) {
  const found = savingsOf(view);
  // No economics section (or no savings in it): no card, and the decision-log summary keeps its "not available" text.
  if (found === null) return null;
  const { savings, currency } = found;
  const { t, lang, zone } = ctx;
  const today = savings.today;
  const running = savings.running;
  return (
    <section class="card" aria-labelledby="eco-savings-title" data-card="savings">
      <header class="cardhead">
        <h2 id="eco-savings-title">{t['dashboard.eco.savings.title']}</h2>
        <p class="sub">{t['dashboard.eco.savings.sub']}</p>
      </header>
      <PollStatus view={view} evaluatedAt={found.economics.at} now={found.economics.at} lang={lang} t={t} />
      <dl class="dash-kv">
        <dt>{t['dashboard.eco.savings.today']}</dt>
        <dd data-row="eco-savings">
          <SavingsBody total={today} savings={savings} currency={currency} {...ctx} />
        </dd>
        {today.periods > 0 && (
          <>
            <dt>{t['dashboard.eco.savings.periods']}</dt>
            <dd data-row="eco-periods">
              <span class="mono">{today.periods}</span>
            </dd>
          </>
        )}
        {today.devices.length > 0 && (
          <Fragment>
            {today.devices.map((device) => (
              <DeviceRow key={device.id} device={device} currency={currency} name={name} t={t} lang={lang} />
            ))}
          </Fragment>
        )}
        {running !== null && (
          <>
            <dt>{t['dashboard.eco.savings.running']}</dt>
            <dd data-row="eco-running">
              <span class="mono">
                {fill(t['dashboard.eco.savings.runningValue'], {
                  from: localClock(running.start, lang, zone),
                  to: localClock(running.end, lang, zone),
                  amount: formatFixed(running.savings, lang, 2),
                  currency,
                })}
              </span>
              {running.fallback && <span class="sub">{t['dashboard.eco.savings.runningFallback']}</span>}
            </dd>
          </>
        )}
      </dl>
      {(today.unpricedPeriods > 0 || today.incompletePeriods > 0) && (
        <p class="note">{fill(t['dashboard.eco.savings.notCounted'], { unpriced: String(today.unpricedPeriods), incomplete: String(today.incompletePeriods) })}</p>
      )}
      <p class="dash-why">{t['dashboard.eco.savings.estimate']}</p>
      <p class="dash-why">{t['dashboard.eco.savings.how']}</p>
    </section>
  );
}

/** The device's sources in words (en, pl); zone ids in monospace. */
export function SourcesText({ sources, t }: { sources: string; t: Messages }) {
  if (sources === 'pv_only') return <>{t['device.eco.sources.pv_only']}</>;
  if (sources === 'any') return <>{t['device.eco.sources.any']}</>;
  const zones = sources.slice('pv_and_zone:'.length).split(',');
  return (
    <>
      {t['device.eco.sources.zones']}{' '}
      {zones.map((zone, i) => (
        <Fragment key={zone}>
          {i > 0 && ', '}
          <span class="mono">{zone}</span>
        </Fragment>
      ))}
    </>
  );
}

export function economicsDevice(economics: Economics | null, id: string): EconomicsDevice | null {
  return economics?.devices?.find((d) => d.id === id) ?? null;
}
