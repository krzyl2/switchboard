import { useCallback, useEffect, useReducer, useRef, useState } from 'preact/hooks';
import {
  emptyDraft,
  fetchDraft,
  parseDraft,
  fetchTemplates,
  saveDraft,
  validateDraft,
  type DeviceDraft,
  type DraftView,
  type SaveOutcome,
  type SetupDraft,
  type SetupIssue,
  type Template,
  type Validation,
} from './api/setup';
import { SETUP_REQUEST_TIMEOUT_MS } from './config';
import type { Lang, Messages } from './i18n';
import { draftReducer, issuesAt, issuesOfStep, type StepName } from './setupDraft';

export type SetupLoad = { kind: 'loading' } | { kind: 'ready' } | { kind: 'unavailable' };

/**
 * What became of unsaved edits kept from an earlier visit of the wizard in this browser tab:
 * restored (the file is still the one they were made on), or set aside because the file changed
 * since (restoring them onto another file could undo that change).
 */
export type Unsaved = 'none' | 'restored' | 'discarded';

/** sessionStorage key of the unsaved draft and the file revision it was made on. */
export const DRAFT_STORAGE_KEY = 'switchboard.setup.draft';

interface StoredDraft {
  baseRevision: string | null;
  draft: SetupDraft;
}

function readStored(): StoredDraft | null {
  try {
    const text = window.sessionStorage.getItem(DRAFT_STORAGE_KEY);
    if (text === null) return null;
    const raw = JSON.parse(text) as { baseRevision?: unknown; draft?: unknown };
    const baseRevision = raw.baseRevision === null || typeof raw.baseRevision === 'string' ? raw.baseRevision : undefined;
    // Parsed as strictly as the add-on's own answer: a draft of another shape is dropped, never half-read.
    return baseRevision === undefined ? null : { baseRevision, draft: parseDraft(raw.draft) };
  } catch {
    return null;
  }
}

function writeStored(value: StoredDraft | null): void {
  try {
    if (value === null) window.sessionStorage.removeItem(DRAFT_STORAGE_KEY);
    else window.sessionStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(value));
  } catch {
    // A convenience only: without storage, leaving the wizard loses unsaved edits as before.
  }
}

/** What every step reads and writes: the one draft, the add-on's last verdict on it, and the templates. */
export interface SetupState {
  load: SetupLoad;
  /** The file as loaded (revision, comments, what is preserved); null until loaded. */
  view: DraftView | null;
  draft: SetupDraft;
  templates: Template[];
  prefillVoltageV: number | null;
  validation: Validation | null;
  /** The issues to show: the last validation's, or a refused save's. */
  issues: SetupIssue[];
  busy: 'idle' | 'validating' | 'saving';
  outcome: SaveOutcome | null;
  /** Unsaved edits of an earlier visit: restored or set aside (UI-06: leaving the wizard to switch a device in Home Assistant loses nothing). */
  unsaved: Unsaved;
  /** Drops the restored edits and loads the file again. */
  discardUnsaved: () => void;
  set: (path: string, value: unknown) => void;
  addDevice: (device: DeviceDraft) => void;
  removeDevice: (index: number) => void;
  /** POST api/setup/validate; nothing is written. */
  validate: () => Promise<Validation | null>;
  /** POST api/setup/save; on a refusal the issues name the fields to fix. */
  save: () => Promise<SaveOutcome | null>;
  reload: () => void;
  issuesOf: (step: StepName) => SetupIssue[];
  issuesAt: (field: string) => SetupIssue[];
}

function withTimeout<T>(run: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), SETUP_REQUEST_TIMEOUT_MS);
  return run(controller.signal).finally(() => clearTimeout(timeout));
}

/**
 * The wizard's shared draft state (UI-06): loads GET api/setup/draft (an existing file opens with
 * every device it declares), keeps every edit in one reducer, and validates or saves through the
 * add-on, which is the only judge. Re-running the wizard therefore starts from the file, never from
 * an empty form.
 */
