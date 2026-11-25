import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';

import { defaultPageSettings, guessRoles } from '@/anki/pageSettings';
import { createServerApi } from '@/api/server';
import type { SettingsChange, SettingsView } from '@/api/types';
import { type Collection, createCollection, LAPIS_FIELDS } from '@/testing/ankiCollection';
import { BASE, fakeRoutes, json, problem, type RoutedRequest } from '@/testing/fakes';

import SettingsScreen from './SettingsScreen.vue';

const DEVICE = 'http://127.0.0.1:8765';

function view(overrides: Partial<SettingsView['Anki']> = {}, rest: Partial<SettingsView> = {}) {
  return {
    Anki: {
      Where: 'off',
      DeviceAddress: DEVICE,
      DeviceKey: '',
      ServerAddress: '',
      ServerKeySet: false,
      ...overrides,
    },
    Stream: { MaxLines: 1000 },
    ...rest,
  } satisfies SettingsView;
}

/**
 * The server, which keeps the settings, passes calls on to the AnkiConnect on a server, and an
 * AnkiConnect on the device that the browser calls itself.
 */
function backend(start: SettingsView, anki: { server?: Collection; device?: Collection } = {}) {
  let lines = 12;
  let stored: SettingsView = structuredClone(start);
  const server = fakeRoutes((request: RoutedRequest) => {
    if (request.url.startsWith(DEVICE)) {
      if (anki.device === undefined) return new TypeError('Failed to fetch');
      return json(anki.device.answer(request.body));
    }
    if (request.path === 'Settings' && request.method === 'PUT') {
      const change = request.body as SettingsChange;
      const { ServerKey, ...rest } = change.Anki ?? {};
      stored = {
        ...stored,
        Anki: {
          ...stored.Anki,
          ...rest,
          ...(ServerKey === undefined ? {} : { ServerKeySet: ServerKey !== '' }),
        },
        ...(change.Page === undefined ? {} : { Page: change.Page }),
        ...(change.Stream === undefined ? {} : { Stream: change.Stream }),
      };
      return json(stored);
    }
    if (request.path === 'Lines' && request.method === 'DELETE') {
      if (lines < 0) return problem(500, 'The stream could not be emptied.');
      lines = 0;
      return new Response(null, { status: 204 });
    }
    if (request.path === 'GameSettings') {
      return new Response('server = https://vn.example/base\nkey = the-key\n');
    }
    if (request.path === 'Dictionaries') {
      return json({ Dictionaries: [], Import: { State: 'idle', Progress: 0 }, MayManage: true });
    }
    if (request.path === 'Anki') {
      if (anki.server === undefined) {
        return problem(502, 'The AnkiConnect at anki.example.org could not be reached.');
      }
      const call = request.body as { Action: string; Params?: unknown };
      return json(anki.server.answer({ action: call.Action, version: 6, params: call.Params }));
    }
    return undefined;
  });
  return {
    ...server,
    stored: () => stored,
    lines: () => lines,
    /** Makes emptying the stream fail. */
    breakLines: () => (lines = -1),
    puts: () => server.sent.filter((sent) => sent.method === 'PUT'),
  };
}

let screen: VueWrapper | undefined;

async function open(server: ReturnType<typeof backend>, settings: SettingsView) {
  screen = mount(SettingsScreen, {
    props: {
      api: createServerApi(server.fetch, BASE),
      fetch: server.fetch,
      base: BASE,
      settings,
    },
    attachTo: document.body,
  });
  await flushPromises();
  return screen;
}

const find = (wrapper: VueWrapper, id: string) => wrapper.get(`[data-testid="${id}"]`);

