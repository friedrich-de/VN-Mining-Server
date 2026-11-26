import type { APIRequestContext, Page } from '@playwright/test';

import { guessRoles } from '../../src/anki/pageSettings';
import { LAPIS_FIELDS } from '../../src/testing/ankiCollection';
import type { StandInAnki } from '../support/ankiConnect';
import {
  type DictionaryList,
  importEnded,
  marksDictionary,
  sendDictionary,
  serveFiles,
  uploadDictionary,
  wordsDictionary,
} from '../support/dictionaries';
import { expect, saveSettings, test } from '../support/fixtures';
import type { PretendGame } from '../support/game';
import { tapCharacter } from '../support/lines';
import { probePicture, probeSound, type SampleMedia } from '../support/media';
import { jpodOf, setWordAudio, startAudioSource } from '../support/wordAudio';
import { yomitanZip } from '../support/yomitanZip';

const popup = (page: Page) => page.getByTestId('word-popup');
const line = (page: Page, id: number) => page.locator(`.line[data-line="${id}"]`);

/** Two spoken lines, each with its voice and a screenshot. */
async function scene(game: PretendGame, media: SampleMedia): Promise<void> {
  await game.say({
    text: '昨日の話、覚えてる？',
    name: 'ユキ',
    audio: media.voice,
    screenshot: media.screenshot,
  });
  await game.say({
    text: '約束したからね。',
    name: 'ハル',
    audio: media.voice2,
    screenshot: media.screenshotJpeg,
  });
}

/** Cards go to this AnkiConnect through the server, as Lapis notes in the deck Mining. */
async function useAnki(api: APIRequestContext, anki: StandInAnki, tags: string[]): Promise<void> {
  await saveSettings(api, {
    Anki: { Where: 'server', ServerAddress: anki.url, ServerKey: '' },
    Page: {
      anki: { deck: 'Mining', noteType: 'Lapis', fields: guessRoles(LAPIS_FIELDS), tags },
    },
  });
}

/**
 * Selects a part of a definition in the popup: with the mouse on a desktop, as a long press
 * leaves it on a phone.
 */
async function selectInPopup(page: Page, text: string, mouse: boolean): Promise<void> {
  const ends = await popup(page)
    .locator('.dictionary-popup')
    .evaluate(
      (host, [wanted, drag]) => {
        const root = host.shadowRoot!;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
          const at = (node as Text).data.indexOf(wanted!);
          if (at === -1) continue;
          const range = document.createRange();
          range.setStart(node, at);
          range.setEnd(node, at + wanted!.length);
          if (drag === 'no') {
            document.getSelection()!.removeAllRanges();
            document.getSelection()!.addRange(range);
            return null;
          }
          const box = range.getBoundingClientRect();
          return { y: box.top + box.height / 2, from: box.left + 1, to: box.right - 1 };
        }
        throw new Error(`No ${wanted} in the popup`);
      },
      [text, mouse ? 'yes' : 'no'],
    );
  if (ends === null) return;
  await page.mouse.move(ends.from, ends.y);
  await page.mouse.down();
  await page.mouse.move(ends.to, ends.y, { steps: 5 });
  await page.mouse.up();
}

