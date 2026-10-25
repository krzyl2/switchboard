import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import {
  INCONCLUSIVE_REASONS,
  PHASE_STEP_VERDICTS,
  READOUT_STATUSES,
  SETUP_ISSUE_CODES,
  emptyDraft,
  fetchDiscovery,
  parseDiscovery,
  parseDraftView,
  parseEvidence,
  parseReadouts,
  parseRefusal,
  parseSaved,
  parseSelfTest,
  parseSelfTestStarted,
  parseTemplates,
  parseValidation,
  saveDraft,
  startSelfTest,
  validateDraft,
  DiscoveryUnavailable,
  cancelActiveTest,
  fetchActiveTestPreconditions,
  parseActiveTest,
  parseActiveTestPreconditions,
  startActiveTest,
} from './setup';
import activePreconditionsSample from './setup-active-test-preconditions.sample.json';
import activeRefusedSample from './setup-active-test-refused.sample.json';
import activeTestSample from './setup-active-test.sample.json';
import discoverySample from './setup-discovery.sample.json';
import draftSample from './setup-draft.sample.json';
import evidenceSample from './setup-evidence.sample.json';
import readoutsSample from './setup-readouts.sample.json';
import refusalSample from './setup-refusal.sample.json';
import savedSample from './setup-saved.sample.json';
import selfTestSample from './setup-self-test.sample.json';
import startedSample from './setup-self-test-started.sample.json';
import templatesSample from './setup-templates.sample.json';
import validationSample from './setup-validation.sample.json';

// The samples have the keys the setup endpoints really write (SetupSpaContractTests compares them).
function copy<T>(sample: T, change?: (p: any) => void): T {
  const payload = structuredClone(sample);
  change?.(payload);
  return payload;
}

let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) } as Response);
}

describe('discovery (UI-03)', () => {
  it('accepts the documented payload and marks derived and unverifiable entities', () => {
    const parsed = parseDiscovery(copy(discoverySample));

    const usage = parsed.entities.find((e) => e.entityId === 'sensor.usage')!;
    expect(usage.derived).toEqual({ platform: 'template' });
    expect(parsed.entities.find((e) => e.entityId === 'sensor.house_load_yaml')?.inRegistry).toBe(false);
    expect(parsed.entities.find((e) => e.entityId === 'sensor.solaredge_m1_ac_current_a')).toMatchObject({ unit: 'A', deviceClass: 'current', derived: null });
    expect(parsed.registryAvailable).toBe(true);
  });

  it.each([
    ['an entity without its derived key', (p: any) => delete p.entities[0].derived],
    ['a derived without its platform', (p: any) => (p.entities[0].derived = {})],
    ['a missing truncation flag', (p: any) => delete p.truncated],
    ['an entity id that is not text', (p: any) => (p.entities[0].entityId = 7)],
  ])('refuses %s', (_case, change) => {
    expect(() => parseDiscovery(copy(discoverySample, change))).toThrow();
  });

  it('tells a Home Assistant outage apart from a broken answer', async () => {
    fetchMock.mockReturnValue(respond({ code: 'home_assistant_unavailable', message: 'down' }, 503));

    await expect(fetchDiscovery(new AbortController().signal)).rejects.toBeInstanceOf(DiscoveryUnavailable);
  });
});

describe('readouts (UI-04)', () => {
  it('accepts the documented payload with ages and refresh', () => {
    const parsed = parseReadouts(copy(readoutsSample));

    expect(parsed.items.map((i) => i.status)).toEqual(['found', 'found', 'not_found']);
    expect(parsed.items[0]?.refresh.intervalS).toBe(10);
    expect(parsed.items[2]?.ageS).toBeNull();
  });

  it.each(READOUT_STATUSES)('accepts the status %s', (status) => {
    expect(parseReadouts(copy(readoutsSample, (p) => (p.items[0].status = status))).items[0]?.status).toBe(status);
  });

  it('refuses an unknown status', () => {
    expect(() => parseReadouts(copy(readoutsSample, (p) => (p.items[0].status = 'stale')))).toThrow();
  });
});

