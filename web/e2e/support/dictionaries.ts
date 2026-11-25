import { once } from 'node:events';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

import type { APIRequestContext } from '@playwright/test';

import { expect } from './fixtures';
import { term, type TestDictionary, yomitanZip } from './yomitanZip';

/** The server's list of dictionaries, as the API gives it. */
export interface DictionaryList {
  Dictionaries: { Id: string; Title: string; Enabled: boolean; Terms: number }[];
  Import: {
    State: string;
    Source?: string;
    Progress: number;
    Title?: string;
    Error?: string;
    Waiting: number;
    Finished: { Number: number; Source: string; Title?: string; Error?: string }[];
  };
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

/** Waits until the import that runs, and those that wait behind it, have ended. */
export async function importEnded(api: APIRequestContext): Promise<DictionaryList> {
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
  return list;
}

/** Sends a zip to be imported, without waiting for the import. */
export async function sendDictionary(
  api: APIRequestContext,
  name: string,
  zip: Buffer,
): Promise<DictionaryList> {
  const response = await api.post(`/Api/Dictionaries?name=${encodeURIComponent(name)}`, {
    headers: { 'Content-Type': 'application/zip' },
    data: zip,
  });
  expect(response.status(), await response.text()).toBe(202);
  return (await response.json()) as DictionaryList;
}

/** Uploads a dictionary and waits for its import. */
export async function uploadDictionary(
  api: APIRequestContext,
  dictionary: TestDictionary,
): Promise<DictionaryList> {
  await sendDictionary(api, `${dictionary.title}.zip`, yomitanZip(dictionary));
  const list = await importEnded(api);
  expect(list.Import, JSON.stringify(list.Import)).toMatchObject({ State: 'done' });
  return list;
}

/**
 * A web server on 127.0.0.1 that hands out files, as a dictionary's publisher does, once `until`
 * has come true: a download that takes as long as a test wants.
 */
export async function serveFiles(
  files: Record<string, Buffer>,
  until: Promise<unknown> = Promise.resolve(),
): Promise<{ url: string; stop: () => Promise<void> }> {
  const server = createServer((request, response) => {
    const file = files[request.url ?? ''];
    void until.then(() => {
      if (file === undefined) {
        response.writeHead(404).end();
        return;
      }
      response.writeHead(200, { 'Content-Type': 'application/zip', 'Content-Length': file.length });
      response.end(file);
    });
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    stop: () =>
      new Promise((resolve) => {
        server.close(() => resolve());
        // A download the server gave up on is not waited for.
        server.closeAllConnections();
      }),
  };
}