test.describe('a word tapped in a line', () => {
  test.beforeEach(async ({ api }) => {
    await uploadDictionary(api, wordsDictionary());
  });

  test('is looked up on the server and shown with its picture', async ({
    openPage,
    game,
    media,
  }) => {
    await scene(game, media);
    const page = await openPage();
    await expect(line(page, 2)).toBeVisible();

    const picture = page.waitForResponse((response) =>
      response.url().includes('/Media?path=img%2Fbowl.png'),
    );
    await tapCharacter(page, line(page, 1), '話');
    await expect(popup(page)).toBeVisible();
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    // The word is picked out in its line, and choosing the line is left to its box.
    await expect(line(page, 1).locator('.word')).toHaveText('話');
    await expect(line(page, 1).getByRole('checkbox')).not.toBeChecked();

    // The picture of the definition came with the sign-in, and the popup shows it.
    expect((await picture).status()).toBe(200);
    const image = popup(page).locator('img').first();
    await expect(image).toBeVisible();
    await expect
      .poll(() => image.evaluate((element) => (element as HTMLImageElement).naturalWidth))
      .toBe(1);

    // The popup's own stylesheets and the dictionary's are adopted, as the page's policy needs.
    const sheets = await popup(page)
      .locator('.dictionary-popup')
      .evaluate((host) => host.shadowRoot?.adoptedStyleSheets.length ?? 0);
    expect(sheets).toBeGreaterThanOrEqual(3);

    await tapCharacter(page, line(page, 1), '昨');
    await expect(popup(page).getByText('yesterday')).toBeVisible();
    await expect(line(page, 1).locator('.word')).toHaveText('昨日');

    // A word of another line, with its frequency. The popup lies over that line until it is closed.
    await page.keyboard.press('Escape');
    await expect(popup(page)).toBeHidden();
    await tapCharacter(page, line(page, 2), '約');
    await expect(popup(page).getByText('promise')).toBeVisible();
    await expect(popup(page)).toContainText('812');
    await expect(line(page, 2).locator('.word')).toHaveText('約束');
    await expect(line(page, 1).locator('.word')).toHaveCount(0);

    await page.keyboard.press('Escape');
    await expect(popup(page)).toBeHidden();
    await expect(line(page, 2).locator('.word')).toHaveCount(0);

    // Without Anki the popup says how to make cards.
    await tapCharacter(page, line(page, 1), '話');
    await expect(popup(page)).toContainText('Choose where your cards go in the settings');
    // A tap beside the words closes it, and a tap on the line's box still chooses the line.
    await page.getByTestId('title').click();
    await expect(popup(page)).toBeHidden();
    await line(page, 1).getByRole('checkbox').click();
    await expect(line(page, 1).getByRole('checkbox')).toBeChecked();
  });

  test('shows the pictures a dictionary’s zip names in Shift-JIS', async ({
    openPage,
    game,
    media,
    api,
  }) => {
    // As a real Daijirin comes: its marks are named in Shift-JIS, with the names in UTF-8 beside
    // them, where Yomitan reads them.
    await uploadDictionary(api, marksDictionary());
    await scene(game, media);
    const page = await openPage();
    await expect(line(page, 2)).toBeVisible();
    const pictures: string[] = [];
    page.on('response', (response) => {
      if (response.url().includes('/Media?path=gaiji')) pictures.push(String(response.status()));
    });
    await tapCharacter(page, line(page, 1), '昨');
    await expect(popup(page).getByText('(ナリ) the day before today')).toBeVisible();

    // Both marks, the SVG and the PNG, are drawn, and none failed.
    const marks = popup(page).locator('.dictionary-popup [data-image-load-state]');
    await expect(marks).toHaveCount(2);
    await expect
      .poll(() =>
        marks.evaluateAll((links) =>
          links.map((link) => link.getAttribute('data-image-load-state')),
        ),
      )
      .toEqual(['loaded', 'loaded']);
    await expect(popup(page)).not.toContainText('Image failed to load');
    expect(pictures).toEqual(['200', '200']);
  });

  test('stays in view while the game goes on', async ({ openPage, game, media }) => {
    await scene(game, media);
    const page = await openPage();
    await tapCharacter(page, line(page, 1), '話');
    await expect(popup(page).getByText('talk; story')).toBeVisible();

    // The game shows more lines than the screen holds: the word looked up is not moved away.
    for (let number = 1; number <= 30; number += 1)
      await game.say({ text: `これは${number}行目。` });
    await expect(line(page, 32)).toBeAttached();
    await expect(popup(page).getByText('talk; story')).toBeVisible();
    await expect(line(page, 1)).toBeInViewport();
  });

  test('becomes a new card with its reading, meaning, frequency, pitch, picture and sound', async ({
    openPage,
    game,
    media,
    api,
    anki,
  }) => {
    const server = await anki();
    await useAnki(api, server, ['e2e']);
    await scene(game, media);
    const page = await openPage();

    await tapCharacter(page, line(page, 2), '約');
    await expect(popup(page).getByText('promise')).toBeVisible();
    const add = popup(page).locator('.gsm-hoshidicts-mine-button').first();
    await expect(add).toHaveAttribute('data-card-state', 'add');
    await add.click();
    await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "約束".');
    await expect(add).toHaveAttribute('data-card-state', 'in-anki');

    const note = [...server.collection.notes.values()].at(-1)!;
    expect(note).toMatchObject({ deck: 'Mining', modelName: 'Lapis', tags: ['e2e'] });
    expect(note.fields).toMatchObject({
      Expression: '約束',
      ExpressionReading: 'やくそく',
      ExpressionFurigana: '約束[やくそく]',
      MainDefinition: '<div class="yomitan-glossary" style="text-align: left">promise</div>',
      Sentence: '<b>約束</b>したからね。',
      Frequency: '812',
      PitchPosition: '0',
      MiscInfo: 'Sample Novel',
    });
    // The screenshot and the voice of the word's own line.
    const pictureName = /<img src="([^"]+)">/.exec(note.fields.Picture ?? '')?.[1];
    expect(pictureName).toMatch(/^vn-miner_2_[0-9a-f]{10}\.jpg$/);
    expect((await probePicture(server.media(pictureName!))).width).toBe(640);
    const soundName = /^\[sound:([^\]]+)\]$/.exec(note.fields.SentenceAudio ?? '')?.[1];
    const sound = await probeSound(server.media(soundName!));
    expect(Math.abs(sound.seconds - 0.5), `lasts ${sound.seconds} s`).toBeLessThan(0.15);

    // Looked up again, Anki says the deck has it.
    await page.keyboard.press('Escape');
    await tapCharacter(page, line(page, 2), '約');
    await expect(popup(page).locator('.gsm-hoshidicts-mine-button').first()).toHaveAttribute(
      'data-card-state',
      'in-anki',
    );
    expect(server.seen.some((call) => call.action === 'canAddNotesWithErrorDetail')).toBe(true);
    expect(server.collection.notes.size).toBe(1);
  });

  test('takes the chosen lines as its sentence when it is in one of them', async ({
    openPage,
    game,
    media,
    api,
    anki,
  }) => {
    const server = await anki();
    await useAnki(api, server, []);
    await scene(game, media);
    const page = await openPage();

    await line(page, 1).getByRole('checkbox').click();
    await line(page, 2).getByRole('checkbox').click();
    await expect(page.getByTestId('selected-count')).toHaveText('2 lines selected');
    await tapCharacter(page, line(page, 2), '約');
    await popup(page).locator('.gsm-hoshidicts-mine-button').first().click();
    await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "約束".');

    const note = [...server.collection.notes.values()].at(-1)!;
    expect(note.fields.Sentence).toBe('昨日の話、覚えてる？<br><b>約束</b>したからね。');
    // Both voices, and the screenshot of the first line.
    const pictureName = /<img src="([^"]+)">/.exec(note.fields.Picture ?? '')?.[1];
    expect(pictureName).toMatch(/^vn-miner_1_/);
    const soundName = /^\[sound:([^\]]+)\]$/.exec(note.fields.SentenceAudio ?? '')?.[1];
    const sound = await probeSound(server.media(soundName!));
    expect(Math.abs(sound.seconds - 1.75), `lasts ${sound.seconds} s`).toBeLessThan(0.15);
  });

  test('becomes a card with its sound and only the part of its definition selected', async ({
    openPage,
    game,
    media,
    api,
    anki,
  }, info) => {
    const source = await startAudioSource({ 話: media.wordSound });
    const server = await anki();
    try {
      await setWordAudio(api, source.address);
      await useAnki(api, server, []);
      await scene(game, media);
      const page = await openPage();

      await tapCharacter(page, line(page, 1), '話');
      await expect(popup(page).getByText('talk; story')).toBeVisible();

      // The play button plays the word's sound, fetched by the server.
      const play = popup(page).getByRole('button', { name: 'Play pronunciation' }).first();
      await expect(play).toBeVisible();
      const heard = page.waitForResponse((response) => response.url().includes('/WordAudio?'));
      await play.click();
      expect((await heard).status()).toBe(200);
      await expect(page.getByTestId('word-status')).toHaveCount(0);

      // Held, it lists the word's sounds, as Yomitan's does; the one chosen plays and is kept.
      const box = (await play.boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.waitForTimeout(700);
      await page.mouse.up();
      const sounds = popup(page).locator('.gsm-hoshidicts-audio-menu-item');
      await expect(sounds).toHaveText(['gone', 'nhk16', 'jpod']);
      const chosen = page.waitForResponse((response) => response.url().includes('source=jpod'));
      await sounds.filter({ hasText: 'jpod' }).click();
      expect((await chosen).status()).toBe(200);
      await expect(sounds).toHaveCount(0);

      await selectInPopup(page, 'story', info.project.name !== 'phone');
      await popup(page).locator('.gsm-hoshidicts-mine-button').first().click();
      await expect(page.getByTestId('word-status')).toHaveText('Made a new card, "話".');

      const note = [...server.collection.notes.values()].at(-1)!;
      expect(note.fields.SelectionText).toBe('story');
      expect(note.fields.MainDefinition).toContain('talk; story');
      const name = /^\[sound:([^\]]+)\]$/.exec(note.fields.ExpressionAudio ?? '')?.[1];
      expect(name).toMatch(/^vn-miner_word_[0-9a-f]{8}\.mp3$/);
      // The card has the sound chosen.
      expect(Buffer.compare(server.media(name!), jpodOf(media.wordSound))).toBe(0);

      // A word the source has no sound for still gets its card, without one.
      await page.keyboard.press('Escape');
      await tapCharacter(page, line(page, 1), '昨');
      await expect(popup(page).getByText('yesterday')).toBeVisible();
      await popup(page).locator('.gsm-hoshidicts-mine-button').first().click();
      await expect(page.getByTestId('word-status')).toHaveText(
        'Made a new card, "昨日", without the word\'s sound: the audio source has none for it.',
      );
      const quiet = [...server.collection.notes.values()].at(-1)!;
      expect(quiet.fields).toMatchObject({
        Expression: '昨日',
        ExpressionAudio: '',
        SelectionText: '',
      });
    } finally {
      await source.stop();
    }
  });

  test('is left to Yomitan when the user turns looking up off', async ({
    openPage,
    game,
    media,
  }) => {
    await scene(game, media);
    const page = await openPage();
    await expect(line(page, 2)).toBeVisible();

    await page.getByTestId('open-settings').click();
    await expect(page.getByTestId('look-up')).toBeChecked();
    await expect(page.getByTestId('dictionaries')).toContainText('In E2E Words');
    await page.getByTestId('look-up').uncheck();
    await page.getByTestId('save').click();
    await expect(page.getByTestId('settings-message')).toHaveText('Saved.');
    await page.getByTestId('settings-back').click();

    await tapCharacter(page, line(page, 1), '話');
    await expect(line(page, 1).getByRole('checkbox')).toBeChecked();
    await expect(popup(page)).toHaveCount(0);
  });
});

