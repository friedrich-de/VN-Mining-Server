import type { Page } from '@playwright/test';

import { expect, test } from '../support/fixtures';

const line = (page: Page, id: number) => page.locator(`.line[data-line="${id}"]`);

test.describe('the installed app', () => {
  test('can be installed, and opens without the server to say it cannot reach it', async ({
    openPage,
    context,
    server,
    game,
    media,
    api,
  }) => {
    await game.say({
      text: '昨日の話、覚えてる？',
      audio: media.voice,
      screenshot: media.screenshot,
    });
    const page = await openPage();
    await expect(line(page, 1)).toBeVisible();

    // The worker is there, for the whole of the server's address.
    const scope = await page.evaluate(async () => (await navigator.serviceWorker.ready).scope);
    expect(scope).toBe(`${server.url}/`);

    // Chromium finds nothing that keeps the page from being installed.
    const cdp = await context.newCDPSession(page);
    const { installabilityErrors } = (await cdp.send('Page.getInstallabilityErrors')) as {
      installabilityErrors: { errorId: string }[];
    };
    expect(installabilityErrors).toEqual([]);
    const { errors, url } = (await cdp.send('Page.getAppManifest')) as {
      errors: unknown[];
      url: string;
    };
    expect(errors).toEqual([]);
    expect(url).toBe(`${server.url}/manifest.webmanifest`);

    // What the worker keeps: the page's files, and nothing the server answered about the game.
    const { Version: version } = (await (await api.get('/Api/Status')).json()) as {
      Version: string;
    };
    await expect
      .poll(() => page.evaluate(() => caches.keys()))
      .toEqual([`vn-miner-page ${server.url}/ ${version}`]);
    const kept = await page.evaluate(async () => {
      const [name] = await caches.keys();
      const requests = await (await caches.open(name!)).keys();
      return requests.map((request) => request.url);
    });
    expect(kept).toContain(`${server.url}/`);
    expect(kept.filter((address) => !address.startsWith(`${server.url}/`))).toEqual([]);
    expect(kept.filter((address) => address.includes('/Api/'))).toEqual([]);

    // Without the server the page still opens, and says what is wrong.
    await page.reload();
    await expect(line(page, 1)).toBeVisible();
    expect(await page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true);
    await context.setOffline(true);
    await page.reload();
    await expect(
      page.getByRole('heading', { name: 'The server could not be reached' }),
    ).toBeVisible();

    await context.setOffline(false);
    await page.getByRole('button', { name: 'Try again' }).click();
    await expect(line(page, 1)).toBeVisible();
  });

  test('keeps to the screen of a phone and of a desk', async ({ openPage, game, media }) => {
    for (let number = 1; number <= 12; number += 1) {
      await game.say({
        text: `これは${number}行目で、画面の幅より長くなるように少し長めに書いてある文章です。`,
        name: number % 2 === 0 ? 'ユキ' : 'ハル',
        audio: media.voice2,
        screenshot: media.screenshotJpeg,
      });
    }
    const page = await openPage();
    await expect(line(page, 12)).toBeVisible();
    await line(page, 12).getByRole('checkbox').click();
    await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();

    // Nothing is wider than the screen: no sideways scrolling, with lines chosen or in the settings.
    const overflow = () =>
      page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
    expect(await overflow()).toBeLessThanOrEqual(0);
    await page.getByTestId('open-settings').click();
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();
    expect(await overflow()).toBeLessThanOrEqual(0);
  });
});
