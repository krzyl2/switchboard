// The one place a request to the add-on is built. Every API client goes through it, so the headers,
// the no-store cache mode and the way an unexpected status reads are decided once. The strict JSON
// readers in strict.ts stay the validation entry: http.ts moves bytes, strict.ts judges them.
//
// URLs are relative on purpose: ingress serves the page under a per-session prefix, and a leading
// slash would address Home Assistant itself instead of this add-on.

export type HttpMethod = 'GET' | 'POST' | 'DELETE';

export interface RequestOptions {
  method?: HttpMethod;
  /** Sent as JSON, with the content type that makes a cross-site form unable to send it without a preflight. */
  body?: unknown;
  signal?: AbortSignal;
}

/** The raw response, for a caller that reads a status other than 2xx as an answer (a refusal, a 404). */
export function request(url: string, { method, body, signal }: RequestOptions = {}): Promise<Response> {
  const init: RequestInit = { signal, cache: 'no-store' };
  if (method !== undefined && method !== 'GET') init.method = method;
  init.headers = { Accept: 'application/json' };
  if (body !== undefined) {
    init.headers = { Accept: 'application/json', 'Content-Type': 'application/json' };
    init.body = JSON.stringify(body);
  }
  return fetch(url, init);
}

/** What a caller says about a request that did not end in a 2xx: "Plan request failed with HTTP 500." */
export function httpError(what: string, response: Response): Error {
  return new Error(`${what} request failed with HTTP ${response.status}.`);
}

/** A 2xx answer or an error; the JSON is returned untrusted, for strict.ts to judge. */
export async function readJson(response: Response, what: string): Promise<unknown> {
  if (!response.ok) throw httpError(what, response);
  return response.json();
}

export async function getJson(url: string, signal: AbortSignal | undefined, what: string): Promise<unknown> {
  return readJson(await request(url, { signal }), what);
}

/** Runs a request with its own abort controller, aborted when it has not settled after timeoutMs. */
export function withTimeout<T>(run: (signal: AbortSignal) => Promise<T>, timeoutMs: number): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  return run(controller.signal).finally(() => clearTimeout(timeout));
}