export function useSetup(): SetupState {
  const [load, setLoad] = useState<SetupLoad>({ kind: 'loading' });
  const [view, setView] = useState<DraftView | null>(null);
  const [draft, dispatch] = useReducer(draftReducer, undefined, emptyDraft);
  const [templates, setTemplates] = useState<Template[]>([]);
  const [prefillVoltageV, setPrefillVoltageV] = useState<number | null>(null);
  const [validation, setValidation] = useState<Validation | null>(null);
  const [issues, setIssues] = useState<SetupIssue[]>([]);
  const [busy, setBusy] = useState<SetupState['busy']>('idle');
  const [outcome, setOutcome] = useState<SaveOutcome | null>(null);
  const [generation, setGeneration] = useState(0);
  const [unsaved, setUnsaved] = useState<Unsaved>('none');
  const draftRef = useRef(draft);
  draftRef.current = draft;
  const viewRef = useRef(view);
  viewRef.current = view;

  useEffect(() => {
    let disposed = false;
    setLoad({ kind: 'loading' });
    void Promise.all([withTimeout(fetchDraft), withTimeout(fetchTemplates)])
      .then(([loaded, cards]) => {
        if (disposed) return;
        setView(loaded);
        const stored = readStored();
        const differs = stored !== null && JSON.stringify(stored.draft) !== JSON.stringify(loaded.draft);
        if (differs && stored.baseRevision === loaded.revision) {
          dispatch({ kind: 'replace', draft: stored.draft });
          setUnsaved('restored');
        } else {
          if (differs) writeStored(null);
          dispatch({ kind: 'replace', draft: loaded.draft });
          setUnsaved(differs ? 'discarded' : 'none');
        }
        setTemplates(cards.templates);
        setPrefillVoltageV(cards.prefillVoltageV);
        setIssues(loaded.fileIssues);
        setLoad({ kind: 'ready' });
      })
      .catch(() => {
        if (!disposed) setLoad({ kind: 'unavailable' });
      });
    return () => {
      disposed = true;
    };
  }, [generation]);

  // Every edit is kept for this browser tab with the revision it was made on, until a save.
  useEffect(() => {
    if (load.kind === 'ready' && view !== null) writeStored({ baseRevision: view.revision, draft });
  }, [draft, view, load.kind]);

  const validate = useCallback(async () => {
    setBusy('validating');
    try {
      const result = await withTimeout((signal) => validateDraft(draftRef.current, viewRef.current?.revision ?? null, signal));
      setValidation(result);
      setIssues(result.issues);
      return result;
    } catch {
      return null;
    } finally {
      setBusy('idle');
    }
  }, []);

  const save = useCallback(async () => {
    setBusy('saving');
    try {
      const result = await withTimeout((signal) => saveDraft(draftRef.current, viewRef.current?.revision ?? null, signal));
      setOutcome(result);
      if (result.kind !== 'saved') setIssues(result.issues);
      else {
        writeStored(null);
        setUnsaved('none');
      }
      return result;
    } catch {
      return null;
    } finally {
      setBusy('idle');
    }
  }, []);

  return {
    load,
    view,
    draft,
    templates,
    prefillVoltageV,
    validation,
    issues,
    busy,
    outcome,
    unsaved,
    discardUnsaved: () => {
      writeStored(null);
      setLoad({ kind: 'loading' });
      setView(null);
      setValidation(null);
      setOutcome(null);
      setGeneration((g) => g + 1);
    },
    set: (path, value) => dispatch({ kind: 'set', path, value }),
    addDevice: (device) => dispatch({ kind: 'addDevice', device }),
    removeDevice: (index) => dispatch({ kind: 'removeDevice', index }),
    validate,
    save,
    reload: () => {
      setValidation(null);
      setOutcome(null);
      setGeneration((g) => g + 1);
    },
    issuesOf: (step) => issuesOfStep(issues, step),
    issuesAt: (field) => issuesAt(issues, field),
  };
}

/** What the shell hands every step component. */
export interface StepProps {
  setup: SetupState;
  t: Messages;
  lang: Lang;
  /** Moves the wizard to another step (the step's own Next/Back). */
  goTo: (step: StepName) => void;
}
