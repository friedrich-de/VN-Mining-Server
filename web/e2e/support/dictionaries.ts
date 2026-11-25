import type { APIRequestContext } from '@playwright/test';

import { expect } from './fixtures';
import { term, type TestDictionary, yomitanZip } from './yomitanZip';

/** The server's list of dictionaries, as the API gives it. */
export interface DictionaryList {
  Dictionaries: { Id: string; Title: string; Enabled: boolean; Terms: number }[];
  Import: { State: string; Source?: string; Progress: number; Title?: string; Error?: string };
  MayManage: boolean;
}

/** A small dictionary of words in the tests' lines, with a picture and a stylesheet. */
export function wordsDictionary(title = 'E2E Words'): TestDictionary {
  return {
    title,
    terms: [
      term(
        '食べる',
        'たべる',
        'v1',
        [
          {
            type: 'structured-content',
            content: [{ tag: 'span', data: { content: 'gloss' }, content: 'to eat' }],
          },
        ],
        'v1 vt',
        10,
      ),
      term('昨日', 'きのう', '', ['yesterday'], 'n'),
      term(
        '話',
        'はなし',
        '',
        [
          {
            type: 'structured-content',
            content: [
              { tag: 'span', data: { content: 'gloss' }, content: 'talk; story' },
              { tag: 'img', path: 'img/bowl.png', width: 2, height: 2, sizeUnits: 'em' },
            ],
          },
        ],
        'n',
      ),
      term('約束', 'やくそく', '', ['promise'], 'n vs'),
    ],
    metas: [
      ['約束', 'freq', { value: 812, displayValue: '812' }],
      ['約束', 'pitch', { reading: 'やくそく', pitches: [{ position: 0 }] }],
    ],
    tags: [['v1', 'partOfSpeech', 0, 'Ichidan verb', 0]],
    styles: '[data-sc-content="gloss"] { font-weight: bold; }',
    frequencyMode: 'rank-based',
    files: {
      // A 1×1 PNG.
      'img/bowl.png': Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        'base64',
      ),
    },
  };
}

export async function listDictionaries(api: APIRequestContext): Promise<DictionaryList> {
  const response = await api.get('/Api/Dictionaries');
  expect(response.status(), await response.text()).toBe(200);
  return (await response.json()) as DictionaryList;
}

/** Uploads a dictionary and waits for its import. */
export async function uploadDictionary(
  api: APIRequestContext,
  dictionary: TestDictionary,
): Promise<DictionaryList> {
  const response = await api.post(
    `/Api/Dictionaries?name=${encodeURIComponent(dictionary.title)}.zip`,
    { headers: { 'Content-Type': 'application/zip' }, data: yomitanZip(dictionary) },
  );
  expect(response.status(), await response.text()).toBe(202);
  let list = await listDictionaries(api);
  await expect
    .poll(
      async () => {
        list = await listDictionaries(api);
        return list.Import.State;
      },
      { timeout: 30_000 },
    )
    .toMatch(/^(done|failed|idle)$/);
  expect(list.Import, JSON.stringify(list.Import)).toMatchObject({ State: 'done' });
  return list;
}
