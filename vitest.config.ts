import { defineConfig } from 'vitest/config';

// Unit tests for every integrated workspace run from the repository root: `npm test`.
// Electron end-to-end tests are separate (Playwright): `npm run test:e2e`.
//
// Packages still under construction (dxf, cad-ops, export, architecture, the AutoCAD plug-in's
// interop tests) are listed in vitest.next.config.ts instead, so their work in progress never
// breaks `npm test`. Move a package's glob here once it is integrated.
//
// maxWorkers is capped so that several test runs can share a laptop (override with
// VITEST_MAX_WORKERS=8 for a faster single run on a bigger machine).
const maxWorkers = Number(process.env.VITEST_MAX_WORKERS ?? 2);

export default defineConfig({
  test: {
    include: [
      'packages/{geometry,units,document,cad-protocol,copilot-agent}/src/**/*.test.ts',
      'apps/*/src/**/*.test.ts',
    ],
    environment: 'node',
    maxWorkers: Number.isFinite(maxWorkers) && maxWorkers > 0 ? maxWorkers : 2,
  },
});
