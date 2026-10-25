import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/preact';
import { parsePower } from './api/power';
import power from './api/power.sample.json';
import { messages } from './i18n';
import { Age, PhaseBar, QuantityText, StateBadge } from './ui';

afterEach(cleanup);

const en = messages.en;
const pl = messages.pl;
const sample = parsePower(power);

describe('PhaseBar', () => {
  it('draws the measured current, its share of the rating and the three markers at their currents', () => {
    const l1 = sample.phases[0]!;
    const { container } = render(<PhaseBar phase={l1} t={en} lang="en" />);

    expect(container.querySelector('.pval')?.textContent).toBe('19.1 A76% of limit');
    const left = (selector: string) => parseFloat((container.querySelector(selector) as HTMLElement).style.left);
    // The dashed reserve marker sits left of the solid breaker marker, the continuous limit right of it.
    expect(left('.tick.reserve')).toBeLessThan(left('.tick.breaker'));
    expect(left('.tick.breaker')).toBeLessThan(left('.tick.continuous'));
    expect(container.querySelector('.bar')?.getAttribute('aria-label')).toBe('L1: 19.1 A, 76% of the breaker rating');
  });

  it('draws an exporting phase as one export segment', () => {
    const { container } = render(<PhaseBar phase={sample.phases[1]!} t={pl} lang="pl" />);

    expect([...container.querySelectorAll('.seg')].map((s) => s.getAttribute('data-kind'))).toEqual(['export']);
    expect(container.querySelector('.pval')?.textContent).toContain('6,5 A');
  });
});

describe('QuantityText', () => {
  it('shows a known power in kW with two decimals', () => {
    const { container } = render(<QuantityText quantity={{ w: 1500, unknown: null, reportedAt: null }} t={en} lang="en" />);
    expect(container.textContent).toBe('1.50 kW');
  });

  it('never shows an unknown power as a number: it names the reason and the devices', () => {
    const { container } = render(
      <QuantityText quantity={{ w: null, unknown: 'device_power_unknown', reportedAt: null }} t={en} lang="en" devices={['ev_charger']} />,
    );
    expect(container.textContent).toBe('unknown — a device draw is not fresh: ev_charger');
    expect(container.textContent).not.toMatch(/\d/);
  });

  it('shows something not declared as such, not as 0 W', () => {
    const { container } = render(<QuantityText quantity={{ w: null, unknown: 'not_declared', reportedAt: null }} t={pl} lang="pl" />);
    expect(container.textContent).toBe('nie zadeklarowano');
  });
});

describe('Age and StateBadge', () => {
  it('measures the age on the add-on clock, so development replay shows data-time ages', () => {
    const { container } = render(<Age reportedAt={new Date('2026-10-22T09:20:50Z')} now={new Date('2026-10-22T09:21:02Z')} lang="en" t={en} />);
    expect(container.textContent).toBe('12 s ago');
    expect(container.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-22T09:20:50.000Z');
  });

  it('shows no age for a value without a report time (a model value)', () => {
    const { container } = render(<Age reportedAt={null} now={new Date()} lang="en" t={en} />);
    expect(container.textContent).toBe('');
  });

  it('pairs the colour with a text label: colour alone never carries a state', () => {
    const { container } = render(<StateBadge tone="warn" label="would switch off" />);
    expect(container.querySelector('.badge')?.getAttribute('data-tone')).toBe('warn');
    expect(container.textContent).toBe('would switch off');
  });
});
