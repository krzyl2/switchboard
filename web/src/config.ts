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

/**
 * How often the signals page asks for every signal. Faster than the status page: ages are the point
 * of that page, and a meter refreshing every few seconds should visibly tick.
 */
export const SIGNALS_REFRESH_INTERVAL_MS = 2_000;

/** Shorter than the refresh interval, so a hung request never delays the next one. */
export const SIGNALS_REQUEST_TIMEOUT_MS = 1_800;
