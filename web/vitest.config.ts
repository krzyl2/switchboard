import { defineConfig } from 'vitest/config';
import preact from '@preact/preset-vite';

export default defineConfig({
  plugins: [preact()],
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    // The stylesheet is part of the contract (theme.test.ts reads its tokens through ?raw); vitest
    // leaves CSS empty unless it is processed.
    css: { include: [/styles\.css/] },
  },
});
