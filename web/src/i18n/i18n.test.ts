import { describe, expect, it } from 'vitest';
import { detectLanguage, fill, messages } from '.';
import { dashboard } from './dashboard';
import { device } from './device';
import { flows } from './flows';
import { limits } from './limits';
import { log } from './log';
import enSource from './en.ts?raw';
import { setup } from './setup';
import { setupConnection } from './setupConnection';
import { setupLoads } from './setupLoads';
import { setupMeter } from './setupMeter';
import { setupMode } from './setupMode';
import { setupSources } from './setupSources';
import { shell } from './shell';

// UI-14: every piece of chrome must exist in both languages. A key present in one file only would
// render as undefined (blank) for half of the users, and an empty string hides a label silently.
describe('translations', () => {
  it('en and pl define exactly the same keys', () => {
    expect(Object.keys(messages.pl).sort()).toEqual(Object.keys(messages.en).sort());
  });

  it.each(Object.entries(messages))('%s has no empty strings', (_lang, table) => {
    const empty = Object.entries(table)
      .filter(([, text]) => typeof text !== 'string' || text.trim() === '')
      .map(([key]) => key);
    expect(empty).toEqual([]);
  });

  // fill() puts numbers into {name} placeholders; a translation that drops or renames one would show
  // a sentence without its number, or fail to render.
  it('en and pl use the same placeholders in every text', () => {
    const names = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    const differing = Object.keys(messages.en).filter(
      (key) =>
        JSON.stringify(names(messages.en[key as keyof typeof messages.en])) !==
        JSON.stringify(names(messages.pl[key as keyof typeof messages.pl])),
    );
    expect(differing).toEqual([]);
  });

  it('pl is actually translated, not a copy of en', () => {
    expect(messages.pl.notProtectiveTitle).not.toBe(messages.en.notProtectiveTitle);
    expect(messages.pl.modeShadowNote).not.toBe(messages.en.modeShadowNote);
  });
});

describe('detectLanguage', () => {
  it.each(['pl', 'pl-PL', 'PL-pl'])('picks Polish for %s', (browserLanguage) => {
    expect(detectLanguage(browserLanguage)).toBe('pl');
  });

  // "plt" (Plateau Malagasy) starts with the letters "pl" but is not Polish.
  it.each(['en-US', 'de-DE', 'plt', '', undefined])('falls back to English for %s', (browserLanguage) => {
    expect(detectLanguage(browserLanguage)).toBe('en');
  });
});

describe('fill', () => {
  it('puts every value into its placeholder', () => {
    expect(fill('{from}–{upTo}×', { from: '1,25', upTo: '1,45' })).toBe('1,25–1,45×');
  });

  // A sentence that silently lost its number would read as complete.
  it('refuses a placeholder without a value', () => {
    expect(() => fill('full again in {time}', {})).toThrow();
  });
});

// Stage 4: each runtime screen keeps its texts in its own file so parallel work never edits the same
// file. A key defined twice would be silently overwritten by the later spread, so the files must not
// overlap, and every key of a screen file starts with that screen's own prefix.
describe('per-screen translation files', () => {
  const files = { shell, dashboard, flows, device, limits, log, setup, setupMeter, setupConnection, setupSources, setupLoads, setupMode };
  const prefixes: Record<string, string[]> = {
    shell: ['shell.', 'nav.', 'theme.', 'common.', 'age.', 'poll.', 'powerUnknown.', 'generationNote.', 'segment.', 'source.', 'bar.'],
    dashboard: ['dashboard.'],
    flows: ['flows.'],
    device: ['device.'],
    limits: ['limits.'],
    log: ['log.', 'decisionKind.'],
    setup: ['setup.'],
    setupMeter: ['setupMeter.'],
    setupConnection: ['setupConnection.'],
    setupSources: ['setupSources.'],
    setupLoads: ['setupLoads.'],
    setupMode: ['setupMode.'],
  };

  it.each(Object.entries(files))('%s: en and pl define exactly the same keys, each with its prefix', (name, file) => {
    expect(Object.keys(file.pl).sort()).toEqual(Object.keys(file.en).sort());
    const outside = Object.keys(file.en).filter((key) => !prefixes[name]!.some((prefix) => key.startsWith(prefix)));
    expect(outside).toEqual([]);
  });

  it('no key is defined in two files, or in a file and the Stage 0-3 table', () => {
    const screens = Object.values(files).flatMap((file) => Object.keys(file.en));
    const total = Object.keys(messages.en).length;

    expect(new Set(screens).size).toBe(screens.length);
    // The Stage 0-3 keys are the literal keys of en.ts itself; none of them may reappear in a screen
    // file, where the later spread would silently replace its text.
    const core = [...enSource.matchAll(/^ {2}'?([A-Za-z0-9_.]+)'?:/gm)].map((m) => m[1]!);
    expect(core.length).toBeGreaterThan(300);
    expect(screens.filter((key) => core.includes(key))).toEqual([]);
    expect(total).toBe(core.length + screens.length);
  });

  it('the Polish chrome uses the mockup vocabulary with its diacritics', () => {
    expect(messages.pl['nav.limits']).toBe('Limity i przeciążenia');
    expect(messages.pl['nav.flows']).toBe('Przepływy');
    expect(messages.pl['nav.device']).toBe('Karta urządzenia');
    expect(messages.pl['nav.log']).toBe('Dziennik decyzji');
    expect(messages.pl['shell.shadowPill']).toContain('niczego nie przełącza');
  });
});

// No copy may suggest that Switchboard is a protective device (CLAUDE.md): wherever a text names a
// protective device it says Switchboard is not one, and no text claims it protects or guarantees.
describe('no copy claims protection', () => {
  const claims = [
    /\bswitchboard (protects|guarantees|prevents|keeps you safe)/i,
    /\b(will protect|protects your|prevents? (a |the )?trip)/i,
    /\bswitchboard (chroni|zabezpiecza|gwarantuje)/i,
    /\b(chroni przed|zapobiega zadziałaniu|gwarantuje bezpieczeństwo)/i,
  ];

  it.each(Object.entries(messages))('%s: no text claims protection', (_lang, table) => {
    const offenders = Object.entries(table).filter(([, text]) => claims.some((claim) => claim.test(text)));
    expect(offenders).toEqual([]);
  });

  it.each(Object.entries(messages))('%s: a text naming a protective device negates it', (_lang, table) => {
    const naming = Object.entries(table).filter(([, text]) => /protective device|urządzeni\w* zabezpieczając/i.test(text));
    expect(naming.length).toBeGreaterThan(0);
    expect(naming.filter(([, text]) => !/\bnot a protective device|nie jest urządzeni\w* zabezpieczając\w*/i.test(text))).toEqual([]);
  });
});