describe('draft (UI-06)', () => {
  it('reads the existing configuration with every device and what is preserved', () => {
    const view = parseDraftView(copy(draftSample));

    expect(view.fileState).toBe('valid');
    expect(view.hasComments).toBe(true);
    expect(view.draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    expect(view.draft.devices.every((d) => d.existing && d.editable)).toBe(true);
    expect(view.draft.meter.phases.L1?.current?.entity).toBe('sensor.solaredge_m1_ac_current_a');
    expect(view.draft.sources.inverter?.unavailableAsZero).toBe(true);
  });

  it('keeps odd values of a hand-written file as text, so they can be fixed in the wizard', () => {
    const view = parseDraftView(copy(draftSample, (p) => (p.draft.connection.topology = 'three-phase')));

    expect(view.draft.connection.topology).toBe('three-phase');
  });

  it.each([
    ['a device without its editable flag', (p: any) => delete p.draft.devices[0].editable],
    ['a missing acknowledgements block', (p: any) => delete p.draft.acknowledgements],
    ['an unknown file state', (p: any) => (p.fileState = 'broken')],
  ])('refuses %s', (_case, change) => {
    expect(() => parseDraftView(copy(draftSample, change))).toThrow();
  });

  it('reads the stop actuator of a modulating device and the start policy, null when not written', () => {
    const view = parseDraftView(
      copy(draftSample, (p) => Object.assign(p.draft.devices[3], { stopActuator: 'switch.goe_allow_charging', start: 'headroom' })),
    );

    expect(view.draft.devices[3]).toMatchObject({ stopActuator: 'switch.goe_allow_charging', start: 'headroom' });
    expect(view.draft.devices[0]).toMatchObject({ stopActuator: null, start: null });
  });

  it('an empty draft round-trips through the parser', () => {
    expect(parseDraftView({ revision: null, fileState: 'missing', hasComments: false, unreadable: false, draft: emptyDraft(), preserved: [], fileIssues: [] }).draft).toEqual(emptyDraft());
  });
});

describe('validation (CONN-06, UI-05)', () => {
  it('accepts the documented payload with the reserve explanation and self-test verdicts', () => {
    const parsed = parseValidation(copy(validationSample));

    expect(parsed.valid).toBe(true);
    expect(parsed.reserve?.explanation.reaction).toBe('hold');
    expect(parsed.reserve?.perPhase.map((p) => p.phase)).toEqual(['L1', 'L2', 'L3']);
    expect(parsed.selfTest.length).toBe(3);
    expect(parsed.issues.every((i) => i.field !== null)).toBe(true);
  });

  it.each(SETUP_ISSUE_CODES)('accepts the issue code %s', (code) => {
    expect(parseValidation(copy(validationSample, (p) => (p.issues[0].code = code))).issues[0]?.code).toBe(code);
  });

  it.each(PHASE_STEP_VERDICTS)('accepts the self-test verdict %s', (verdict) => {
    expect(parseValidation(copy(validationSample, (p) => (p.selfTest[0].verdict = verdict))).selfTest[0]?.verdict).toBe(verdict);
  });

  it.each([
    ['an unknown issue code', (p: any) => (p.issues[0].code = 'made_up')],
    ['an unknown severity', (p: any) => (p.issues[0].severity = 'info')],
    ['args that are not text', (p: any) => (p.issues[0].args = { entity: 3 })],
    ['an unknown tier reaction', (p: any) => (p.reserve.explanation.reaction = 'panic')],
    ['a passed self-test (there is no such verdict)', (p: any) => (p.selfTest[0].verdict = 'passed')],
  ])('refuses %s', (_case, change) => {
    expect(() => parseValidation(copy(validationSample, change))).toThrow();
  });

  it('posts the draft as JSON with its base revision', async () => {
    fetchMock.mockReturnValue(respond(copy(validationSample)));

    await validateDraft(emptyDraft(), 'ABC');

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('api/setup/validate');
    expect(init?.method).toBe('POST');
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ draft: emptyDraft(), baseRevision: 'ABC' });
  });
});

