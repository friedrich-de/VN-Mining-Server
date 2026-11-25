import type { Page } from '@playwright/test';

import { expect, test } from '../support/fixtures';
import type { PretendGame } from '../support/game';
import { probePicture, probeSound, type SampleMedia } from '../support/media';

const KEY = 'e2e-anki-key';

const line = (page: Page, id: number) => page.locator(`.line[data-line="${id}"]`);

/** The names of the files a card's fields point at. */
function pictureIn(field: string | undefined): string {
  const name = /<img src="([^"]+)">/.exec(field ?? '')?.[1];
  if (name === undefined) throw new Error(`No picture in ${field}`);
  return name;
}

function soundIn(field: string | undefined): string {
  const name = /\[sound:([^\]]+)\]/.exec(field ?? '')?.[1];
  if (name === undefined) throw new Error(`No sound in ${field}`);
  return name;
}

/** Three lines of a scene: two spoken, the first with a screenshot, and one of narration. */
async function scene(game: PretendGame, media: SampleMedia): Promise<void> {
  await game.say({
    text: '昨日の話、覚えてる？',
    name: 'ユキ',
    audio: media.voice,
    screenshot: media.screenshotJpeg,
  });
  await game.say({ text: 'もちろん。', name: 'ハル', audio: media.voice2 });
  await game.say({ text: '約束したからね。' });
}

async function openSettings(page: Page): Promise<void> {
  await page.getByTestId('open-settings').click();
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();
}

