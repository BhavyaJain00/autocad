import path from 'node:path';
import { fileURLToPath } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';

const copilotDir = path.dirname(fileURLToPath(import.meta.url));

/** Dev server port; scripts/dev.mjs points Electron at the URL the server resolves. */
const DEV_SERVER_PORT = 5174;

/**
 * Content Security Policy of the packaged renderer. Everything is bundled locally: no remote
 * scripts, styles, fonts or connections, and no inline scripts.
 */
const CONTENT_SECURITY_POLICY = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self' data:",
  "connect-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

/**
 * Injects the CSP <meta> right after <meta charset> in the production build only (the dev server
 * needs inline scripts and websockets for hot reload). Fails the build if it cannot be placed.
 */
function contentSecurityPolicyPlugin(): Plugin {
  const metaTag = `<meta http-equiv="Content-Security-Policy" content="${CONTENT_SECURITY_POLICY}" />`;
  return {
    name: 'cadcopilot:content-security-policy',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler(html) {
        const charsetMeta = /<meta\s+charset\s*=\s*["']?[\w-]+["']?\s*\/?>/i;
        if (charsetMeta.test(html)) {
          return html.replace(charsetMeta, (match) => `${match}\n    ${metaTag}`);
        }
        const headOpen = /<head(\s[^>]*)?>/i;
        if (headOpen.test(html)) {
          return html.replace(headOpen, (match) => `${match}\n    ${metaTag}`);
        }
        throw new Error('cadcopilot:content-security-policy: index.html has no <head> to receive the CSP meta tag');
      },
    },
  };
}

export default defineConfig({
  root: path.join(copilotDir, 'src/renderer'),
  // Relative asset URLs: the packaged renderer is loaded from file://.
  base: './',
  plugins: [react(), tailwindcss(), contentSecurityPolicyPlugin()],
  clearScreen: false,
  build: {
    outDir: path.join(copilotDir, 'dist/renderer'),
    emptyOutDir: true,
  },
  server: {
    host: '127.0.0.1',
    port: DEV_SERVER_PORT,
    strictPort: true,
  },
});