describe('saving (UI-06, SAFE-06)', () => {
  it('reads a saved answer', () => {
    expect(parseSaved(copy(savedSample))).toMatchObject({ kind: 'saved', reloaded: false });
  });

  it('reads a refusal with its issues and a conflict with the current revision', async () => {
    fetchMock.mockReturnValueOnce(respond(copy(refusalSample), 422));
    const refused = await saveDraft(emptyDraft(), null);
    expect(refused.kind).toBe('refused');
    expect(refused.kind === 'refused' && refused.issues.some((i) => i.field === 'acknowledgements.notProtective')).toBe(true);

    fetchMock.mockReturnValueOnce(respond({ code: 'revision_conflict', issues: [], currentRevision: 'NEW' }, 409));
    expect(await saveDraft(emptyDraft(), 'OLD')).toEqual({ kind: 'conflict', currentRevision: 'NEW', issues: [] });
  });

  it('refuses a 409 that is not a revision conflict', () => {
    expect(() => parseRefusal({ code: 'other', issues: [], currentRevision: null }, 409)).toThrow();
  });
});

describe('templates (UI-02)', () => {
  it('reads the four cards with their prefill and what they inherit', () => {
    const parsed = parseTemplates(copy(templatesSample));

    expect(parsed.templates.map((t) => t.id)).toEqual(['ev_charger', 'water_heater_element', 'heat_pump', 'generic_onoff']);
    expect(parsed.templates[0]).toMatchObject({ type: 'modulating', prefill: { type: 'modulating', min: 6, max: 16, step: 1 }, drawRecommended: true });
    expect(parsed.templates[1]?.inherited).toEqual({ reactionTime: '1s', minOn: '6m', minOff: '90s' });
    expect(parsed.prefillVoltageV).toBe(230);
  });
});

describe('self-test (CONN-05)', () => {
  it('reads a watch and its observations', () => {
    expect(parseSelfTestStarted(copy(startedSample)).status).toBe('watching');
    const parsed = parseSelfTest(copy(selfTestSample));

    expect(parsed.status).toBe('finished');
    expect(parsed.verdict.verdict).toBe('confirmed');
    expect(parsed.observations[0]).toMatchObject({ transition: 'on', observedPhase: 'L2', source: 'watch' });
    expect(parsed.observations[0]?.deltas.L2).toBeGreaterThan(1600);
  });

  it.each(INCONCLUSIVE_REASONS)('accepts the inconclusive reason %s', (reason) => {
    const parsed = parseSelfTest(copy(selfTestSample, (p) => Object.assign(p.verdict, { verdict: 'inconclusive', reason })));
    expect(parsed.verdict.reason).toBe(reason);
  });

  it('refuses a delta on a phase that does not exist', () => {
    expect(() => parseSelfTest(copy(selfTestSample, (p) => (p.observations[0].deltas.L4 = 1)))).toThrow();
  });

  it('reads the recorded evidence per device', () => {
    const devices = parseEvidence(copy(evidenceSample));
    expect(devices.find((d) => d.stateEntity === 'switch.grzalka_1')?.observations.some((o) => o.source === 'recorded')).toBe(true);
  });

  it('tells a busy watch and a refusal apart from a start', async () => {
    fetchMock.mockReturnValueOnce(respond({ code: 'self_test_busy', id: 'abc' }, 409));
    expect(await startSelfTest(emptyDraft(), { device: { index: 0 } })).toEqual({ kind: 'busy', id: 'abc' });

    fetchMock.mockReturnValueOnce(respond(copy(startedSample), 202));
    expect((await startSelfTest(emptyDraft(), { device: { index: 0 } })).kind).toBe('started');
    expect(JSON.parse(String(fetchMock.mock.calls[1]![1]?.body))).toEqual({ draft: emptyDraft(), device: { index: 0 } });
  });
});

