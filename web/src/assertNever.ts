/**
 * The default of a switch over a closed union: the compiler fails when a member is added and not
 * handled, and a value that is not a member (a payload the validators let through) fails loudly
 * instead of falling out of the switch.
 */
export function assertNever(value: never): never {
  throw new Error(`Unhandled case: ${JSON.stringify(value)}`);
}
