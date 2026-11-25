import { describe, expect, it } from 'vitest';

import { HttpError } from '@/api/http';
import type { ServerApi } from '@/api/server';
import { createCollection } from '@/testing/ankiCollection';
import { json } from '@/testing/fakes';

import {
  deviceAnki,
  markOf,
  markOfBytes,
  mediaName,
  serverAnki,
  WordAudioMissingError,
} from './connect';

const CUT = {
  kind: 'picture' as const,
  options: { line: 12, format: 'jpeg' as const, width: 640 },
};

const bytes = (text: string): Uint8Array => new TextEncoder().encode(text);

const WORD = { kind: 'word-audio' as const, term: '食べる', reading: 'たべる' };

function cutsApi(): Pick<ServerApi, 'picture' | 'sound' | 'wordAudio'> {
  return {
    picture: () => Promise.resolve(new Blob(['picture'], { type: 'image/jpeg' })),
    sound: () => Promise.resolve(new Blob(['sound'], { type: 'audio/mpeg' })),
    wordAudio: (term, _reading, _signal, choice) =>
      term === '食べる'
        ? Promise.resolve(new Blob([choice ?? 'word'], { type: 'audio/mpeg' }))
        : Promise.reject(new HttpError(404, 'The audio source has no sound for this word.')),
  };
}

describe('the Anki on the device', () => {
  it('is called by the browser in version 6, with its key', async () => {
    const collection = createCollection({ key: 'device-key' });
    const bodies: unknown[] = [];
    const fetchFn: typeof fetch = (_url, init) => {
      const body = JSON.parse(init?.body as string) as unknown;
      bodies.push(body);
      return Promise.resolve(json(collection.answer(body)));
    };
    const anki = deviceAnki(fetchFn, 'http://127.0.0.1:8765', 'device-key', cutsApi());

    expect(await anki.invoke('deckNames')).toEqual(['Default', 'Mining']);
    expect(bodies[0]).toEqual({ action: 'deckNames', version: 6, key: 'device-key' });

    // The page has the server make the picture and hands it over as base64.
    const name = await anki.store(CUT);
    expect(name).toBe(mediaName(CUT, 'image/jpeg', bytes('picture')));
    expect(name).toMatch(/^vn-miner_12_[0-9a-f]{8}\.jpg$/);
    expect(atob(collection.media.get(name)!)).toBe('picture');

    const wrong = deviceAnki(fetchFn, 'http://127.0.0.1:8765', 'other', cutsApi());
    await expect(wrong.invoke('version')).rejects.toThrow('wants a key');
  });

  it('says so when Anki is not open or does not let the page in', async () => {
    const anki = deviceAnki(
      () => Promise.reject(new TypeError('Failed to fetch')),
      'http://127.0.0.1:8765',
      '',
      cutsApi(),
    );

    await expect(anki.invoke('version')).rejects.toMatchObject({
      unreachable: true,
      message: expect.stringContaining('Is Anki open') as string,
    });

    const notAnki = deviceAnki(
      () => Promise.resolve(new Response('<html>')),
      'http://127.0.0.1:8765',
      '',
      cutsApi(),
    );
    await expect(notAnki.invoke('version')).rejects.toThrow('is not AnkiConnect');
  });

  it('puts how a word sounds into Anki under a name of the word', async () => {
    const collection = createCollection();
    const fetchFn: typeof fetch = (_url, init) =>
      Promise.resolve(json(collection.answer(JSON.parse(init?.body as string) as unknown)));
    const anki = deviceAnki(fetchFn, 'http://127.0.0.1:8765', '', cutsApi());

    const name = await anki.store(WORD);

    expect(name).toBe(mediaName(WORD, 'audio/mpeg', bytes('word')));
    expect(name).toMatch(/^vn-miner_word_[0-9a-f]{8}\.mp3$/);
    expect(mediaName({ ...WORD, term: ' 食べる ' }, 'audio/mpeg', bytes('word'))).toBe(name);
    expect(mediaName({ ...WORD, reading: '' }, 'audio/mpeg', bytes('word'))).not.toBe(name);
    expect(atob(collection.media.get(name)!)).toBe('word');

    // The sound chosen in the popup goes in under a name of its own.
    const chosen = await anki.store({ ...WORD, choice: 'jpod' });
    expect(chosen).not.toBe(name);
    expect(atob(collection.media.get(chosen)!)).toBe('jpod');
    expect(mediaName({ ...WORD, choice: '' }, 'audio/mpeg', bytes('word'))).toBe(name);
    collection.media.delete(chosen);

    // A word the source has no sound for is told apart from Anki failing.
    await expect(anki.store({ ...WORD, term: '猫' })).rejects.toBeInstanceOf(WordAudioMissingError);
    expect(collection.media.size).toBe(1);
  });

  it('names a file by its line and by what is in it', () => {
    expect(markOf('a')).toBe(markOf('a'));
    expect(markOf('a')).not.toBe(markOf('b'));
    expect(markOfBytes(bytes('a'))).toBe(markOf('a'));

    // The voices of several lines are named by the first of them.
    const sound = { kind: 'sound' as const, options: { lines: [7, 8, 9] } };
    const name = mediaName(sound, 'audio/ogg', bytes('voices'));
    expect(name).toMatch(/^vn-miner_7_[0-9a-f]{8}\.ogg$/);
    expect(mediaName(sound, 'audio/ogg', bytes('voices'))).toBe(name);
    // Another server's line 7, or the same line made anew in another way, replaces nothing.
    expect(mediaName(sound, 'audio/ogg', bytes('other voices'))).not.toBe(name);
  });
});

