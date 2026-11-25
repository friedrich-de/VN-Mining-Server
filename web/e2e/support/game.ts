import type { TestServer } from './server';

/** A line as a game shows it. */
export interface GameLine {
  text: string;
  /** Who says it. */
  name?: string;
  /** The voice file's name in the game. */
  voice?: string;
  audio?: Buffer;
  screenshot?: Buffer;
}

/** A line as the server answers it. */
export interface SentLine {
  Id: number;
  At: string;
  Text: string;
  Name?: string;
  Game?: string;
  HasAudio: boolean;
  HasPicture: boolean;
}

/** Reads a mining.txt as the game does: "name = value" rows, "#" starts a comment. */
export function readGameFile(text: string): { server: string; key: string } {
  const values: Record<string, string> = {};
  for (const row of text.split(/\r?\n/)) {
    const trimmed = row.trim();
    if (trimmed === '' || trimmed.startsWith('#')) continue;
    const equals = trimmed.indexOf('=');
    if (equals === -1) continue;
    values[trimmed.slice(0, equals).trim().toLowerCase()] = trimmed.slice(equals + 1).trim();
  }
  if (!values.server || !values.key) throw new Error('The file names no server or no key.');
  return { server: values.server, key: values.key };
}

function kindOf(data: Buffer): string {
  if (data.subarray(0, 4).toString('latin1') === 'OggS') return 'audio/ogg';
  if (data[0] === 0xff && data[1] === 0xd8) return 'image/jpeg';
  if (data.subarray(1, 4).toString('latin1') === 'PNG') return 'image/png';
  return 'application/octet-stream';
}

/**
 * A pretend game: it sends its lines to the server the way the iOS app does, as a form with the
 * text, who speaks, and the voice and screenshot as files, with the key from its mining.txt.
 */
export class PretendGame {
  constructor(
    private readonly server: string,
    private readonly key: string,
    private readonly title: string | undefined = 'Sample Novel',
  ) {}

  static of(server: TestServer, title?: string): PretendGame {
    return new PretendGame(server.url, server.key, title ?? 'Sample Novel');
  }

  /** A game that was given the file the page hands out. */
  static fromFile(text: string, title?: string): PretendGame {
    const { server, key } = readGameFile(text);
    return new PretendGame(server, key, title ?? 'Sample Novel');
  }

  async post(line: GameLine): Promise<Response> {
    const form = new FormData();
    form.set('text', line.text);
    if (line.name !== undefined) form.set('name', line.name);
    if (line.voice !== undefined) form.set('voice', line.voice);
    if (this.title !== undefined) form.set('game', this.title);
    if (line.audio !== undefined) {
      form.set(
        'audio',
        new Blob([new Uint8Array(line.audio)], { type: kindOf(line.audio) }),
        'voice.ogg',
      );
    }
    if (line.screenshot !== undefined) {
      form.set(
        'screenshot',
        new Blob([new Uint8Array(line.screenshot)], { type: kindOf(line.screenshot) }),
        'screenshot',
      );
    }
    return fetch(`${this.server.replace(/\/$/, '')}/Api/Lines`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${this.key}` },
      body: form,
    });
  }

  /** Shows a line and returns it as the server kept it. */
  async say(line: GameLine): Promise<SentLine> {
    const response = await this.post(line);
    if (!response.ok)
      throw new Error(`The server answered ${response.status}: ${await response.text()}`);
    return (await response.json()) as SentLine;
  }
}
