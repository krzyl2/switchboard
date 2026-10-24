import { en, type Messages } from './en';
import { pl } from './pl';

export type { Messages };
export type Lang = 'en' | 'pl';

export const messages: Record<Lang, Messages> = { en, pl };

/** Polish for any Polish browser locale (pl, pl-PL), English for everything else. */
export function detectLanguage(browserLanguage: string | undefined): Lang {
  // Match the primary language subtag, not the first two letters: "plt" is a different language.
  return /^pl(-|$)/i.test(browserLanguage ?? '') ? 'pl' : 'en';
}

/**
 * Puts values into a text's {name} placeholders. A placeholder without a value is an error: a
 * sentence that silently lost its number would read as a complete one.
 */
export function fill(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = values[name];
    if (value === undefined) throw new Error(`No value for {${name}} in "${template}".`);
    return value;
  });
}
