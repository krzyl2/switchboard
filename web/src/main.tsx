import { render } from 'preact';
import { App } from './App';
import { detectLanguage } from './i18n';
import { applyTheme, readTheme } from './theme';
import './styles.css';

// Before the first render, so a stored dark choice never flashes light.
applyTheme(readTheme());

const lang = detectLanguage(navigator.language);
document.documentElement.lang = lang;

const root = document.getElementById('app');
if (!root) {
  throw new Error('index.html has no #app element.');
}
render(<App lang={lang} />, root);
