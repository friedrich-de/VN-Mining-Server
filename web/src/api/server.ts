import { apiUrl } from './addresses';
import { type Fetch, getJson, send } from './http';
import type {
  DictionariesView,
  LinesView,
  Lookup,
  SettingsChange,
  SettingsView,
  Status,
  StoredMedia,
  WordAudioSaved,
  WordAudioSourcesView,
} from './types';

export type PictureFormat = 'jpeg' | 'webp' | 'avif';
export type SoundFormat = 'mp3' | 'opus';

/** A card's picture: a line's screenshot, in the format, size and quality asked for. */
export interface PictureOptions {
  /** The line whose screenshot it is. */
  line: number;
  format?: PictureFormat;
  width?: number;
  quality?: number;
}

/** A card's sound: the voices of one or several lines, one after the other. */
export interface SoundOptions {
  /** The lines whose voices it holds. */
  lines: number[];
  format?: SoundFormat;
  bitrate?: number;
  normalize?: boolean;
}

/** Which part of the stream to read: the newest lines when neither end is given. */
export interface LinesQuery {
  after?: number;
  before?: number;
  limit?: number;
}

/** What the page asks the server for. The browser's sign-in cookie goes with every call. */
export interface ServerApi {
  /** Says whether the page is signed in, and what it is talking to. */
  status(): Promise<Status>;
  /** Signs the browser in with the server's API key. */
  signIn(key: string): Promise<void>;
  signOut(): Promise<void>;
  /** Reads a part of the stream. */
  lines(query?: LinesQuery, signal?: AbortSignal): Promise<LinesView>;
  /** Empties the stream. */
  clearLines(): Promise<void>;
  /** Opens the stream of news, from the line after a number. The caller reads and closes it. */
  events(after: number, signal: AbortSignal): Promise<Response>;
  /** Where a line's screenshot is, as the game sent it, for an img. */
  screenshotUrl(line: number): string;
  /** Where the file is that tells a game how to reach this server. */
  gameSettingsUrl(): string;
  /** That file's text. It holds the API key. */
  gameSettings(): Promise<string>;
  picture(options: PictureOptions, signal?: AbortSignal): Promise<Blob>;
  sound(options: SoundOptions, signal?: AbortSignal): Promise<Blob>;
  settings(): Promise<SettingsView>;
  saveSettings(change: SettingsChange): Promise<SettingsView>;
  /** One call to the AnkiConnect on a server. The answer is AnkiConnect's own. */
  anki(action: string, params?: unknown): Promise<Response>;
  /** Makes a picture and puts it into the Anki on a server. */
  pictureToAnki(options: PictureOptions): Promise<StoredMedia>;
  /** Makes a sound and puts it into the Anki on a server. */
  soundToAnki(options: SoundOptions): Promise<StoredMedia>;
  /** The server's dictionaries, and the import that runs. */
  dictionaries(): Promise<DictionariesView>;
  /** The words a text starts with, in the server's dictionaries. */
  lookUp(text: string, signal?: AbortSignal): Promise<Lookup>;
  /** A picture of a dictionary's definitions. */
  dictionaryPicture(id: string, path: string): Promise<Blob>;
  /** Imports a dictionary's zip. */
  uploadDictionary(file: Blob, name: string): Promise<DictionariesView>;
  /** The server downloads a dictionary's zip and imports it. */
  downloadDictionary(url: string): Promise<DictionariesView>;
  stopDictionaryImport(): Promise<DictionariesView>;
  /** Every dictionary's id once, the first first. */
  arrangeDictionaries(order: string[], disabled: string[]): Promise<DictionariesView>;
  removeDictionary(id: string): Promise<DictionariesView>;
  /**
   * How a word sounds, from the audio source set as in Yomitan: the sound of that name, or the
   * first that works when no name is given.
   */
  wordAudio(term: string, reading: string, signal?: AbortSignal, choice?: string): Promise<Blob>;
  /** The names of the sounds the audio source has for a word, to choose one as in Yomitan. */
  wordAudioSources(term: string, reading: string, signal?: AbortSignal): Promise<string[]>;
  /** Puts how a word sounds into the Anki on a server: the sound of that name, if given. */
  wordAudioToAnki(term: string, reading: string, choice?: string): Promise<StoredMedia>;
  /** Sets the audio source for words, or takes it away with "". */
  saveWordAudio(address: string): Promise<WordAudioSaved>;
}

function query(values: Record<string, string | number | boolean | undefined>): string {
  const pairs = Object.entries(values).filter(
    (pair): pair is [string, string | number | boolean] => pair[1] !== undefined,
  );
  return new URLSearchParams(pairs.map(([name, value]) => [name, String(value)])).toString();
}

