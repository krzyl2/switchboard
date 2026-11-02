const DECIMAL = /^[+-]?(\d+[.,]?\d*|[.,]\d+)$/;

/**
 * A number typed into a configuration field. A comma is a decimal separator too ("0,35"). Empty (or
 * blanks only) is null: the field is cleared on purpose. Text that is not a plain decimal number is
 * undefined: the caller clears the value and may say why. Only plain decimals pass: "0x10", "1e3",
 * "Infinity", "1,5,5" and "1 000" would each become some number through Number(), and a breaker or
 * a power typed that way is a typo to refuse, not a value to guess.
 */
export function parseNumber(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  if (!DECIMAL.test(trimmed)) return undefined;
  const value = Number(trimmed.replace(',', '.'));
  return Number.isFinite(value) ? value : undefined;
}
