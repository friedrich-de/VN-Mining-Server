import { type APIRequestContext, expect, type Page, test as base } from '@playwright/test';

import { startAnkiConnect, type StandInAnki, type StandInOptions } from './ankiConnect';
import { PretendGame } from './game';
import { sampleMedia, type SampleMedia } from './media';
import { startServer, type TestServer } from './server';

interface Fixtures {
  /** A mining server of the test's own, empty, stopped and removed when the test ends. */
  server: TestServer;
  /** A pretend game that sends its lines to the test's server. */
  game: PretendGame;
  /** A voice and a screenshot as a game sends them. */
  media: SampleMedia;
  /** Calls the server's API with the key, as a script of the owner would. */
  api: APIRequestContext;
  /** Signs the browser in the way the page does, and opens the page. */
  openPage: () => Promise<Page>;
  /** Starts an AnkiConnect of the test's own, stopped when the test ends. */
  anki: (options?: StandInOptions) => Promise<StandInAnki>;
  /** What the page wrote to the browser's console as an error, checked after every test. */
  pageErrors: string[];
}

export const test = base.extend<Fixtures>({
  // eslint-disable-next-line no-empty-pattern
  server: async ({}, use) => {
    const server = await startServer();
    try {
      await use(server);
    } finally {
      await server.stop();
    }
  },

  baseURL: async ({ server }, use) => {
    await use(server.url);
  },

  game: async ({ server }, use) => {
    await use(PretendGame.of(server));
  },

  // eslint-disable-next-line no-empty-pattern
  media: async ({}, use) => {
    await use(await sampleMedia());
  },

  api: async ({ playwright, server }, use) => {
    const context = await playwright.request.newContext({
      baseURL: server.url,
      extraHTTPHeaders: { Authorization: `Bearer ${server.key}` },
    });
    await use(context);
    await context.dispose();
  },

  openPage: async ({ page, server }, use) => {
    await use(async () => {
      // The cookie the page would get for the key; the sign-in form has tests of its own.
      const response = await page.request.post('/Api/Session', { data: { Key: server.key } });
      expect(response.status(), await response.text()).toBe(204);
      await page.goto('/');
      return page;
    });
  },

  // eslint-disable-next-line no-empty-pattern
  anki: async ({}, use) => {
    const started: StandInAnki[] = [];
    await use(async (options) => {
      const anki = await startAnkiConnect(options);
      started.push(anki);
      return anki;
    });
    await Promise.allSettled(started.map((anki) => anki.stop()));
  },

  pageErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on('pageerror', (error) => errors.push(`uncaught: ${error.message}`));
      page.on('console', (message) => {
        // A refused request, also one that CORS refuses, is the browser's own line, and the tests
        // that expect one assert on what the page shows. Everything else, a refused script or
        // style above all, is a fault.
        if (message.type() !== 'error') return;
        if (message.text().startsWith('Failed to load resource:')) return;
        if (
          /^Access to fetch at '[^']+' from origin '[^']+' has been blocked by CORS/.test(
            message.text(),
          )
        )
          return;
        errors.push(message.text());
      });
      await use(errors);
      expect(errors, 'errors in the console of the page').toEqual([]);
    },
    { auto: true },
  ],
});

/** Changes the settings through the API, as the page does. */
export async function saveSettings(api: APIRequestContext, change: unknown): Promise<void> {
  const response = await api.put('/Api/Settings', { data: change });
  expect(response.status(), await response.text()).toBe(200);
}

export { expect };
