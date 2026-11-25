/**
 * Addresses, worked out from where the page is served. They are relative to the page, so they
 * hold behind a reverse proxy with a base path.
 */

/** The page's own folder. */
export function pageAddress(base: string): URL {
  return new URL('./', base);
}

/** One of the server's endpoints, by its path below /Api/. */
export function apiUrl(base: string, path: string): string {
  return new URL(`Api/${path}`, pageAddress(base)).href;
}
