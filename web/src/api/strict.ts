// Strict JSON readers shared by the API parsers. Each one accepts exactly one shape and throws with
// the path of the first value that does not fit, so a contract break shows as "unavailable" instead
// of a guess on screen.

export type Json = Record<string, unknown>;

export function fail(path: string, expected: string): never {
  throw new Error(`Payload: ${path} is not ${expected}.`);
}

// A key that is absent is an error even where null is allowed: the add-on always writes every key.
export function field(r: Json, key: string, path: string): unknown {
  if (!(key in r)) fail(`${path}.${key}`, 'present');
  return r[key];
}

export function object(raw: unknown, path: string): Json {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) fail(path, 'an object');
  return raw as Json;
}

export function array(raw: unknown, path: string): unknown[] {
  if (!Array.isArray(raw)) fail(path, 'an array');
  return raw;
}

export function text(raw: unknown, path: string): string {
  if (typeof raw !== 'string') fail(path, 'a string');
  return raw;
}

export function number(raw: unknown, path: string): number {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) fail(path, 'a finite number');
  return raw;
}

export function count(raw: unknown, path: string): number {
  if (typeof raw !== 'number' || !Number.isInteger(raw) || raw < 0) fail(path, 'a non-negative integer');
  return raw;
}

/** A count of at least 1: a stage number, a rank. */
export function positive(raw: unknown, path: string): number {
  const value = count(raw, path);
  if (value < 1) fail(path, 'a positive integer');
  return value;
}

export function bool(raw: unknown, path: string): boolean {
  if (typeof raw !== 'boolean') fail(path, 'a boolean');
  return raw;
}

export function instant(raw: unknown, path: string): Date {
  const at = new Date(text(raw, path));
  if (Number.isNaN(at.getTime())) fail(path, 'a timestamp');
  return at;
}

export function oneOf<T extends string>(allowed: readonly T[], raw: unknown, path: string): T {
  if (typeof raw !== 'string' || !(allowed as readonly string[]).includes(raw)) {
    fail(path, `one of ${allowed.join(', ')}`);
  }
  return raw as T;
}

export function nullable<T>(raw: unknown, path: string, parse: (raw: unknown, path: string) => T): T | null {
  return raw === null ? null : parse(raw, path);
}

export function list<T>(raw: unknown, path: string, item: (raw: unknown, path: string) => T): T[] {
  return array(raw, path).map((value, i) => item(value, `${path}[${i}]`));
}
