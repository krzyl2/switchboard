import type { ComponentType } from 'preact';
import { render } from '@testing-library/preact';
import type { Mock } from 'vitest';
import activePreconditionsSample from './api/setup-active-test-preconditions.sample.json';
import activeRefusedSample from './api/setup-active-test-refused.sample.json';
import activeTestSample from './api/setup-active-test.sample.json';
import discoverySample from './api/setup-discovery.sample.json';
import draftSample from './api/setup-draft.sample.json';
import evidenceSample from './api/setup-evidence.sample.json';
import startedSample from './api/setup-self-test-started.sample.json';
import selfTestSample from './api/setup-self-test.sample.json';
import templatesSample from './api/setup-templates.sample.json';
import validationSample from './api/setup-validation.sample.json';
import { messages, type Lang } from './i18n';
import { advance, respond } from './screens.test-support';
import type { StepName } from './setupDraft';
import { useSetup, type SetupState, type StepProps } from './useSetup';

// Steps 3 and 4 against the real shared draft hook (useSetup) and a fetch double that answers like
// the add-on: draft, templates, discovery, readouts of whatever is asked, evidence, validation, the
// passive self-test and the automatic one. Anything else is a request the steps must not make: 404.

export const samples = {
  activePreconditionsSample,
  activeRefusedSample,
  activeTestSample,
  discoverySample,
  draftSample,
  evidenceSample,
  startedSample,
  selfTestSample,
  templatesSample,
  validationSample,
};

export type Payload = Record<string, unknown>;

export interface Server {
  draft: unknown;
  discovery: unknown;
  templates: unknown;
  validation: () => unknown;
  evidence: unknown;
  /** Answers of GET api/setup/self-test/<id>, one per poll; the last one repeats. */
  selfTests: unknown[];
  start: () => Promise<Response>;
  /** GET api/setup/readouts for the asked URL (default: every entity found, switches off). */
  readouts: (url: string) => unknown;
  /** GET api/setup/active-test/preconditions (default: refused, control not active). */
  activePreconditions: () => unknown;
  /** POST api/setup/active-test (default: refused with the sample's reasons). */
  activeStart: () => Promise<Response>;
  /** Answers of GET api/setup/active-test/<id>, one per poll; the last one repeats. */
  activeTests: unknown[];
}

/** A clean install: no file, nothing mapped, no device. */
export function cleanDraft(): Payload {
  const view = structuredClone(draftSample) as Payload & { draft: Payload };
  return {
    ...view,
    revision: null,
    fileState: 'missing',
    hasComments: false,
    preserved: [],
    fileIssues: [],
    draft: {
      ...view.draft,
      connection: { topology: null, breakerRatingA: null, characteristic: null, voltageV: null, reserveW: null },
      meter: { currentSign: null, currentDirection: null, powerSign: null, phases: { L1: null, L2: null, L3: null }, total: { power: null, current: null } },
      sources: { inverter: null },
      devices: [],
    },
  };
}

/** Each asked entity found, reporting 1234 three seconds ago, refresh every 10 s. */
export function readoutsFor(url: string): Payload {
  const entities = new URLSearchParams(url.split('?')[1] ?? '').getAll('entity');
  return {
    at: '2026-10-22T07:39:03+00:00',
    items: entities.map((entityId) => ({
      entityId,
      status: 'found',
      state: entityId.startsWith('switch.') ? 'off' : '1234',
      unit: entityId.startsWith('switch.') ? null : 'W',
      lastReported: '2026-10-22T07:39:00+00:00',
      ageS: 3,
      refresh: { intervalS: 10, samples: 9, needed: 8 },
    })),
  };
}

export function serve(fetchMock: Mock<typeof fetch>, overrides: Partial<Server> = {}): void {
  const server: Server = {
    draft: draftSample,
    discovery: discoverySample,
    templates: templatesSample,
    validation: () => validationSample,
    evidence: evidenceSample,
    selfTests: [selfTestSample],
    start: () => respond(structuredClone(startedSample), 202),
    readouts: readoutsFor,
    activePreconditions: () => activePreconditionsSample,
    activeStart: () => respond(structuredClone(activeRefusedSample), 409),
    activeTests: [activeTestSample],
    ...overrides,
  };
  let polls = 0;
  let activePolls = 0;
  fetchMock.mockImplementation((input, init) => {
    const url = String(input);
    const path = url.split('?')[0]!;
    const method = init?.method ?? 'GET';
    if (path === 'api/setup/draft') return respond(structuredClone(server.draft));
    if (path === 'api/setup/templates') return respond(structuredClone(server.templates));
    if (path === 'api/setup/discovery') return respond(structuredClone(server.discovery));
    if (path === 'api/setup/readouts') return respond(server.readouts(url));
    if (path === 'api/setup/active-test/preconditions') return respond(structuredClone(server.activePreconditions()));
    if (path === 'api/setup/active-test' && method === 'POST') return server.activeStart();
    if (path.startsWith('api/setup/active-test/') && method === 'DELETE') return respond(null, 204);
    if (path.startsWith('api/setup/active-test/')) return respond(structuredClone(server.activeTests[Math.min(activePolls++, server.activeTests.length - 1)]));
    if (path === 'api/setup/evidence') return respond(structuredClone(server.evidence));
    if (path === 'api/setup/validate' && method === 'POST') return respond(structuredClone(server.validation()));
    if (path === 'api/setup/self-test' && method === 'POST') return server.start();
    if (path.startsWith('api/setup/self-test/') && method === 'DELETE') return respond(null, 204);
    if (path.startsWith('api/setup/self-test/')) return respond(structuredClone(server.selfTests[Math.min(polls++, server.selfTests.length - 1)]));
    return respond({}, 404);
  });
}

/** Renders one step with the real hook; returns a reader of the hook's latest state. */
export async function renderStep(Step: ComponentType<StepProps>, goTo: (step: StepName) => void, lang: Lang = 'en'): Promise<() => SetupState> {
  let current: SetupState | undefined;
  function Harness() {
    const setup = useSetup();
    current = setup;
    return setup.load.kind === 'ready' ? <Step setup={setup} t={messages[lang]} lang={lang} goTo={goTo} /> : null;
  }
  render(<Harness />);
  for (let i = 0; i < 4; i++) await advance();
  return () => current!;
}

/** The JSON bodies the page posted to a URL, oldest first. */
export function posted(fetchMock: Mock<typeof fetch>, url: string): Payload[] {
  return fetchMock.mock.calls.filter(([u, init]) => String(u) === url && init?.method === 'POST').map(([, init]) => JSON.parse(String(init?.body)) as Payload);
}