// CONN-05, Stage C: the active phase self-test. Switchboard switches a declared, idle relay stage on
// and off itself, only in effective active mode; the page must never show a test as startable when
// the add-on names a reason, nor an aborted test without its reason.
describe('active self-test (CONN-05)', () => {
  /** A finished test as the add-on writes it (ActivePhaseTestTests generates the sample): on and off, both judged. */
  function finished(change?: (p: any) => void): unknown {
    return copy(activeTestSample, change);
  }

  it('reads the preconditions the add-on refuses with', () => {
    const preconditions = parseActiveTestPreconditions(copy(activePreconditionsSample));

    expect(preconditions.ok).toBe(false);
    expect(preconditions.refusals.map((r) => r.code)).toContain('control_not_active');
  });

  it('reads startable preconditions with the expected duration', () => {
    expect(parseActiveTestPreconditions({ ok: true, refusals: [], expectedDurationS: 250 })).toEqual({ ok: true, refusals: [], expectedDurationS: 250 });
  });

  it.each([
    ['ok next to a refusal', (p: any) => (p.ok = true)],
    ['not ok without a refusal', (p: any) => (p.refusals = [])],
    ['an unknown refusal code', (p: any) => (p.refusals[0].code = 'too_cold')],
  ])('refuses preconditions with %s', (_case, change) => {
    expect(() => parseActiveTestPreconditions(copy(activePreconditionsSample, change))).toThrow();
  });

  it('reads a finished test with its own observations and verdict', () => {
    const test = parseActiveTest(finished());

    expect(test).toMatchObject({ device: 'heater_l1', state: 'finished', phase: 'done', expectedPhase: 'L1', abortReason: null });
    expect(test.verdict?.verdict).toBe('confirmed');
    expect(test.observations.map((o) => o.source)).toEqual(['active', 'active']);
  });

  it('reads an aborted test with its reason', () => {
    const test = parseActiveTest(finished((p) => Object.assign(p, { state: 'aborted', abortReason: 'guard_acted', verdict: null, observations: [], offAt: null })));

    expect(test).toMatchObject({ state: 'aborted', abortReason: 'guard_acted', verdict: null });
  });

  it.each([
    ['an aborted test without its reason', (p: any) => (p.state = 'aborted')],
    ['a reason next to a finished test', (p: any) => (p.abortReason = 'cancelled')],
    ['an unknown phase', (p: any) => (p.phase = 'cooling')],
    ['an unknown abort reason', (p: any) => Object.assign(p, { state: 'aborted', abortReason: 'bored' })],
    ['stage 0', (p: any) => (p.stage = 0)],
  ])('refuses %s', (_case, change) => {
    expect(() => parseActiveTest(finished(change))).toThrow();
  });

  it('asks for the preconditions of one device stage', async () => {
    fetchMock.mockReturnValue(respond(copy(activePreconditionsSample)));

    await fetchActiveTestPreconditions('heater_l1', 1);

    expect(String(fetchMock.mock.calls[0]![0])).toBe('api/setup/active-test/preconditions?device=heater_l1&stage=1');
  });

  it('starts with a JSON post and reads a refusal with its reasons', async () => {
    fetchMock.mockReturnValueOnce(respond({ id: 'abc', expectedDurationS: 250 }, 202)).mockReturnValueOnce(respond(copy(activeRefusedSample), 409));

    expect(await startActiveTest('heater_l1', 1)).toEqual({ kind: 'started', id: 'abc', expectedDurationS: 250 });
    const refused = await startActiveTest('heater_l1', 1);
    expect(refused.kind).toBe('refused');
    const [, init] = fetchMock.mock.calls[0]!;
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ device: 'heater_l1', stage: 1 });
  });

  it('cancels a test, and says when there is none', async () => {
    fetchMock.mockReturnValueOnce(respond(null, 204)).mockReturnValueOnce(respond(null, 404));

    expect(await cancelActiveTest('abc')).toBe(true);
    expect(await cancelActiveTest('abc')).toBe(false);
    expect(fetchMock.mock.calls[0]![1]?.method).toBe('DELETE');
  });
});