describe('the Anki on a server', () => {
  it('goes through the server, which answers in AnkiConnect words', async () => {
    const collection = createCollection();
    const calls: [string, unknown][] = [];
    const anki = serverAnki({
      anki: (action, params) => {
        calls.push([action, params]);
        return Promise.resolve(json(collection.answer({ action, params })));
      },
      pictureToAnki: () => Promise.resolve({ FileName: 'stored.jpg' }),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: (term, reading, choice) =>
        Promise.resolve({
          FileName: `${term}-${reading}${choice === undefined ? '' : `-${choice}`}.mp3`,
        }),
    });

    expect(await anki.invoke('findNotes', { query: 'added:1' })).toEqual([]);
    expect(calls).toEqual([['findNotes', { query: 'added:1' }]]);
    expect(await anki.store(CUT)).toBe('stored.jpg');
    expect(await anki.store(WORD)).toBe('食べる-たべる.mp3');
    expect(await anki.store({ ...WORD, choice: 'jpod' })).toBe('食べる-たべる-jpod.mp3');

    const refusing = serverAnki({
      anki: () => Promise.resolve(json({ result: null, error: 'valid api key must be provided' })),
      pictureToAnki: () => Promise.reject(new Error('unused')),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new Error('unused')),
    });
    await expect(refusing.invoke('version')).rejects.toThrow('did not take the key');
  });

  it('passes on what the server says when it cannot reach the AnkiConnect', async () => {
    const anki = serverAnki({
      anki: () =>
        Promise.reject(
          new HttpError(
            502,
            'The AnkiConnect at anki.example.org could not be reached from this server.',
          ),
        ),
      pictureToAnki: () =>
        Promise.reject(
          new HttpError(409, 'Set the address of the AnkiConnect on your server in the settings.'),
        ),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new Error('unused')),
    });

    await expect(anki.invoke('version')).rejects.toMatchObject({ unreachable: true });
    await expect(anki.store(CUT)).rejects.toThrow('Set the address');
  });

  it('tells a word without a sound apart from an Anki it cannot reach', async () => {
    const answer = { status: 404, text: 'The audio source has no sound for this word.' };
    const anki = serverAnki({
      anki: () => Promise.reject(new Error('unused')),
      pictureToAnki: () => Promise.reject(new Error('unused')),
      soundToAnki: () => Promise.reject(new Error('unused')),
      wordAudioToAnki: () => Promise.reject(new HttpError(answer.status, answer.text)),
    });

    await expect(anki.store(WORD)).rejects.toBeInstanceOf(WordAudioMissingError);
    Object.assign(answer, { status: 502, text: 'The audio source could not be reached.' });
    await expect(anki.store(WORD)).rejects.toBeInstanceOf(WordAudioMissingError);
    Object.assign(answer, { status: 409, text: 'Set the address of the AnkiConnect.' });
    await expect(anki.store(WORD)).rejects.toThrow('Set the address');
    await expect(anki.store(WORD)).rejects.not.toBeInstanceOf(WordAudioMissingError);
  });
});