export function createServerApi(fetchFn: Fetch, base: string): ServerApi {
  const dictionaries = (path: string): string => apiUrl(base, `Dictionaries${path}`);

  async function sendJson<T>(method: string, url: string, body?: unknown): Promise<T> {
    const response = await send(fetchFn, url, {
      method,
      headers: {
        Accept: 'application/json',
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    return (await response.json()) as T;
  }

  const pictureQuery = (options: PictureOptions): string =>
    query({ format: options.format, width: options.width, quality: options.quality });

  const soundQuery = (options: SoundOptions): string =>
    query({
      lines: options.lines.join(','),
      format: options.format,
      bitrate: options.bitrate,
      normalize: options.normalize,
    });

  async function file(url: string, signal: AbortSignal | undefined): Promise<Blob> {
    const response = await send(fetchFn, url, signal === undefined ? {} : { signal });
    return response.blob();
  }

  return {
    status: () => getJson<Status>(fetchFn, apiUrl(base, 'Status')),

    signIn: async (key) => {
      await send(fetchFn, apiUrl(base, 'Session'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ Key: key }),
      });
    },

    signOut: async () => {
      await send(fetchFn, apiUrl(base, 'Session'), { method: 'DELETE' });
    },

    lines: (which = {}, signal) =>
      getJson<LinesView>(fetchFn, apiUrl(base, `Lines?${query({ ...which })}`), signal),

    clearLines: async () => {
      await send(fetchFn, apiUrl(base, 'Lines'), { method: 'DELETE' });
    },

    events: (after, signal) =>
      fetchFn(apiUrl(base, `Events?${query({ after })}`), {
        headers: { Accept: 'text/event-stream' },
        cache: 'no-store',
        signal,
      }),

    screenshotUrl: (line) => apiUrl(base, `Lines/${line}/Screenshot`),

    gameSettingsUrl: () => apiUrl(base, 'GameSettings'),

    gameSettings: async () => {
      const response = await send(fetchFn, apiUrl(base, 'GameSettings'), { cache: 'no-store' });
      return response.text();
    },

    picture: (options, signal) =>
      file(apiUrl(base, `Lines/${options.line}/Picture?${pictureQuery(options)}`), signal),

    sound: (options, signal) => file(apiUrl(base, `Lines/Audio?${soundQuery(options)}`), signal),

    settings: () => getJson<SettingsView>(fetchFn, apiUrl(base, 'Settings')),

    saveSettings: (change) => sendJson<SettingsView>('PUT', apiUrl(base, 'Settings'), change),

    anki: (action, params) =>
      send(fetchFn, apiUrl(base, 'Anki'), {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify(
          params === undefined ? { Action: action } : { Action: action, Params: params },
        ),
      }),

    pictureToAnki: (options) =>
      sendJson<StoredMedia>(
        'POST',
        apiUrl(base, `Lines/${options.line}/Picture/Anki?${pictureQuery(options)}`),
      ),

    soundToAnki: (options) =>
      sendJson<StoredMedia>('POST', apiUrl(base, `Lines/Audio/Anki?${soundQuery(options)}`)),

    dictionaries: () => getJson<DictionariesView>(fetchFn, dictionaries('')),

    lookUp: (text, signal) =>
      getJson<Lookup>(fetchFn, dictionaries(`/Lookup?${query({ text })}`), signal),

    dictionaryPicture: (id, path) =>
      file(dictionaries(`/${encodeURIComponent(id)}/Media?${query({ path })}`), undefined),

    uploadDictionary: async (zip, name) => {
      const response = await send(fetchFn, dictionaries(`?${query({ name })}`), {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/zip' },
        body: zip,
      });
      return (await response.json()) as DictionariesView;
    },

    downloadDictionary: (url) =>
      sendJson<DictionariesView>('POST', dictionaries('/Download'), { Url: url }),

    stopDictionaryImport: () => sendJson<DictionariesView>('DELETE', dictionaries('/Import')),

    arrangeDictionaries: (order, disabled) =>
      sendJson<DictionariesView>('PUT', dictionaries(''), { Order: order, Disabled: disabled }),

    removeDictionary: (id) =>
      sendJson<DictionariesView>('DELETE', dictionaries(`/${encodeURIComponent(id)}`)),

    wordAudio: (term, reading, signal, choice) =>
      file(apiUrl(base, `WordAudio?${query({ term, reading, source: choice })}`), signal),

    wordAudioSources: async (term, reading, signal) =>
      (
        await getJson<WordAudioSourcesView>(
          fetchFn,
          apiUrl(base, `WordAudio/Sources?${query({ term, reading })}`),
          signal,
        )
      ).Sources,

    wordAudioToAnki: (term, reading, choice) =>
      sendJson<StoredMedia>(
        'POST',
        apiUrl(base, `WordAudio/Anki?${query({ term, reading, source: choice })}`),
      ),

    saveWordAudio: (address) =>
      sendJson<WordAudioSaved>('PUT', apiUrl(base, 'WordAudio'), { Address: address }),
  };
}
