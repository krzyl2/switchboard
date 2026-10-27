// Presentation settings of the ingress page. None of these is a control parameter: they only decide
// how often and how patiently the page asks the add-on for its status.

/** The product name is a proper noun and is not translated. */
export const PRODUCT_NAME = 'Switchboard';

/** How often the page asks the add-on for its status. */
export const STATUS_REFRESH_INTERVAL_MS = 5_000;

/**
 * A status request still unanswered after this long counts as failed, so a hung add-on shows up as
 * "status unavailable" instead of leaving a frozen page that looks like a calm one.
 */
export const STATUS_REQUEST_TIMEOUT_MS = 4_000;

/** The mode banner every page shows (shadow or active, MODE-02): as often as the status page. */
export const MODE_REFRESH_INTERVAL_MS = 5_000;
export const MODE_REQUEST_TIMEOUT_MS = 4_000;

/** The control block (Status page, setup step 5): the switch, the mode it results in, the command path. */
export const CONTROL_REFRESH_INTERVAL_MS = 5_000;
export const CONTROL_REQUEST_TIMEOUT_MS = 4_000;

/**
 * The add-on answers a change of the control switch (or the state confirmation) only after the guard
 * evaluated it, waiting up to its own Guard:ApiEvaluationTimeout (2 s); longer than that.
 */
export const CONTROL_CHANGE_TIMEOUT_MS = 5_000;

/**
 * How often the signals page asks for every signal. Faster than the status page: ages are the point
 * of that page, and a meter refreshing every few seconds should visibly tick.
 */
export const SIGNALS_REFRESH_INTERVAL_MS = 2_000;

/** Shorter than the refresh interval, so a hung request never delays the next one. */
export const SIGNALS_REQUEST_TIMEOUT_MS = 1_800;

/** How often the limits page asks for the guard's view: as often as the signals page, for the same reason. */
export const GUARD_REFRESH_INTERVAL_MS = 2_000;

/** Shorter than the refresh interval, so a hung request never delays the next one. */
export const GUARD_REQUEST_TIMEOUT_MS = 1_800;

/** How often the limits page asks for dispatch's view of every device: with the guard's view. */
export const DISPATCH_REFRESH_INTERVAL_MS = 2_000;

/** Shorter than the refresh interval, so a hung request never delays the next one. */
export const DISPATCH_REQUEST_TIMEOUT_MS = 1_800;

/**
 * The add-on answers an override only after the guard runner evaluated it, waiting up to its own
 * Guard:ApiEvaluationTimeout (2 s); longer than that, so a late evaluation is reported as such.
 */
export const OVERRIDE_REQUEST_TIMEOUT_MS = 5_000;

// Runtime screens (Stage 4). Each request times out before its next poll is due, so a hung request
// never delays the next one and a frozen add-on shows up as "unavailable".

/** The power picture (dashboard, flows): as often as the guard's view, so bars and ages visibly tick. */
export const POWER_REFRESH_INTERVAL_MS = 2_000;
export const POWER_REQUEST_TIMEOUT_MS = 1_800;

/** The declared devices change only with the configuration file. */
export const DEVICES_REFRESH_INTERVAL_MS = 10_000;
export const DEVICES_REQUEST_TIMEOUT_MS = 8_000;

/** The recorded excursions change at most once per meter sample. */
export const EXCURSIONS_REFRESH_INTERVAL_MS = 10_000;
export const EXCURSIONS_REQUEST_TIMEOUT_MS = 8_000;

/** The energy buckets: a bucket is minutes long, so half a minute is fresh enough. */
export const STATISTICS_REFRESH_INTERVAL_MS = 30_000;
export const STATISTICS_REQUEST_TIMEOUT_MS = 10_000;

/** The tariff zone changes at most at a boundary and savings at most once a bucket: ten seconds is fresh enough. */
export const ECONOMICS_REFRESH_INTERVAL_MS = 10_000;
export const ECONOMICS_REQUEST_TIMEOUT_MS = 8_000;

/** The decision log page. */
export const DECISION_LOG_REFRESH_INTERVAL_MS = 5_000;
export const DECISION_LOG_REQUEST_TIMEOUT_MS = 4_000;

// Setup wizard (Stage 5).

/** Live readouts beside each picked entity (UI-04): as often as the signals page, so ages visibly tick. */
export const SETUP_READOUTS_REFRESH_INTERVAL_MS = 2_000;
export const SETUP_READOUTS_REQUEST_TIMEOUT_MS = 1_800;

/**
 * Draft, templates, validation and saving: one request each. Validation may first wait for the
 * add-on's discovery link to reach Home Assistant (Setup:DiscoveryRequestTimeout, 15 s), and a save
 * for the running add-on to reload the file (Setup:ReloadConfirmTimeout, 10 s); longer than both.
 */
export const SETUP_REQUEST_TIMEOUT_MS = 30_000;

/** A watching self-test: as often as the readouts. */
export const SETUP_SELF_TEST_REFRESH_INTERVAL_MS = 2_000;
export const SETUP_SELF_TEST_REQUEST_TIMEOUT_MS = 1_800;

/**
 * GET api/plan: the plan changes at most every planning cycle, the goals' progress every control step; ten seconds follows both without
 * asking for the plan's slots and entries (about 40 KB) more often than a person can read them.
 */
export const PLAN_REFRESH_INTERVAL_MS = 10_000;
export const PLAN_REQUEST_TIMEOUT_MS = 8_000;

/** POST and DELETE api/goals only store or drop the add-on's own state and wake the planner: they answer at once. */
export const GOAL_REQUEST_TIMEOUT_MS = 5_000;
