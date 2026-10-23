// Fails when the ingress page could reach the internet. Runs after `vite build` (npm run build) and on
// its own (npm run check:offline). It scans the build output and the page sources for absolute URLs.
//
// The add-on promises no outbound connection beyond Home Assistant and the broker, and an ingress page
// that pulls a CDN script, a web font or an analytics beacon would break that promise from the user's
// browser. License comments may name a URL; those are stripped before scanning.

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = fileURLToPath(new URL('..', import.meta.url));
// Must match build.outDir in vite.config.ts. A mismatch fails below because index.html is missing.
const outDir = fileURLToPath(new URL('../../src/Switchboard.Host/wwwroot', import.meta.url));

// XML namespace names are identifiers that browsers never fetch; Preact's renderer needs them to create
// SVG, MathML and HTML elements. Exact matches only, and only the ones the bundle actually contains.
const NAMESPACE_IDENTIFIERS = new Set([
  'http://www.w3.org/2000/svg',
  'http://www.w3.org/1998/Math/MathML',
  'http://www.w3.org/1999/xhtml',
]);

const ABSOLUTE_URL = /\b(?:https?|wss?):\/\/[^\s"'`)<>\\]*/gi;
// Protocol-relative references ("//host/x") in markup and stylesheets also leave the ingress origin.
const PROTOCOL_RELATIVE = /(?:\b(?:src|href|action|poster)\s*=\s*["']?|url\(\s*["']?)\/\/[^/\s"')]/gi;
// Legal comments as bundlers preserve them: /*! ... */ and a block comment that opens with @license or
// @preserve. Both must start with their marker; a looser pattern would let a stray "/*" inside a string
// (an accept="image/*" attribute, say) swallow real code up to the next legal comment, URLs included.
const LICENSE_COMMENT = /\/\*(?:!|\*?\s*@(?:license|preserve)\b)(?:(?!\*\/)[\s\S])*\*\//g;

function filesUnder(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? filesUnder(path) : [path];
  });
}

// Keep line numbers stable while removing license comments.
function stripLicenseComments(text) {
  return text.replace(LICENSE_COMMENT, (comment) => comment.replace(/[^\n]/g, ' '));
}

function lineOf(text, index) {
  return text.slice(0, index).split('\n').length;
}

function scan(file) {
  const text = stripLicenseComments(readFileSync(file, 'utf8'));
  const problems = [];
  for (const match of text.matchAll(ABSOLUTE_URL)) {
    if (!NAMESPACE_IDENTIFIERS.has(match[0])) {
      problems.push(`${relative(webRoot, file)}:${lineOf(text, match.index)}  ${match[0]}`);
    }
  }
  if (['.html', '.css'].includes(extname(file).toLowerCase())) {
    for (const match of text.matchAll(PROTOCOL_RELATIVE)) {
      problems.push(`${relative(webRoot, file)}:${lineOf(text, match.index)}  ${match[0]}`);
    }
  }
  return problems;
}

if (!existsSync(join(outDir, 'index.html'))) {
  console.error(`check:offline: ${join(outDir, 'index.html')} does not exist. Run "npm run build" first.`);
  process.exit(1);
}

const files = [...filesUnder(outDir), ...filesUnder(join(webRoot, 'src')), join(webRoot, 'index.html')];
const problems = files.flatMap(scan);

if (problems.length > 0) {
  console.error('check:offline: absolute URLs found; the ingress page must load nothing from the internet:');
  for (const problem of problems) console.error(`  ${problem}`);
  process.exit(1);
}

console.log(`check:offline: OK, ${files.length} files scanned, no external URLs.`);
