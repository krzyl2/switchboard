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
