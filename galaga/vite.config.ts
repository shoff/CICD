import { defineConfig } from 'vitest/config';

export default defineConfig({
  // Relative asset URLs so the built game runs from any sub-path or straight off disk.
  base: './',
  test: {
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
});
