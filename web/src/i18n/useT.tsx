import { createContext, type ComponentChildren } from 'preact';
import { useContext } from 'preact/hooks';
import type { Messages } from './en';
import { messages, type Lang } from './messages';

const MessagesContext = createContext<Messages | null>(null);

/**
 * Makes the texts of one language available to everything below it. A screen puts it around its
 * content from its own `lang`, so a screen rendered alone and the same screen inside the app read
 * the same texts.
 */
export function I18n({ lang, children }: { lang: Lang; children: ComponentChildren }) {
  return <MessagesContext.Provider value={messages[lang]}>{children}</MessagesContext.Provider>;
}

/** The texts of the language the enclosing I18n was given. A component outside one is a bug, not English. */
export function useT(): Messages {
  const t = useContext(MessagesContext);
  if (t === null) throw new Error('useT() needs an enclosing <I18n>.');
  return t;
}
