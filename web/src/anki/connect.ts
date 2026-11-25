import { type Fetch, HttpError, UnreachableError } from '@/api/http';
import type { PictureOptions, ServerApi, SoundOptions } from '@/api/server';
import type { AnkiWhere } from '@/api/types';

/** The version of AnkiConnect's interface the page speaks. */
export const ANKI_VERSION = 6;

/** Something went wrong on the way to Anki or in it, in words for the user. */
export class AnkiError extends Error {
  constructor(
    message: string,
    /** Whether Anki could not be reached at all, as opposed to saying no. */
    readonly unreachable = false,
  ) {
    super(message);
    this.name = 'AnkiError';
  }
}

/** The audio source for words has no sound for a word, or could not be reached. */
export class WordAudioMissingError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'WordAudioMissingError';
  }
}

/** A line's picture or the voices of lines for a card, or how a word sounds. */
export type MediaCut =
  | { kind: 'picture'; options: PictureOptions }
  | { kind: 'sound'; options: SoundOptions }
  | {
      kind: 'word-audio';
      term: string;
      reading: string;
      /** The name of the sound chosen in the popup; the first that works when left out. */
      choice?: string;
    };

/** The user's Anki, wherever its AnkiConnect is. */
export interface AnkiConnect {
  readonly where: Exclude<AnkiWhere, 'off'>;
  invoke<T>(action: string, params?: Record<string, unknown>): Promise<T>;
  /** Makes a picture or sound and puts it into Anki's media. Returns the name it is stored under. */
  store(cut: MediaCut): Promise<string>;
}

function explain(error: string, where: AnkiConnect['where']): string {
  if (/api key/i.test(error)) {
    return where === 'device'
      ? 'The AnkiConnect on this device wants a key. Enter it in the settings.'
      : 'The AnkiConnect on your server did not take the key. Check it in the settings.';
  }
  return `Anki said: ${error}`;
}

/** Reads AnkiConnect's answer: the result, or its error in words. */
async function resultOf<T>(response: Response, where: AnkiConnect['where']): Promise<T> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }
  if (body === null || typeof body !== 'object' || !('result' in body) || !('error' in body)) {
    throw new AnkiError(
      'What answers at this address is not AnkiConnect. Check it in the settings.',
    );
  }
  const { result, error } = body as { result: T; error: unknown };
  if (typeof error === 'string' && error.length > 0) throw new AnkiError(explain(error, where));
  return result;
}

/** A short mark of a text, so the same text gets the same name. FNV-1a, 32 bits. */
export function markOf(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16).padStart(8, '0');
}

/**
 * A short mark of a file's bytes, so the same file gets the same name and another file another.
 * FNV-1a, 32 bits: the browser's own hashes need a secure address, which a server at home may lack.
 */
export function markOfBytes(bytes: Uint8Array): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i += 1) {
    hash ^= bytes[i]!;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16).padStart(8, '0');
}

const EXTENSIONS: Readonly<Record<string, string>> = {
  'image/jpeg': 'jpg',
  'image/webp': 'webp',
  'image/avif': 'avif',
  'audio/mpeg': 'mp3',
  'audio/ogg': 'ogg',
  'audio/mp4': 'm4a',
  'audio/aac': 'aac',
  'audio/wav': 'wav',
  'audio/webm': 'webm',
  'audio/flac': 'flac',
};

/**
 * The name a file goes into Anki under when the page puts it there itself. A line's file is named
 * by the line's number and a mark of its bytes: another server's line of the same number, or the
 * same line made in another format, gets another name and replaces nothing.
 */
export function mediaName(cut: MediaCut, type: string, bytes: Uint8Array): string {
  const extension =
    EXTENSIONS[type.split(';')[0]!.trim()] ?? (type.startsWith('audio/') ? 'ogg' : 'bin');
  if (cut.kind === 'word-audio') {
    // The same word from the same sound gets the same name, so its sound is in Anki once.
    const choice = cut.choice === undefined || cut.choice === '' ? '' : `|${cut.choice}`;
    return `vn-miner_word_${markOf(`${cut.term.trim()}|${cut.reading.trim() || cut.term.trim()}${choice}`)}.${extension}`;
  }
  const line = cut.kind === 'picture' ? cut.options.line : (cut.options.lines[0] ?? 0);
  return `vn-miner_${line}_${markOfBytes(bytes)}.${extension}`;
}