test.describe('cards', () => {
  test('go to the AnkiConnect on a server, with a key the page never gets', async ({
    openPage,
    game,
    media,
    anki,
  }) => {
    const server = await anki({ key: KEY });
    // A card Yomitan made a moment ago, with the word it looked up in bold.
    const id = server.collection.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '約束', Sentence: '<b>約束</b>したからね。' },
    });
    // A deck of the user's own besides: only the card says which deck is for mining.
    server.collection.decks.push('Kanji');
    await scene(game, media);

    const page = await openPage();
    const settingsSeen: Promise<string>[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/Api/Settings')) settingsSeen.push(response.text());
    });
    await expect(line(page, 3)).toBeVisible();

    // Before Anki is set up, the page says how.
    await line(page, 1).getByRole('checkbox').click();
    await page.getByTestId('set-up-anki').click();
    await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible();

    await page.getByTestId('where-server').check();
    await page.getByTestId('server-address').fill(server.url);
    await page.getByTestId('server-key').fill('not-the-key');
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connect-error')).toHaveText(
      'The AnkiConnect on your server did not take the key. Check it in the settings.',
    );
    await page.getByTestId('server-key').fill(KEY);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toHaveText('Connected: 3 decks, 2 note types.');
    await expect(page.getByTestId('server-key')).toHaveValue('');

    // A new user's Lapis is chosen for them, with the deck its newest card is in.
    await expect(page.getByTestId('lapis-found')).toContainText('"Lapis" is a Lapis note type');
    await expect(page.getByTestId('note-type')).toHaveValue('Lapis');
    await expect(page.getByTestId('deck')).toHaveValue('Mining');
    await expect(page.getByTestId('role-Sentence')).toHaveValue('sentence');
    await expect(page.getByTestId('role-SentenceAudio')).toHaveValue('sound');
    await expect(page.getByTestId('role-Picture')).toHaveValue('picture');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await line(page, 3)
      .getByRole('checkbox')
      .click({ modifiers: ['Shift'] });
    await expect(page.getByTestId('selected-count')).toHaveText('3 lines selected');
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await expect(page.getByTestId('newest-card')).toHaveText('Adds to "約束", made just now.');
    await page.getByTestId('add-to-newest').click();
    await expect(page.getByTestId('sending')).toContainText('Added to "約束".');
    await expect(page.getByTestId('selected-count')).toHaveCount(0);

    const note = server.collection.notes.get(id)!;
    expect(note.fields).toMatchObject({
      Expression: '約束',
      Sentence: '昨日の話、覚えてる？<br>もちろん。<br><b>約束</b>したからね。',
      MiscInfo: 'Sample Novel',
    });
    // The screenshot of the first line at the width of the settings, and both voices in one sound:
    // a second, a pause of a quarter second, and half a second.
    const pictureName = pictureIn(note.fields.Picture);
    expect(pictureName).toMatch(/^vn-miner_1_[0-9a-f]{10}\.jpg$/);
    expect(await probePicture(server.media(pictureName))).toEqual({
      width: 640,
      height: 480,
      codec: 'mjpeg',
    });
    const soundName = soundIn(note.fields.SentenceAudio);
    expect(soundName).toMatch(/^vn-miner_1_[0-9a-f]{10}\.mp3$/);
    const sound = await probeSound(server.media(soundName));
    expect(sound.codec).toBe('mp3');
    expect(Math.abs(sound.seconds - 1.75), `lasts ${sound.seconds} s`).toBeLessThan(0.15);

    // Every call came from the mining server with the key; the page never had it.
    expect(server.seen.filter((call) => call.origin !== undefined)).toEqual([]);
    expect(server.seen.filter((call) => call.key === KEY).length).toBeGreaterThan(5);
    const bodies = await Promise.all(settingsSeen);
    expect(bodies.length).toBeGreaterThan(0);
    for (const body of bodies) expect(body).not.toContain(KEY);

    await page.getByTestId('undo').click();
    await expect(page.getByTestId('sending')).toContainText('The card is as it was.');
    expect(note.fields.Sentence).toBe('<b>約束</b>したからね。');
    expect(note.fields.Picture).toBe('');

    // The bar has the server's Anki sync with AnkiWeb now, through the mining server.
    const release = server.hold('sync');
    await page.getByTestId('sync-anki').click();
    await expect(page.getByTestId('sync-status')).toHaveText('Syncing Anki with AnkiWeb…');
    await expect(page.getByTestId('sync-anki')).toBeDisabled();
    release();
    await expect(page.getByTestId('sync-status')).toHaveText(/^Anki synced with AnkiWeb\./);
    await expect(page.getByTestId('sync-anki')).toBeEnabled();
    expect(server.collection.sync.count).toBe(1);
    expect(server.seen.at(-1)).toMatchObject({ action: 'sync', key: KEY, origin: undefined });
  });

  test('go to the Anki on the device once it lets the page in', async ({
    openPage,
    server,
    game,
    media,
    anki,
  }) => {
    const device = await anki({ key: KEY });
    const origin = new URL(server.url).origin;
    await scene(game, media);

    const page = await openPage();
    await expect(line(page, 3)).toBeVisible();
    await openSettings(page);

    await page.getByTestId('where-device').check();
    await expect(page.getByText(`"webCorsOriginList": ["${origin}"]`)).toBeVisible();
    await page.getByTestId('device-address').fill(device.url);
    await page.getByTestId('device-key').fill(KEY);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connect-error')).toHaveText(
      `Anki could not be reached at ${device.url}. Is Anki open, with AnkiConnect letting this page in?`,
    );

    // The user puts the page's address into AnkiConnect's settings.
    device.allow(origin);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toHaveText('Connected: 2 decks, 2 note types.');
    await expect(page.getByTestId('note-type')).toHaveValue('Lapis');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    // No card made yet: the button is off, and the page says why.
    await line(page, 1).getByRole('checkbox').click();
    await expect(page.getByTestId('newest-card')).toHaveText(
      'Anki has no "Lapis" card made today. Make one with Yomitan first, or make a new card.',
    );
    await expect(page.getByTestId('add-to-newest')).toBeDisabled();

    // Yomitan makes one meanwhile: the page finds it by itself, and names it.
    const id = device.collection.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '昨日', Sentence: '<b>昨日</b>の話、覚えてる？' },
    });
    await expect(page.getByTestId('newest-card')).toHaveText('Adds to "昨日", made just now.', {
      timeout: 15_000,
    });
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await page.getByTestId('add-to-newest').click();
    await expect(page.getByTestId('sending')).toContainText('Added to "昨日".');

    const note = device.collection.notes.get(id)!;
    expect(note.fields.Sentence).toBe('<b>昨日</b>の話、覚えてる？');
    const pictureName = pictureIn(note.fields.Picture);
    expect(pictureName).toMatch(/^vn-miner_1_[0-9a-f]+\.jpg$/);
    expect((await probePicture(device.media(pictureName))).width).toBe(640);
    const sound = await probeSound(device.media(soundIn(note.fields.SentenceAudio)));
    expect(Math.abs(sound.seconds - 1), `lasts ${sound.seconds} s`).toBeLessThan(0.15);

    // The browser called Anki itself, from the page's origin.
    const stores = device.seen.filter((call) => call.action === 'storeMediaFile');
    expect(stores).toHaveLength(2);
    expect(stores.every((call) => call.origin === origin && call.key === KEY)).toBe(true);
  });

  test('become new cards of a note type of the user’s own', async ({
    openPage,
    game,
    media,
    anki,
  }) => {
    const server = await anki();
    await scene(game, media);
    const page = await openPage();
    await expect(line(page, 3)).toBeVisible();
    await openSettings(page);
    await page.getByTestId('where-server').check();
    await page.getByTestId('server-address').fill(server.url);
    await page.getByTestId('connect').click();
    await expect(page.getByTestId('connected')).toBeVisible();

    await page.getByTestId('deck').selectOption('Mining');
    await page.getByTestId('note-type').selectOption('Basic');
    await page.getByTestId('role-Front').selectOption('sentence');
    await page.getByTestId('role-Back').selectOption('sound');
    await page.getByTestId('tags').fill('e2e mined');
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await line(page, 2).getByRole('checkbox').click();
    await line(page, 3).getByRole('checkbox').click();
    await expect(page.getByTestId('selected-count')).toHaveText('2 lines selected');
    await expect(page.getByTestId('play-sound')).toBeEnabled();
    await page.getByTestId('add-new').click();
    await expect(page.getByTestId('sending')).toContainText(
      'Made a new card, "もちろん。 約束したからね。".',
    );
    await expect(page.getByTestId('undo')).toHaveCount(0);

    const note = [...server.collection.notes.values()].at(-1)!;
    expect(note).toMatchObject({
      deck: 'Mining',
      modelName: 'Basic',
      tags: ['e2e', 'mined'],
      fields: { Front: 'もちろん。<br>約束したからね。' },
    });
    const sound = await probeSound(server.media(soundIn(note.fields.Back)));
    expect(Math.abs(sound.seconds - 0.5), `lasts ${sound.seconds} s`).toBeLessThan(0.15);

    // The lines were let go of with the card; chosen again, they would make the same card twice.
    await expect(page.getByTestId('selected-count')).toHaveCount(0);
    await expect(line(page, 2).getByRole('checkbox')).not.toBeChecked();
    await line(page, 2).getByRole('checkbox').click();
    await line(page, 3).getByRole('checkbox').click();
    await page.getByTestId('add-new').click();
    await expect(page.getByTestId('sending')).toContainText(
      'This deck has a card like this already.',
    );
    await expect(page.getByTestId('selected-count')).toHaveText('2 lines selected');
    expect(server.collection.notes.size).toBe(1);
  });
});
