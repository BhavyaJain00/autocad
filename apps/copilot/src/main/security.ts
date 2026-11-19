/**
 * Process-wide hardening: no navigation away from the app, no new windows, no <webview>, and no
 * web permissions (camera, microphone, geolocation, notifications, ...).
 */
import { app, type Session, type WebContents } from 'electron';
import { isAllowedNavigation } from './url-policy';

/**
 * The only permission granted: writing sanitised text to the clipboard (Copy in the UI). It cannot
 * read anything; every other permission request and check is denied.
 */
const ALLOWED_PERMISSIONS: ReadonlySet<string> = new Set(['clipboard-sanitized-write']);

function hardenWebContents(contents: WebContents, devServerUrl: string | null): void {
  contents.on('will-attach-webview', (event) => {
    event.preventDefault();
  });
  contents.setWindowOpenHandler(() => ({ action: 'deny' }));
  const blockForeignNavigation = (event: { readonly url: string; preventDefault(): void }): void => {
    if (!isAllowedNavigation(event.url, devServerUrl)) {
      event.preventDefault();
      console.warn(`[security] Blocked navigation to ${event.url}`);
    }
  };
  contents.on('will-navigate', blockForeignNavigation);
  // Sub-frames and server redirects must not leave the app either.
  contents.on('will-frame-navigate', blockForeignNavigation);
  contents.on('will-redirect', blockForeignNavigation);
}

/** Applies the navigation / window / webview policy to every web contents ever created. */
export function installWebContentsSecurity(devServerUrl: string | null): void {
  app.on('web-contents-created', (_event, contents) => {
    hardenWebContents(contents, devServerUrl);
  });
}

/** Denies web permission requests and checks (except sanitised clipboard writes). */
export function installSessionSecurity(session: Session): void {
  session.setPermissionRequestHandler((_webContents, permission, callback) => {
    callback(ALLOWED_PERMISSIONS.has(permission));
  });
  session.setPermissionCheckHandler((_webContents, permission) => ALLOWED_PERMISSIONS.has(permission));
}