function base64Of(bytes: Uint8Array): string {
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

/**
 * An AnkiConnect on the user's own device, which the browser calls itself. AnkiConnect lets the
 * page in only when the page's address is in its webCorsOriginList.
 */
export function deviceAnki(
  fetchFn: Fetch,
  address: string,
  key: string,
  api: Pick<ServerApi, 'picture' | 'sound' | 'wordAudio'>,
): AnkiConnect {
  async function invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
    let response: Response;
    try {
      response = await fetchFn(address, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          action,
          version: ANKI_VERSION,
          ...(key === '' ? {} : { key }),
          ...(params === undefined ? {} : { params }),
        }),
      });
    } catch {
      throw new AnkiError(
        `Anki could not be reached at ${address}. Is Anki open, with AnkiConnect letting this page in?`,
        true,
      );
    }
    if (!response.ok) {
      throw new AnkiError(`The AnkiConnect at ${address} answered ${response.status}.`);
    }
    return resultOf<T>(response, 'device');
  }

  return {
    where: 'device',
    invoke,
    async store(cut) {
      const blob =
        cut.kind === 'word-audio'
          ? await wordAudioOf(api, cut.term, cut.reading, cut.choice)
          : cut.kind === 'picture'
            ? await api.picture(cut.options)
            : await api.sound(cut.options);
      const bytes = new Uint8Array(await blob.arrayBuffer());
      const filename = mediaName(cut, blob.type, bytes);
      const stored = await invoke<unknown>('storeMediaFile', {
        filename,
        data: base64Of(bytes),
      });
      return typeof stored === 'string' && stored !== '' ? stored : filename;
    },
  };
}

/** Whether an answer of the server's word audio says the source has no sound for the word. */
function wordAudioMissing(error: unknown): WordAudioMissingError | undefined {
  return error instanceof HttpError && (error.status === 404 || error.status === 502)
    ? new WordAudioMissingError(error.message)
    : undefined;
}

async function wordAudioOf(
  api: Pick<ServerApi, 'wordAudio'>,
  term: string,
  reading: string,
  choice: string | undefined,
): Promise<Blob> {
  try {
    return await api.wordAudio(term, reading, undefined, choice);
  } catch (error) {
    throw wordAudioMissing(error) ?? error;
  }
}

function serverError(error: unknown): AnkiError {
  if (error instanceof AnkiError) return error;
  if (error instanceof HttpError) return new AnkiError(error.message, error.status >= 500);
  if (error instanceof UnreachableError) return new AnkiError(error.message, true);
  return new AnkiError(error instanceof Error ? error.message : String(error));
}

/** An AnkiConnect on a server, which this server calls for the page with the key. */
export function serverAnki(
  api: Pick<ServerApi, 'anki' | 'pictureToAnki' | 'soundToAnki' | 'wordAudioToAnki'>,
): AnkiConnect {
  return {
    where: 'server',
    async invoke<T>(action: string, params?: Record<string, unknown>): Promise<T> {
      try {
        return await resultOf<T>(await api.anki(action, params), 'server');
      } catch (error) {
        throw serverError(error);
      }
    },
    async store(cut) {
      try {
        const stored =
          cut.kind === 'word-audio'
            ? await api.wordAudioToAnki(cut.term, cut.reading, cut.choice)
            : cut.kind === 'picture'
              ? await api.pictureToAnki(cut.options)
              : await api.soundToAnki(cut.options);
        return stored.FileName;
      } catch (error) {
        throw (
          (cut.kind === 'word-audio' ? wordAudioMissing(error) : undefined) ?? serverError(error)
        );
      }
    },
  };
}
