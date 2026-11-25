import type { Page } from '@playwright/test';

import { expect, saveSettings, test } from '../support/fixtures';
import { PretendGame } from '../support/game';

const line = (page: Page, id: number) => page.locator(`.line[data-line="${id}"]`);

/** Says the page is in front of the user, or behind another app. */
async function setVisibility(page: Page, state: 'visible' | 'hidden'): Promise<void> {
  await page.evaluate((now) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => now });
    document.dispatchEvent(new Event('visibilitychange'));
  }, state);
}

test.describe('the stream of lines', () => {
  test('shows each line as the game sends it, with its voice and its screenshot', async ({
    openPage,
    game,
    media,
  }) => {
    const page = await openPage();
    await expect(page.getByTestId('no-lines')).toContainText('mining.txt');
    await expect(page.getByTestId('where')).toContainText('Live');

    // The game shows a line: it is here without a reload.
    const sent = await game.say({
      text: '昨日の話、覚えてる？',
      name: 'ユキ',
      voice: 'YUK_0001',
      audio: media.voice,
      screenshot: media.screenshotJpeg,
    });
    expect(sent).toMatchObject({ Id: 1, HasAudio: true, HasPicture: true });
    await expect(line(page, 1).locator('.said')).toHaveText('昨日の話、覚えてる？');
    await expect(line(page, 1).locator('.speaker')).toHaveText('ユキ');
    await expect(line(page, 1).getByLabel('With voice')).toBeVisible();
    await expect(line(page, 1).getByLabel('With screenshot')).toBeVisible();
    await expect(page.getByTestId('title')).toHaveText('Sample Novel');
    await expect(line(page, 1)).toHaveAttribute('aria-current', 'true');

    // Narration: no speaker and no voice.
    await game.say({ text: '窓の外では雨が降っていた。', screenshot: media.screenshot });
    await expect(line(page, 2).locator('.said')).toHaveText('窓の外では雨が降っていた。');
    await expect(line(page, 2).locator('.speaker')).toHaveCount(0);
    await expect(line(page, 2).getByLabel('With voice')).toHaveCount(0);
    await expect(line(page, 2)).toHaveAttribute('aria-current', 'true');

    // Choosing the first line shows what a card of it gets: the screenshot at the width of the
    // settings, and the voice in a format every browser plays.
    await line(page, 1).getByRole('checkbox').click();
    const picture = page.getByTestId('preview-picture').locator('img');
    await expect(picture).toBeVisible();
    await expect
      .poll(() => picture.evaluate((image) => (image as HTMLImageElement).naturalWidth))
      .toBe(640);
    const sound = page.getByTestId('sound');
    await expect(sound).toHaveAttribute('src', /^blob:/);
    await expect
      .poll(() => sound.evaluate((audio) => (audio as HTMLAudioElement).duration))
      .toBeGreaterThan(0.9);
    expect(await sound.evaluate((audio) => (audio as HTMLAudioElement).duration)).toBeLessThan(1.3);
    await expect(page.getByTestId('play-sound')).toBeEnabled();

    // The narration has a picture and says why it has no sound.
    await line(page, 1).getByRole('checkbox').click();
    await line(page, 2).getByRole('checkbox').click();
    await expect(page.getByTestId('preview-picture').locator('img')).toBeVisible();
    await expect(page.getByText('This line has no voice.')).toBeVisible();
  });

  test('joins the voices of the chosen lines into one sound', async ({ openPage, game, media }) => {
    await game.say({ text: 'おはよう。', name: 'ハル', audio: media.voice });
    await game.say({ text: '朝の光が差していた。' });
    await game.say({ text: 'おはよう、ハル。', name: 'ユキ', audio: media.voice2 });
    const page = await openPage();

    await line(page, 1).getByRole('checkbox').click();
    await line(page, 3)
      .getByRole('checkbox')
      .click({ modifiers: ['Shift'] });
    await expect(page.getByTestId('selected-count')).toHaveText('3 lines selected');

    // A second, a pause of a quarter second, and half a second: the line without a voice is skipped.
    const sound = page.getByTestId('sound');
    await expect(sound).toHaveAttribute('src', /^blob:/);
    await expect
      .poll(() => sound.evaluate((audio) => (audio as HTMLAudioElement).duration))
      .toBeGreaterThan(1.6);
    expect(await sound.evaluate((audio) => (audio as HTMLAudioElement).duration)).toBeLessThan(2.1);
    // No line has a screenshot.
    await expect(page.getByTestId('preview-picture')).toHaveText('These lines have no screenshot.');
  });

  test('follows the game, and stays where the user reads', async ({ openPage, game }) => {
    for (let number = 1; number <= 40; number += 1)
      await game.say({ text: `これは${number}行目。` });
    const page = await openPage();

    // The newest line is in view when the page opens, and each new one after it.
    await expect(line(page, 40)).toBeInViewport();
    await game.say({ text: '四十一行目。' });
    await expect(line(page, 41)).toBeInViewport();

    // Reading back: new lines no longer move the page.
    await line(page, 20).scrollIntoViewIfNeeded();
    await line(page, 20).getByRole('checkbox').click();
    await expect(page.getByTestId('follow')).toHaveAttribute('aria-checked', 'false');
    await game.say({ text: '四十二行目。' });
    await expect(line(page, 42)).toBeAttached();
    await expect(line(page, 42)).not.toBeInViewport();
    await expect(line(page, 20)).toBeInViewport();

    await page.getByTestId('go-to-newest').click();
    await expect(line(page, 42)).toBeInViewport();
    await expect(page.getByTestId('go-to-newest')).toBeHidden();
  });

  test('catches up when the user comes back from the game', async ({ openPage, game }) => {
    for (let number = 1; number <= 30; number += 1)
      await game.say({ text: `これは${number}行目。` });
    const page = await openPage();
    await expect(line(page, 30)).toBeInViewport();
    await page.mouse.wheel(0, -2_000);
    await page.locator('main').dispatchEvent('wheel');
    await expect(page.getByTestId('follow')).toHaveAttribute('aria-checked', 'false');

    // The game is in front, and goes on.
    await setVisibility(page, 'hidden');
    for (let number = 31; number <= 35; number += 1)
      await game.say({ text: `これは${number}行目。` });
    await page.waitForTimeout(300);
    await expect(line(page, 31)).toHaveCount(0);

    // Back on the page: what the game showed meanwhile is there, the newest line in view.
    await setVisibility(page, 'visible');
    await expect(line(page, 35)).toBeInViewport();
    await expect(page.locator('.line')).toHaveCount(35);
    await expect(page.getByTestId('follow')).toHaveAttribute('aria-checked', 'true');
  });

  test('reads earlier lines when asked, and keeps the place', async ({ openPage, game }) => {
    test.slow();
    for (let number = 1; number <= 320; number += 1)
      await game.say({ text: `これは${number}行目。` });
    const page = await openPage();
    await expect(page.locator('.line')).toHaveCount(300);
    await expect(line(page, 20)).toHaveCount(0);

    await page.getByTestId('show-earlier').scrollIntoViewIfNeeded();
    await expect(line(page, 21)).toBeInViewport();
    await page.getByTestId('show-earlier').click();
    await expect(page.locator('.line')).toHaveCount(320);
    await expect(line(page, 1).locator('.said')).toHaveText('これは1行目。');
    // The line that was at the top is still in view, with the earlier ones above it.
    await expect(line(page, 21)).toBeInViewport();
    await expect(page.getByTestId('show-earlier')).toHaveCount(0);
  });

  test('lets the oldest lines go as the server does, and empties when asked', async ({
    openPage,
    game,
    api,
    media,
  }) => {
    await saveSettings(api, { Stream: { MaxLines: 50 } });
    for (let number = 1; number <= 50; number += 1) {
      await game.say({
        text: `これは${number}行目。`,
        ...(number === 1 ? { audio: media.voice } : {}),
      });
    }
    const page = await openPage();
    await expect(page.locator('.line')).toHaveCount(50);

    // Five more lines: the server keeps fifty, and so does the page.
    for (let number = 51; number <= 55; number += 1)
      await game.say({ text: `これは${number}行目。` });
    await expect(line(page, 55)).toBeVisible();
    await expect(page.locator('.line')).toHaveCount(50);
    await expect(line(page, 5)).toHaveCount(0);
    expect((await api.get('/Api/Lines/Audio?lines=1')).status()).toBe(404);

    await page.getByTestId('open-settings').click();
    await expect(page.getByTestId('max-lines')).toHaveValue('50');
    await page.getByTestId('empty-stream').click();
    await page.getByTestId('empty-stream-yes').click();
    await expect(page.getByTestId('emptied')).toBeVisible();
    await page.getByTestId('settings-back').click();
    await expect(page.getByTestId('no-lines')).toBeVisible();
    expect(((await (await api.get('/Api/Status')).json()) as { Lines: number }).Lines).toBe(0);

    // The numbers go on: no later line is taken for one that is gone.
    const next = await game.say({ text: '新しい行。' });
    expect(next.Id).toBe(56);
    await expect(line(page, 56)).toBeVisible();
  });

  test('hands out the file that makes a game send its lines here', async ({
    openPage,
    server,
    media,
  }) => {
    const page = await openPage();
    await page.getByTestId('get-game-file').click();

    // As a download for the game's folder.
    const download = page.waitForEvent('download');
    await page.getByTestId('game-file').click();
    const file = await download;
    expect(file.suggestedFilename()).toBe('mining.txt');
    const chunks: Buffer[] = [];
    for await (const chunk of await file.createReadStream()) chunks.push(chunk as Buffer);
    const text = Buffer.concat(chunks).toString('utf8');
    expect(text).toContain(`server = ${server.url}\n`);
    expect(text).toContain(`key = ${server.key}\n`);

    // And as text to copy, the same.
    await page.getByTestId('show-game-file').click();
    await expect(page.getByTestId('game-file-text')).toContainText(`key = ${server.key}`);

    // A game that reads the file reaches the server with it.
    const game = PretendGame.fromFile(text, 'Another Novel');
    await game.say({ text: 'ファイルから来た行。', audio: media.voice });
    await page.getByTestId('settings-back').click();
    await expect(line(page, 1).locator('.said')).toHaveText('ファイルから来た行。');
    await expect(page.getByTestId('title')).toHaveText('Another Novel');
  });
});
