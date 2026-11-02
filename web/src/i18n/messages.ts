import { en, type Messages } from './en';
import { pl } from './pl';

export type Lang = 'en' | 'pl';

export const messages: Record<Lang, Messages> = { en, pl };
