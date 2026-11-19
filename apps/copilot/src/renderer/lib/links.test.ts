import { describe, it, expect } from 'vitest';
import { API_KEY_URL, safeExternalUrl } from './links';

describe('safeExternalUrl', () => {
  it('accepts absolute web links', () => {
    expect(safeExternalUrl('https://platform.claude.com/')).toBe('https://platform.claude.com/');
    expect(safeExternalUrl('  https://example.com/a?b=1#c ')).toBe('https://example.com/a?b=1#c');
    expect(safeExternalUrl('http://example.com')).toBe('http://example.com/');
  });

  it('rejects every other scheme', () => {
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull();
    expect(safeExternalUrl('file:///C:/Windows/system32/cmd.exe')).toBeNull();
    expect(safeExternalUrl('data:text/html,<script>1</script>')).toBeNull();
    expect(safeExternalUrl('mailto:someone@example.com')).toBeNull();
    expect(safeExternalUrl('ms-settings:privacy')).toBeNull();
  });

  it('rejects relative, empty and malformed links', () => {
    expect(safeExternalUrl('/settings')).toBeNull();
    expect(safeExternalUrl('#anchor')).toBeNull();
    expect(safeExternalUrl('example.com')).toBeNull();
    expect(safeExternalUrl('')).toBeNull();
    expect(safeExternalUrl('   ')).toBeNull();
    expect(safeExternalUrl(undefined)).toBeNull();
    expect(safeExternalUrl(null)).toBeNull();
    expect(safeExternalUrl('https://')).toBeNull();
  });

  it('the API key link is itself a safe link', () => {
    expect(safeExternalUrl(API_KEY_URL)).toBe(API_KEY_URL);
  });
});
