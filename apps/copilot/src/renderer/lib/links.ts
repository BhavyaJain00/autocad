/** Link safety for assistant markdown: only web links leave the app, through the main process. */

/** Where people create an Anthropic API key. */
export const API_KEY_URL = 'https://platform.claude.com/';

/**
 * Returns the normalised URL when `href` is an absolute http(s) link, otherwise null (relative
 * links, `javascript:`, `file:`, `data:` and anything malformed never open).
 */
export function safeExternalUrl(href: string | undefined | null): string | null {
  if (typeof href !== 'string') return null;
  const trimmed = href.trim();
  if (trimmed.length === 0) return null;
  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return null;
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
  if (url.hostname.length === 0) return null;
  return url.toString();
}
