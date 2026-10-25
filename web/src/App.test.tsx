import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/preact';
import { App } from './App';
import type { Status } from './api/status';
import { STATUS_REFRESH_INTERVAL_MS, STATUS_REQUEST_TIMEOUT_MS } from './config';

const START = new Date('2026-10-23T10:00:00Z');

const healthyShadow: Status = {
  version: '0.1.0',
  mode: 'shadow',
  homeAssistant: 'connected',
  mqtt: 'connected',
  healthy: true,
};

let fetchMock: Mock<typeof fetch>;

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

/** Advances the virtual clock and lets every resulting fetch, state update and render settle. */
async function advance(ms = 0): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

/** The value cell next to a row label of the status list. */
function rowValue(label: string): HTMLElement {
  const value = screen.getByText(label, { selector: 'dt' }).nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error(`No value next to "${label}".`);
  return value;
}

function toneOf(cell: HTMLElement): string | null {
  return cell.querySelector('.state')?.getAttribute('data-tone') ?? null;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(START);
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  // Since Stage 4 the dashboard is the home page; the status page lives at #/status.
  window.location.hash = '#/status';
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('status page', () => {
  it('requests the relative URL api/status, so the request stays under the ingress prefix', async () => {
    fetchMock.mockImplementation(() => respond(healthyShadow));
    render(<App lang="en" />);
    await advance();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // A leading slash would address Home Assistant itself instead of this add-on.
    expect(fetchMock.mock.calls[0]?.[0]).toBe('api/status');
  });

  it('shows every row for a connected, healthy add-on in shadow mode', async () => {
    fetchMock.mockImplementation(() => respond(healthyShadow));
    render(<App lang="en" />);
    await advance();

    expect(rowValue('Mode').textContent).toContain('Shadow');
    expect(rowValue('Mode').textContent).toContain('Computes and logs, never switches.');
    expect(rowValue('Home Assistant').textContent).toBe('Connected');
    expect(toneOf(rowValue('Home Assistant'))).toBe('ok');
    expect(rowValue('MQTT').textContent).toBe('Connected');
    expect(toneOf(rowValue('MQTT'))).toBe('ok');
    expect(rowValue('Health').textContent).toBe('Healthy');
    expect(toneOf(rowValue('Health'))).toBe('ok');
    expect(rowValue('Version').textContent).toBe('0.1.0');
    expect(screen.getByText(/Last updated/).querySelector('time')?.getAttribute('dateTime')).toBe(
      START.toISOString(),
    );
  });

  it('never shows a lost connection or an unhealthy add-on as fine', async () => {
    fetchMock.mockImplementation(() =>
      respond({ ...healthyShadow, homeAssistant: 'disconnected', mqtt: 'connecting', healthy: false }),
    );
    render(<App lang="en" />);
    await advance();

    expect(rowValue('Home Assistant').textContent).toBe('Disconnected');
    expect(toneOf(rowValue('Home Assistant'))).toBe('crit');
    expect(rowValue('MQTT').textContent).toBe('Connecting');
    expect(toneOf(rowValue('MQTT'))).toBe('warn');
    expect(rowValue('Health').textContent).toBe('Unhealthy');
    expect(toneOf(rowValue('Health'))).toBe('crit');
  });

  it('does not let active mode suggest that this build switches anything', async () => {
    fetchMock.mockImplementation(() => respond({ ...healthyShadow, mode: 'active' }));
    render(<App lang="en" />);
    await advance();

    expect(rowValue('Mode').textContent).toContain('Active');
    expect(rowValue('Mode').textContent).toContain('cannot switch anything');
  });

  it('refreshes on the configured interval', async () => {
    fetchMock
      .mockImplementationOnce(() => respond({ ...healthyShadow, homeAssistant: 'connecting' }))
      .mockImplementation(() => respond(healthyShadow));
    render(<App lang="en" />);
    await advance();
    expect(rowValue('Home Assistant').textContent).toBe('Connecting');

    await advance(STATUS_REFRESH_INTERVAL_MS - 1);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await advance(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(rowValue('Home Assistant').textContent).toBe('Connected');
  });

  // A frozen page must be distinguishable from a calm system: on failure the last known values are
  // withdrawn and the time of the last successful update is shown instead.
  it('shows "status unavailable" with the time of the last successful update when a refresh fails', async () => {
    fetchMock
      .mockImplementationOnce(() => respond(healthyShadow))
      .mockImplementation(() => Promise.reject(new TypeError('Failed to fetch')));
    render(<App lang="en" />);
    await advance();
    await advance(STATUS_REFRESH_INTERVAL_MS);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Status unavailable');
    expect(alert.textContent).toContain('Last successful update');
    // The first (successful) poll, not the failed one five seconds later.
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe(START.toISOString());
    expect(screen.queryByText('Connected')).toBeNull();
    expect(screen.queryByText('Healthy')).toBeNull();
  });

  it('says "never" when no request has succeeded yet', async () => {
    fetchMock.mockImplementation(() => Promise.reject(new TypeError('Failed to fetch')));
    render(<App lang="en" />);
    await advance();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Status unavailable');
    expect(alert.textContent).toContain('never');
    expect(alert.querySelector('time')).toBeNull();
  });

  it('treats a request that never answers as unavailable once the timeout elapses', async () => {
    fetchMock.mockImplementation(
      (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
        }),
    );
    render(<App lang="en" />);
    await advance(STATUS_REQUEST_TIMEOUT_MS - 1);
    expect(screen.queryByRole('alert')).toBeNull();

    await advance(1);
    expect(screen.getByRole('alert').textContent).toContain('Status unavailable');
  });

  it('treats an HTTP error as unavailable', async () => {
    fetchMock.mockImplementation(() => respond({ title: 'Service Unavailable' }, 503));
    render(<App lang="en" />);
    await advance();

    expect(screen.getByRole('alert').textContent).toContain('Status unavailable');
  });

  it('treats a payload it does not understand as unavailable instead of guessing', async () => {
    fetchMock.mockImplementation(() => respond({ ...healthyShadow, mode: 'Active' }));
    render(<App lang="en" />);
    await advance();

    expect(screen.getByRole('alert').textContent).toContain('Status unavailable');
    expect(screen.queryByText('Active')).toBeNull();
  });

  it('recovers when the add-on answers again', async () => {
    fetchMock
      .mockImplementationOnce(() => Promise.reject(new TypeError('Failed to fetch')))
      .mockImplementation(() => respond(healthyShadow));
    render(<App lang="en" />);
    await advance();
    expect(screen.getByRole('alert')).toBeTruthy();

    await advance(STATUS_REFRESH_INTERVAL_MS);
    expect(screen.queryByRole('alert')).toBeNull();
    expect(rowValue('Health').textContent).toBe('Healthy');
  });

  it('stops polling once the page is gone', async () => {
    fetchMock.mockImplementation(() => respond(healthyShadow));
    const { unmount } = render(<App lang="en" />);
    await advance();
    unmount();

    await advance(STATUS_REFRESH_INTERVAL_MS * 3);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('not-a-protective-device statement', () => {
  // SAFE-06 / product constraint: the page must never suggest that it protects the installation,
  // whatever the add-on's state, including before the first answer and when it is unreachable.
  it('is shown before the first status arrives', () => {
    fetchMock.mockImplementation(() => new Promise<Response>(() => undefined));
    render(<App lang="en" />);

    const note = screen.getByRole('note').textContent ?? '';
    expect(note).toContain('Not a protective device.');
    expect(note).toContain('best-effort load management based on Home Assistant data');
    expect(note).toContain('does not replace circuit breakers or EVSE protection');
  });

  it('is shown while the status is unavailable', async () => {
    fetchMock.mockImplementation(() => Promise.reject(new TypeError('Failed to fetch')));
    render(<App lang="en" />);
    await advance();

    expect(screen.getByRole('note').textContent).toContain('Not a protective device.');
  });
});

describe('Polish chrome', () => {
  it('renders labels, states and the statement in Polish', async () => {
    fetchMock.mockImplementation(() => respond(healthyShadow));
    render(<App lang="pl" />);
    await advance();

    expect(rowValue('Tryb pracy').textContent).toContain('Obserwacja');
    expect(rowValue('Home Assistant').textContent).toBe('Połączono');
    expect(rowValue('Kondycja').textContent).toBe('Sprawny');
    expect(screen.getByRole('note').textContent).toContain('To nie jest urządzenie zabezpieczające.');
  });
});
