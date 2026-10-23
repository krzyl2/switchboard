import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  plugins: [preact()],
  // Ingress serves the page under a per-session path prefix, so every asset URL must be relative.
  base: './',
  build: {
    // MUST resolve to exactly src/Switchboard.Host/wwwroot: emptyOutDir wipes this directory on
    // every build, so pointing it one level higher would delete the Host project's sources.
    // scripts/check-offline.mjs scans the same directory.
    outDir: fileURLToPath(new URL('../src/Switchboard.Host/wwwroot', import.meta.url)),
    emptyOutDir: true,
  },
});