test.describe('the dictionaries in the settings', () => {
  test('are added, turned off and removed', async ({ openPage, api }) => {
    await uploadDictionary(api, wordsDictionary());
    const page = await openPage();
    await page.getByTestId('open-settings').click();
    const section = page.getByTestId('dictionaries');
    await expect(section.getByTestId('dictionary-E2E Words')).toContainText('4 words');

    await page.getByTestId('dictionary-file').setInputFiles({
      name: 'more.zip',
      mimeType: 'application/zip',
      buffer: yomitanZip(wordsDictionary('More Words')),
    });
    await expect(page.getByTestId('dictionary-message')).toHaveText('More Words is ready.');
    await expect(section.getByTestId('dictionary-More Words')).toBeVisible();

    await page.getByRole('switch', { name: 'Use E2E Words' }).click();
    await expect(page.getByRole('switch', { name: 'Use E2E Words' })).toHaveAttribute(
      'aria-checked',
      'false',
    );
    await expect(section).toContainText('In More Words');

    await page.getByRole('button', { name: 'Remove More Words' }).click();
    await page.getByTestId('confirm-remove').click();
    await expect(section.getByTestId('dictionary-More Words')).toHaveCount(0);

    // An address where nothing answers says so.
    await page.getByTestId('dictionary-address').fill('http://127.0.0.1:9/none.zip');
    await page.getByTestId('dictionary-download').click();
    await expect(page.getByTestId('dictionary-message')).toContainText(
      'The address could not be reached:',
    );
  });

  test('take several files picked at once, and say how each of them ended', async ({
    openPage,
    api,
  }) => {
    await uploadDictionary(api, wordsDictionary());
    const page = await openPage();
    await page.getByTestId('open-settings').click();
    const section = page.getByTestId('dictionaries');
    await expect(section.getByTestId('dictionary-E2E Words')).toBeVisible();

    await page.getByTestId('dictionary-file').setInputFiles([
      {
        name: 'one.zip',
        mimeType: 'application/zip',
        buffer: yomitanZip(wordsDictionary('One More')),
      },
      { name: 'other.zip', mimeType: 'application/zip', buffer: Buffer.from('not a zip') },
      {
        name: 'two.zip',
        mimeType: 'application/zip',
        buffer: yomitanZip(wordsDictionary('Two More')),
      },
    ]);

    await expect(page.getByTestId('dictionary-message')).toHaveText(
      '2 dictionaries are ready: One More, Two More.\n' +
        'other.zip: That file is not a zip. A Yomitan dictionary is a zip with an index.json in it.',
      { useInnerText: true },
    );
    // In the order they were picked, behind the one that was there.
    await expect(section.locator('.dictionary .title')).toHaveText([
      'E2E Words',
      'One More',
      'Two More',
    ]);
    await expect(page.getByTestId('dictionary-file')).toBeVisible();
  });

  test('wait their turn when they are sent while another is imported', async ({ api }) => {
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const publisher = await serveFiles(
      { '/first.zip': yomitanZip(wordsDictionary('First')) },
      held,
    );
    try {
      const started = await api.post('/Api/Dictionaries/Download', {
        data: { Url: `${publisher.url}/first.zip` },
      });
      expect(started.status(), await started.text()).toBe(202);

      const second = await sendDictionary(api, 'second.zip', yomitanZip(wordsDictionary('Second')));
      expect(second.Import).toMatchObject({ State: 'downloading', Waiting: 1 });
      await sendDictionary(api, 'other.zip', Buffer.from('not a zip'));
      const fourth = await sendDictionary(api, 'fourth.zip', yomitanZip(wordsDictionary('Fourth')));
      expect(fourth.Import).toMatchObject({
        State: 'downloading',
        Source: `${publisher.url}/first.zip`,
        Waiting: 3,
      });
      expect(fourth.Dictionaries).toEqual([]);

      release();
      const list = await importEnded(api);
      expect(list.Dictionaries.map((item) => item.Title)).toEqual(['First', 'Second', 'Fourth']);
      expect(list.Import).toMatchObject({ State: 'done', Title: 'Fourth', Waiting: 0 });
      expect(
        list.Import.Finished.map((outcome) => [
          outcome.Number,
          outcome.Source,
          outcome.Title ?? outcome.Error,
        ]),
      ).toEqual([
        [1, `${publisher.url}/first.zip`, 'First'],
        [2, 'second.zip', 'Second'],
        [
          3,
          'other.zip',
          'That file is not a zip. A Yomitan dictionary is a zip with an index.json in it.',
        ],
        [4, 'fourth.zip', 'Fourth'],
      ]);
    } finally {
      release();
      await publisher.stop();
    }
  });

  test('are let go of when the import they wait behind is stopped', async ({ api }) => {
    const publisher = await serveFiles(
      { '/first.zip': yomitanZip(wordsDictionary('First')) },
      new Promise(() => undefined),
    );
    try {
      await api.post('/Api/Dictionaries/Download', {
        data: { Url: `${publisher.url}/first.zip` },
      });
      const waits = await sendDictionary(api, 'waits.zip', yomitanZip(wordsDictionary('Waits')));
      expect(waits.Import.Waiting).toBe(1);

      const stopped = await api.delete('/Api/Dictionaries/Import');
      expect(stopped.status(), await stopped.text()).toBe(200);
      expect(((await stopped.json()) as DictionaryList).Import.Waiting).toBe(0);

      const list = await importEnded(api);
      expect(list.Import).toMatchObject({ State: 'failed', Error: 'The import was stopped.' });
      expect(list.Dictionaries).toEqual([]);
      expect(list.Import.Finished.map((outcome) => [outcome.Source, outcome.Error])).toEqual([
        ['waits.zip', 'The import was stopped.'],
        [`${publisher.url}/first.zip`, 'The import was stopped.'],
      ]);
    } finally {
      await publisher.stop();
    }
  });

  test('take an audio source for words, which the page never shows again', async ({ openPage }) => {
    const source = await startAudioSource({ 食べる: Buffer.from('ID3') });
    try {
      const page = await openPage();
      await page.getByTestId('open-settings').click();
      await expect(page.getByTestId('word-audio-state')).toContainText('Words have no sound.');

      await page.getByTestId('word-audio-address').fill(source.address);
      await page.getByTestId('word-audio-save').click();
      await expect(page.getByTestId('word-audio-message')).toHaveText(
        'Saved. It works: it has a sound for 食べる.',
      );
      await expect(page.getByTestId('word-audio-state')).toContainText(
        `come from ${new URL(source.url).hostname}`,
      );
      await expect(page.getByTestId('word-audio-address')).toHaveValue('');
      expect(await page.content()).not.toContain('apiKey');

      await page.getByTestId('word-audio-remove').click();
      await expect(page.getByTestId('word-audio-message')).toHaveText('Words have no sound now.');
    } finally {
      await source.stop();
    }
  });
});
