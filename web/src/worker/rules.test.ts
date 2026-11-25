import { describe, expect, it } from 'vitest';

import { cacheName, isStale, keptFiles, keptIsBetter, keyOf } from './rules';

const scope = new URL('https://home.example/vn/');
const files = new Set(['assets/index-abc.js', 'icons/icon-192.png', 'manifest.webmanifest']);

function request(url: string, init: { method?: string; mode?: string } = {}) {
  return { url, method: init.method ?? 'GET', mode: init.mode ?? 'cors' };
}

describe('the page’s service worker', () => {
  it('keeps the page and its build, not itself', () => {
    expect(
      keptFiles([
        'index.html',
        'sw.js',
        'assets/index-abc.js',
        'assets/index-abc.js.map',
        'favicon.svg',
      ]),
    ).toEqual(['./', 'assets/index-abc.js', 'favicon.svg']);
  });

  it('answers the page and its files from where they are kept', () => {
    expect(keyOf(request(scope.href, { mode: 'navigate' }), scope, files)).toBe(scope.href);
    expect(keyOf(request(`${scope.href}?from=app`, { mode: 'navigate' }), scope, files)).toBe(
      scope.href,
    );
    expect(keyOf(request(`${scope.href}assets/index-abc.js`), scope, files)).toBe(
      `${scope.href}assets/index-abc.js`,
    );
  });

  it('never touches the server’s calls, another site or another file', () => {
    for (const url of [
      `${scope.href}Api/Settings`,
      `${scope.href}Api/Lines/3/Picture?width=640`,
      `${scope.href}Api/Lines/3/Screenshot`,
      `${scope.href}Api/GameSettings`,
      `${scope.href}Api/Events`,
      `${scope.href}assets/index-abc.js?v=2`,
      `${scope.href}assets/other.js`,
      'https://home.example/other/',
      'https://home.example/vnX/assets/index-abc.js',
      'https://elsewhere.example/vn/',
      'http://127.0.0.1:8765/',
    ]) {
      expect(keyOf(request(url), scope, files), url).toBeUndefined();
    }
    expect(keyOf(request(scope.href, { method: 'POST' }), scope, files)).toBeUndefined();
  });

  it('opens what it kept when a proxy says the server is down, not when the page is gone', () => {
    expect([502, 503, 504, 500].map(keptIsBetter)).toEqual([true, true, true, true]);
    expect([200, 304, 404, 403].map(keptIsBetter)).toEqual([false, false, false, false]);
  });

  it('drops what older versions kept, and nothing of anyone else', () => {
    const current = cacheName(scope, '0.1.1');
    expect(isStale(cacheName(scope, '0.1.0'), scope, current)).toBe(true);
    expect(isStale(current, scope, current)).toBe(false);
    expect(isStale('another-app', scope, current)).toBe(false);
  });

  it('leaves alone what the page of another server on the same site kept', () => {
    const current = cacheName(scope, '0.1.1');
    for (const other of [
      'https://home.example/',
      'https://home.example/vn-test/',
      'https://home.example/x/vn/',
    ]) {
      const theirs = new URL(other);
      expect(cacheName(theirs, '0.1.0')).not.toBe(cacheName(scope, '0.1.0'));
      expect(isStale(cacheName(theirs, '0.1.0'), scope, current), other).toBe(false);
      expect(isStale(cacheName(theirs, '0.1.1'), scope, current), other).toBe(false);
    }
  });
});
