/** URL rules for navigation and IPC sender checks. Pure; no Electron imports. */

/** Origin of an http(s) URL, or null for anything else / unparsable input. */
export function httpOrigin(url: string): string | null {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.origin : null;
  } catch {
    return null;
  }
}

/**
 * Validates `VITE_DEV_SERVER_URL`: an http(s) URL, normalised; null when unset or invalid.
 * Callers must ignore it entirely in packaged builds.
 */
export function parseDevServerUrl(raw: string | undefined): string | null {
  if (raw === undefined || raw.trim() === '') return null;
  try {
    const parsed = new URL(raw.trim());
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : null;
  } catch {
    return null;
  }
}

/**
 * Navigation policy: in development, navigations within the Vite dev server origin (reloads) are
 * allowed; nothing else (links, `location.href = ...`, dropped files) may navigate a window.
 */
export function isAllowedNavigation(url: string, devServerUrl: string | null): boolean {
  if (devServerUrl === null) return false;
  const devOrigin = httpOrigin(devServerUrl);
  return devOrigin !== null && httpOrigin(url) === devOrigin;
}

/**
 * True when a frame URL belongs to the app itself: the dev server origin in development, a
 * `file:` URL (the packaged renderer) otherwise.
 */
export function isAppUrl(url: string, devServerUrl: string | null): boolean {
  if (devServerUrl !== null) return isAllowedNavigation(url, devServerUrl);
  try {
    return new URL(url).protocol === 'file:';
  } catch {
    return false;
  }
}
