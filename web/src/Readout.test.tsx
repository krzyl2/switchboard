import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/preact';
import { ReadoutLine } from './Readout';
import { parseReadouts } from './api/setup';
import readoutsSample from './api/setup-readouts.sample.json';
import { messages } from './i18n';

const t = messages.en;
const pl = messages.pl;

afterEach(cleanup);

// UI-04: a wrong pick is visible at once: the value, the age of its last report (ticking with each
// poll, so a frozen sensor shows), and the detected refresh.
describe('readout', () => {
  it('shows value and age and ticks', () => {
    const first = parseReadouts(structuredClone(readoutsSample)).items[0]!;
    const { container, rerender } = render(<ReadoutLine readout={{ ...first, ageS: 4 }} t={t} lang="en" />);

    expect(container.querySelector('.value')?.textContent).toBe('0.80 A');
    expect(container.querySelector('.age')?.textContent).toBe('reported 4 s ago');
    expect(container.querySelector('.refresh')?.textContent).toBe('every 10 s');

    rerender(<ReadoutLine readout={{ ...first, ageS: 6 }} t={t} lang="en" />);
    expect(container.querySelector('.age')?.textContent).toBe('reported 6 s ago');
  });

  it('counts the reports while the refresh is still being detected', () => {
    const relay = parseReadouts(structuredClone(readoutsSample)).items[1]!;
    const { container } = render(<ReadoutLine readout={{ ...relay, refresh: { intervalS: null, samples: 3, needed: 8 } }} t={pl} lang="pl" />);

    expect(container.querySelector('.refresh')?.textContent).toBe('odświeżanie: 3 z 8 odczytów');
  });

  it('says plainly when an entity does not exist or reports no value', () => {
    const missing = parseReadouts(structuredClone(readoutsSample)).items[2]!;
    const { container, rerender } = render(<ReadoutLine readout={missing} t={t} lang="en" />);
    expect(container.textContent).toBe('not found in Home Assistant');

    rerender(<ReadoutLine readout={{ ...missing, status: 'unavailable', state: 'unavailable' }} t={t} lang="en" />);
    expect(container.textContent).toBe('reports unavailable');

    rerender(<ReadoutLine readout={undefined} t={t} lang="en" />);
    expect(container.textContent).toBe('no value');
  });
});
