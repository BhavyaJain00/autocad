import { describe, expect, it } from 'vitest';
import { httpOrigin, isAllowedNavigation, isAppUrl, parseDevServerUrl } from './url-policy';

const DEV = 'http://127.0.0.1:5174/';

describe('httpOrigin', () => {
  it('returns the origin of http(s) URLs only', () => {
    expect(httpOrigin('http://127.0.0.1:5174/src/main.tsx?t=1')).toBe('http://127.0.0.1:5174');
    expect(httpOrigin('https://example.com/a')).toBe('https://example.com');
    expect(httpOrigin('file:///C:/app/index.html')).toBeNull();
    expect(httpOrigin('not a url')).toBeNull();
  });
});

describe('parseDevServerUrl', () => {
  it('accepts http(s) URLs', () => {
    expect(parseDevServerUrl('http://127.0.0.1:5174')).toBe('http://127.0.0.1:5174/');
    expect(parseDevServerUrl(' http://localhost:5174/ ')).toBe('http://localhost:5174/');
  });

  it('rejects missing, blank and non-http values', () => {
    expect(parseDevServerUrl(undefined)).toBeNull();
    expect(parseDevServerUrl('')).toBeNull();
    expect(parseDevServerUrl('   ')).toBeNull();
    expect(parseDevServerUrl('file:///C:/evil/index.html')).toBeNull();
    expect(parseDevServerUrl('javascript:alert(1)')).toBeNull();
    expect(parseDevServerUrl('localhost:5174')).toBeNull();
  });
});

describe('isAllowedNavigation', () => {
  it('blocks every navigation in production', () => {
    expect(isAllowedNavigation('file:///C:/app/dist/renderer/index.html', null)).toBe(false);
    expect(isAllowedNavigation('https://example.com/', null)).toBe(false);
  });

  it('allows only the dev server origin in development', () => {
    expect(isAllowedNavigation('http://127.0.0.1:5174/', DEV)).toBe(true);
    expect(isAllowedNavigation('http://127.0.0.1:5174/index.html?reload=1', DEV)).toBe(true);
    expect(isAllowedNavigation('http://127.0.0.1:5175/', DEV)).toBe(false);
    expect(isAllowedNavigation('http://localhost:5174/', DEV)).toBe(false);
    expect(isAllowedNavigation('https://example.com/', DEV)).toBe(false);
    expect(isAllowedNavigation('file:///C:/x.html', DEV)).toBe(false);
  });
});

describe('isAppUrl', () => {
  it('trusts file: pages in production', () => {
    expect(isAppUrl('file:///C:/app/dist/renderer/index.html', null)).toBe(true);
    expect(isAppUrl('http://127.0.0.1:5174/', null)).toBe(false);
    expect(isAppUrl('about:blank', null)).toBe(false);
    expect(isAppUrl('', null)).toBe(false);
  });

  it('trusts only the dev server origin in development', () => {
    expect(isAppUrl('http://127.0.0.1:5174/', DEV)).toBe(true);
    expect(isAppUrl('file:///C:/app/dist/renderer/index.html', DEV)).toBe(false);
  });
});