describe('the settings', () => {
  afterEach(() => {
    screen?.unmount();
    screen = undefined;
  });

  it('connects to an AnkiConnect on a server, guesses the fields and saves the choices', async () => {
    const anki = createCollection();
    const start = view();
    const server = backend(start, { server: anki });
    const page = await open(server, start);

    await find(page, 'where-server').setValue(true);
    await find(page, 'server-address').setValue('https://anki.example.org');
    await find(page, 'server-key').setValue('  server-key ');
    await find(page, 'connect').trigger('click');
    await flushPromises();

    // The key goes to the server once and is not kept on the page.
    expect(server.puts()[0]!.body).toEqual({
      Anki: {
        Where: 'server',
        DeviceAddress: DEVICE,
        DeviceKey: '',
        ServerAddress: 'https://anki.example.org',
        ServerKey: 'server-key',
      },
    });
    expect((find(page, 'server-key').element as HTMLInputElement).value).toBe('');
    expect(find(page, 'server-key').attributes('placeholder')).toContain('Stored on the server');
    expect(find(page, 'connected').text()).toBe('Connected: 2 decks, 2 note types.');
    expect(anki.calls.map((call) => call.action)).toEqual([
      'version',
      'deckNames',
      'modelNames',
      'modelFieldNames',
      'findNotes',
    ]);

    // A new user with a Lapis note type has it chosen, and the one deck made, since no card says.
    expect(find(page, 'lapis-found').text()).toContain('"Lapis" is a Lapis note type');
    expect((find(page, 'note-type').element as HTMLSelectElement).value).toBe('Lapis');
    expect((find(page, 'deck').element as HTMLSelectElement).value).toBe('Mining');
    const role = (field: string) =>
      (find(page, `role-${field}`).element as HTMLSelectElement).value;
    expect(page.findAll('[data-testid^="role-"]')).toHaveLength(LAPIS_FIELDS.length);
    expect([role('Sentence'), role('SentenceAudio'), role('Picture'), role('MiscInfo')]).toEqual([
      'sentence',
      'sound',
      'picture',
      'source',
    ]);
    // The word's parts too, for cards made from the dictionary popup.
    expect([role('Expression'), role('MainDefinition')]).toEqual(['word', 'meaning']);

    await find(page, 'role-SentenceFurigana').setValue('source');
    await find(page, 'role-Expression').setValue('none');
    await find(page, 'deck').setValue('Mining');
    await find(page, 'tags').setValue('mined  anime mined');
    await find(page, 'newest-within').setValue('');
    await find(page, 'picture-format').setValue('webp');
    await find(page, 'save').trigger('submit');
    await flushPromises();

    const saved = server.puts().at(-1)!.body as SettingsChange;
    expect(saved.Anki).not.toHaveProperty('ServerKey');
    expect(saved.Page).toMatchObject({
      anki: {
        deck: 'Mining',
        noteType: 'Lapis',
        tags: ['mined', 'anime'],
        // What cannot be read takes the default, and a number too large the largest allowed.
        newestWithinMinutes: 15,
      },
      media: { pictureFormat: 'webp', soundFormat: 'mp3', normalize: false },
    });
    expect((saved.Page as { anki: { fields: Record<string, string> } }).anki.fields).toMatchObject({
      Sentence: 'sentence',
      SentenceFurigana: 'source',
      Expression: 'none',
    });
    expect(find(page, 'settings-message').text()).toBe('Saved.');
    expect(page.emitted('saved')?.at(-1)).toEqual([server.stored()]);
  });

  it('keeps the fields as chosen when the note type is the same, and guesses new ones', async () => {
    // MiscInfo was added to the note type since the settings were saved.
    const fields = Object.fromEntries(
      LAPIS_FIELDS.filter((name) => name !== 'MiscInfo').map((name) => [name, 'none']),
    );
    const start = view(
      { Where: 'server', ServerAddress: 'https://anki.example.org', ServerKeySet: true },
      { Page: { anki: { noteType: 'Lapis', fields: { ...fields, Expression: 'sentence' } } } },
    );
    const server = backend(start, { server: createCollection() });
    const page = await open(server, start);

    // Opening the settings connects at once.
    expect(find(page, 'connected').text()).toContain('Connected');
    expect((find(page, 'role-Expression').element as HTMLSelectElement).value).toBe('sentence');
    expect((find(page, 'role-Sentence').element as HTMLSelectElement).value).toBe('none');
    expect((find(page, 'role-MiscInfo').element as HTMLSelectElement).value).toBe('source');
  });

  it('chooses a Lapis note type for a new user, with the deck its newest card is in', async () => {
    const anki = createCollection();
    // Lapis copied under a name of the user's own, with a field added; a note type that is
    // called Lapis but is not; and decks of the user's own.
    delete anki.models.Lapis;
    anki.models['Basic (and reversed card)'] = ['Front', 'Back'];
    anki.models['Lapis notes'] = ['Expression', 'Sentence'];
    anki.models['Japanese mining'] = [...LAPIS_FIELDS, 'Notes'];
    anki.decks.push('Japanese::Words', 'Japanese::Sentences');
    anki.makeNote({ id: 1, deck: 'Japanese::Words', modelName: 'Japanese mining', fields: {} });
    anki.makeNote({ id: 2, deck: 'Japanese::Sentences', modelName: 'Japanese mining', fields: {} });
    anki.makeNote({ id: 3, deck: 'Default', modelName: 'Basic', fields: {} });
    const start = view({ Where: 'server', ServerAddress: 'https://anki.example.org' });
    const server = backend(start, { server: anki });
    const page = await open(server, start);

    const value = (id: string) => (find(page, id).element as HTMLSelectElement).value;
    expect(value('note-type')).toBe('Japanese mining');
    expect(value('deck')).toBe('Japanese::Sentences');
    expect(find(page, 'lapis-found').text()).toContain('"Japanese mining" is a Lapis note type');
    expect(
      Object.fromEntries(
        ['Expression', 'SelectionText', 'PitchCategories', 'FreqSort', 'Glossary', 'Notes'].map(
          (field) => [field, value(`role-${field}`)],
        ),
      ),
    ).toEqual({
      Expression: 'word',
      SelectionText: 'selectionText',
      PitchCategories: 'pitchCategories',
      FreqSort: 'frequency',
      Glossary: 'none',
      Notes: 'none',
    });
    // The name that only sounded like Lapis was looked into first, and nothing else after.
    expect(
      anki.calls.filter((call) => call.action === 'modelFieldNames').map((call) => call.params),
    ).toEqual([
      { modelName: 'Lapis notes' },
      { modelName: 'Basic' },
      { modelName: 'Basic (and reversed card)' },
      { modelName: 'Japanese mining' },
    ]);

    // Nothing is kept until the settings are saved.
    expect(server.stored().Page).toBeUndefined();
    await find(page, 'save').trigger('submit');
    await flushPromises();
    expect(server.stored().Page).toMatchObject({
      anki: { noteType: 'Japanese mining', deck: 'Japanese::Sentences' },
    });

    // A user who chooses another note type has the note no longer said to be chosen for them.
    await find(page, 'note-type').setValue('Basic');
    await flushPromises();
    expect(page.find('[data-testid="lapis-found"]').exists()).toBe(false);
  });

  it('chooses nothing for a new user whose Anki has no Lapis note type', async () => {
    const anki = createCollection();
    delete anki.models.Lapis;
    anki.decks.push('Words');
    const start = view({ Where: 'server', ServerAddress: 'https://anki.example.org' });
    const page = await open(backend(start, { server: anki }), start);

    expect((find(page, 'note-type').element as HTMLSelectElement).value).toBe('');
    expect((find(page, 'deck').element as HTMLSelectElement).value).toBe('');
    expect(page.find('[data-testid="lapis-found"]').exists()).toBe(false);
    expect(page.find('[data-testid="fields"]').exists()).toBe(false);
  });

  it('restores the defaults, with Lapis chosen, and keeps where the cards go', async () => {
    const anki = createCollection();
    anki.makeNote({ id: 1, deck: 'Default', modelName: 'Lapis', fields: {} });
    const start = view(
      { Where: 'server', ServerAddress: 'https://anki.example.org', ServerKeySet: true },
      {
        Page: {
          anki: {
            deck: 'Mining',
            noteType: 'Basic',
            fields: { Front: 'sentence', Back: 'picture' },
            tags: ['anime'],
            newestWithinMinutes: 60,
          },
          media: { pictureFormat: 'avif', normalize: true },
          dictionary: { lookUp: false },
        },
      },
    );
    const server = backend(start, { server: anki });
    const page = await open(server, start);
    expect((find(page, 'note-type').element as HTMLSelectElement).value).toBe('Basic');

    await find(page, 'restore-defaults').trigger('click');
    await flushPromises();
    expect(find(page, 'settings-message').text()).toBe(
      'Defaults restored. Where your cards go is kept. Save to keep the rest.',
    );
    expect(find(page, 'lapis-found').text()).toContain('"Lapis" is a Lapis note type');
    await find(page, 'save').trigger('submit');
    await flushPromises();

    const defaults = defaultPageSettings();
    expect(server.stored().Page).toEqual({
      ...defaults,
      anki: {
        ...defaults.anki,
        // The deck its newest card is in.
        deck: 'Default',
        noteType: 'Lapis',
        fields: guessRoles(LAPIS_FIELDS),
      },
    });
    expect(server.stored().Anki).toMatchObject({
      Where: 'server',
      ServerAddress: 'https://anki.example.org',
      ServerKeySet: true,
    });
  });

  it('says what went wrong with the server, and lets the stored key be removed', async () => {
    const start = view({
      Where: 'server',
      ServerAddress: 'https://anki.example.org',
      ServerKeySet: true,
    });
    const server = backend(start);
    const page = await open(server, start);
    expect(find(page, 'connect-error').text()).toContain('could not be reached');

    await page
      .findAll('button')
      .find((button) => button.text() === 'Remove the stored key')!
      .trigger('click');
    await flushPromises();
    expect(server.puts().at(-1)!.body).toEqual({ Anki: { ServerKey: '' } });
    expect(page.text()).not.toContain('Remove the stored key');
  });

  it('calls an Anki on the device from the browser, and only on the device', async () => {
    const device = createCollection({ key: 'device-key' });
    const start = view();
    const server = backend(start, { device });
    const page = await open(server, start);

    await find(page, 'where-device').setValue(true);
    expect(page.text()).toContain('"webCorsOriginList": ["https://vn.example"]');

    for (const elsewhere of ['http://192.168.1.20:8765', 'http://[::1]:8765', 'anki']) {
      await find(page, 'device-address').setValue(elsewhere);
      expect(page.get('[role="alert"]').text(), elsewhere).toContain(
        'can only reach an Anki on this device',
      );
      expect(find(page, 'connect').attributes('disabled'), elsewhere).toBeDefined();
    }

    await find(page, 'device-address').setValue(DEVICE);
    await find(page, 'connect').trigger('click');
    await flushPromises();
    expect(find(page, 'connect-error').text()).toBe(
      'The AnkiConnect on this device wants a key. Enter it in the settings.',
    );

    await find(page, 'device-key').setValue('device-key');
    await find(page, 'connect').trigger('click');
    await flushPromises();
    expect(find(page, 'connected').text()).toContain('2 decks');
    // The browser called the device; the server only kept the settings.
    expect(server.sent.filter((sent) => sent.path === 'Anki')).toHaveLength(0);
    expect(server.stored().Anki).toMatchObject({ Where: 'device', DeviceKey: 'device-key' });
  });

  it('sets aside the decks of one Anki when another is chosen', async () => {
    const device = createCollection();
    device.decks.splice(0, device.decks.length, 'Device deck');
    const start = view({ Where: 'server', ServerAddress: 'https://anki.example.org' });
    const server = backend(start, { server: createCollection(), device });
    const page = await open(server, start);
    expect(find(page, 'connected').text()).toBe('Connected: 2 decks, 2 note types.');

    await find(page, 'where-device').setValue(true);
    expect(page.find('[data-testid="connected"]').exists()).toBe(false);
    expect(page.find('[data-testid="deck"]').exists()).toBe(false);

    await find(page, 'connect').trigger('click');
    await flushPromises();
    expect(find(page, 'connected').text()).toBe('Connected: 1 deck, 2 note types.');
    expect(
      find(page, 'deck')
        .findAll('option')
        .map((option) => option.text()),
    ).toContain('Device deck');
  });

  it('saves how many lines the server keeps, held to what the server takes', async () => {
    const start = view();
    const server = backend(start);
    const page = await open(server, start);
    expect((find(page, 'max-lines').element as HTMLInputElement).value).toBe('1000');

    await find(page, 'max-lines').setValue('250');
    await find(page, 'save').trigger('submit');
    await flushPromises();
    expect((server.puts().at(-1)!.body as SettingsChange).Stream).toEqual({ MaxLines: 250 });
    expect(page.emitted('saved')?.at(-1)).toEqual([server.stored()]);

    // Too few, too many or nothing at all: the nearest number the server takes.
    for (const [typed, kept] of [
      ['7', 50],
      ['999999', 20_000],
      ['', 50],
    ] as const) {
      await find(page, 'max-lines').setValue(typed);
      await find(page, 'save').trigger('submit');
      await flushPromises();
      expect((server.puts().at(-1)!.body as SettingsChange).Stream, typed).toEqual({
        MaxLines: kept,
      });
      expect((find(page, 'max-lines').element as HTMLInputElement).value).toBe(String(kept));
    }
  });

  it('empties the stream only when asked twice', async () => {
    const start = view();
    const server = backend(start);
    const page = await open(server, start);

    await find(page, 'empty-stream').trigger('click');
    expect(page.text()).toContain('Remove every line, with its voice and its screenshot?');
    expect(server.lines()).toBe(12);

    // Thought better of it.
    await page
      .findAll('button')
      .find((button) => button.text() === 'Keep them')!
      .trigger('click');
    expect(server.lines()).toBe(12);
    expect(page.emitted('cleared')).toBeUndefined();

    await find(page, 'empty-stream').trigger('click');
    await find(page, 'empty-stream-yes').trigger('click');
    await flushPromises();
    expect(server.lines()).toBe(0);
    expect(find(page, 'emptied').text()).toBe('The stream is empty.');
    expect(page.emitted('cleared')).toHaveLength(1);

    // What the server says when it cannot.
    server.breakLines();
    await find(page, 'empty-stream').trigger('click');
    await find(page, 'empty-stream-yes').trigger('click');
    await flushPromises();
    expect(page.text()).toContain('The stream could not be emptied.');
    expect(page.emitted('cleared')).toHaveLength(1);
  });

  it('hands out the file for the game, and shows its text only when asked', async () => {
    const start = view();
    const server = backend(start);
    const page = await open(server, start);

    const link = find(page, 'game-file');
    expect(link.attributes('href')).toBe('https://vn.example/base/Api/GameSettings');
    expect(link.attributes('download')).toBe('mining.txt');
    // The file holds the key: nothing asks for it until the user does.
    expect(server.sent.some((sent) => sent.path === 'GameSettings')).toBe(false);
    expect(page.find('[data-testid="game-file-text"]').exists()).toBe(false);

    await find(page, 'show-game-file').trigger('click');
    await flushPromises();
    expect(find(page, 'game-file-text').text()).toBe(
      'server = https://vn.example/base\nkey = the-key',
    );

    await find(page, 'show-game-file').trigger('click');
    expect(page.find('[data-testid="game-file-text"]').exists()).toBe(false);
  });
});
