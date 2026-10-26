import { fetchBalance, type BalanceUnknownReason, type HourlyBalance } from './api/balance';
import { POWER_REFRESH_INTERVAL_MS, POWER_REQUEST_TIMEOUT_MS } from './config';
import { formatCountdown, formatNumber } from './format';
import { messages, type Lang, type Messages } from './i18n';
import { PollStatus } from './ui';
import { usePoll } from './usePoll';

// The hourly balance on the dashboard (BAL-07, docs/13-HOURLY-BALANCE.md "Presentation"): the balance
// of the running hour, its projection at the next :00 for the load as it is, the target band and the
// time to :00, as GET api/balance reports them. Wh and W, positive = export. It only reads.

/** A signed energy: "+240 Wh" is net export, "-120 Wh" net import. */
export function whText(value: number, lang: Lang): string {
  const text = formatNumber(value, lang, 0);
  return `${value > 0 && text !== '0' ? '+' : ''}${text} Wh`;
}

export function wattsText(value: number, lang: Lang): string {
  return `${formatNumber(value, lang, 0)} W`;
}

/** The time to :00 the rule used while it had the balance, else the monitor's; null when neither is known. */
export function secondsToHour(balance: HourlyBalance): number | null {
  return balance.known ? balance.ruleSecondsToNextHour : balance.secondsToNextHour;
}

function unknownText(t: Messages, reason: BalanceUnknownReason): string {
  return t[`dashboard.hb.unknown.${reason}`];
}

function Figures({ balance, t, lang }: { balance: HourlyBalance; t: Messages; lang: Lang }) {
  const remaining = secondsToHour(balance);
  const band = balance.band;
  const unknown = balance.known ? null : unknownText(t, balance.unknownReason);
  return (
    <>
      <dl class="dash-kv">
        <dt>{t['dashboard.hb.balance']}</dt>
        <dd data-row="hb-balance">
          {balance.wh !== null ? <span class="mono">{whText(balance.wh, lang)}</span> : <span class="unknown">{unknownText(t, balance.reason)}</span>}
          {balance.wh !== null && <span class="sub-inline"> ({t[balance.wh > 0 ? 'direction.export' : 'direction.import']})</span>}
        </dd>
        <dt>{t['dashboard.hb.projection']}</dt>
        <dd data-row="hb-projection">
          {balance.projectionWh !== null ? <span class="mono">{whText(balance.projectionWh, lang)}</span> : <span class="unknown">{unknown ?? t['common.none']}</span>}
        </dd>
        <dt>{t['dashboard.hb.band']}</dt>
        <dd data-row="hb-band">
          {band !== null ? (
            <>
              <span class="mono">{`${formatNumber(band.loWh, lang, 0)} … ${formatNumber(band.hiWh, lang, 0)} Wh`}</span>
              {band.burning && <span class="sub-inline"> ({t['dashboard.hb.burning']})</span>}
            </>
          ) : (
            <span class="unknown">{unknown ?? t['common.none']}</span>
          )}
        </dd>
        <dt>{t['dashboard.hb.remaining']}</dt>
        <dd data-row="hb-remaining">{remaining !== null ? <span class="mono">{formatCountdown(remaining)}</span> : <span class="unknown">{t['common.none']}</span>}</dd>
        <dt>{t['dashboard.hb.free']}</dt>
        <dd data-row="hb-free">{balance.freeW !== null ? <span class="mono">{wattsText(balance.freeW, lang)}</span> : <span class="unknown">{t['common.none']}</span>}</dd>
        <dt>{t['dashboard.hb.load']}</dt>
        <dd data-row="hb-load">{balance.loadW !== null ? <span class="mono">{wattsText(balance.loadW, lang)}</span> : <span class="unknown">{t['common.none']}</span>}</dd>
      </dl>
      {unknown !== null && balance.wh !== null && <p class="note">{unknown}</p>}
      <p class="dash-why">{t['dashboard.hb.why']}</p>
    </>
  );
}

export function HourlyBalanceCard({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = usePoll(fetchBalance, POWER_REFRESH_INTERVAL_MS, POWER_REQUEST_TIMEOUT_MS);
  return (
    <section class="card" aria-labelledby="hb-title">
      <header class="cardhead">
        <h2 id="hb-title">{t['dashboard.hb.title']}</h2>
        <p class="sub">{t['dashboard.hb.sub']}</p>
      </header>
      <PollStatus view={view} lang={lang} t={t} />
      {view.kind === 'live' && (view.data.configured ? <Figures balance={view.data} t={t} lang={lang} /> : <p class="note">{t['dashboard.hb.notConfigured']}</p>)}
    </section>
  );
}
