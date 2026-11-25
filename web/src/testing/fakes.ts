import type { Fetch } from '@/api/http';
import type { SettingsView, Status, StreamLine } from '@/api/types';
import type { AuthEnv } from '@/auth/session';
import type { Hello } from '@/stream/lines';

export interface SentRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

export type Answer = Response | Error | ((request: SentRequest) => Response | Error);

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

export function problem(status: number, detail: string): Response {
  return json({ status, detail }, status);
}

/** The headers of a request with lower-case names, whichever way they were given. */
function headerMap(headers: HeadersInit | undefined): Record<string, string> {
  const map: Record<string, string> = {};
  if (headers === undefined) return map;
  const pairs =
    headers instanceof Headers
      ? [...headers.entries()]
      : Array.isArray(headers)
        ? headers
        : Object.entries(headers);
  for (const [name, value] of pairs) {
    if (name !== undefined && value !== undefined) map[name.toLowerCase()] = value;
  }
  return map;
}

/** A server that answers from a list and remembers what it was asked. */
export function fakeServer(...answers: Answer[]): { fetch: Fetch; sent: SentRequest[] } {
  const sent: SentRequest[] = [];
  const fetch: Fetch = (input, init) => {
    const request: SentRequest = {
      url: urlOf(input),
      method: init?.method ?? 'GET',
      headers: headerMap(init?.headers),
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : null,
    };
    sent.push(request);
    const next = answers.shift();
    if (next === undefined) return Promise.reject(new Error(`No answer left for ${request.url}`));
    const answer = typeof next === 'function' ? next(request) : next;
    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  };
  return { fetch, sent };
}

/** Where the page is served in tests: below a base path, as behind a reverse proxy. */
export const BASE = 'https://vn.example/base/';
export const STATUS_URL = 'https://vn.example/base/Api/Status';
export const SESSION_URL = 'https://vn.example/base/Api/Session';

export function status(overrides: Partial<Status> = {}): Status {
  return { Version: '0.1.0', Lines: 0, MaxLines: 1000, ...overrides };
}

export function env(fetch: Fetch): AuthEnv {
  return { fetch, base: BASE, version: '0.1.0' };
}

/** A line of a game, as the server sends it. Its minute is its number. */
export function line(id: number, text: string, more: Partial<StreamLine> = {}): StreamLine {
  return {
    Id: id,
    At: new Date(2026, 0, 15, 21, id % 60).toISOString(),
    Text: text,
    HasAudio: true,
    HasPicture: true,
    ...more,
  };
}

/** The settings of someone who has chosen nothing yet. */
export function settings(overrides: Partial<SettingsView> = {}): SettingsView {
  return {
    Anki: {
      Where: 'off',
      DeviceAddress: 'http://127.0.0.1:8765',
      DeviceKey: '',
      ServerAddress: '',
      ServerKeySet: false,
    },
    Stream: { MaxLines: 1000 },
    ...overrides,
  };
}

/** A stream of server-sent events that a test writes into. */
export function eventStream() {
  const encoder = new TextEncoder();
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  let open = true;
  const body = new ReadableStream<Uint8Array>({
    start(started) {
      controller = started;
    },
  });
  const write = (text: string): void => {
    if (open) controller.enqueue(encoder.encode(text));
  };
  const event = (name: string, data: unknown): void =>
    write(`event: ${name}\ndata: ${JSON.stringify(data)}\n\n`);
  return {
    response: new Response(body, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    }),
    write,
    event,
    /** What the server says first on every connection. */
    hello(hello: Partial<Hello> = {}): void {
      event('hello', { Oldest: 0, Total: 0, MaxLines: 1000, ...hello });
    },
    line(sent: StreamLine): void {
      event('line', sent);
    },
    removed(upTo: number): void {
      event('removed', { UpTo: upTo });
    },
    cleared(): void {
      event('cleared', {});
    },
    close(): void {
      if (open) controller.close();
      open = false;
    },
    fail(error: unknown): void {
      if (open) controller.error(error);
      open = false;
    },
  };
}

export type EventStream = ReturnType<typeof eventStream>;

function urlOf(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
}

export interface RoutedRequest extends SentRequest {
  /** The path below the server's API, such as "Lines/3/Picture". */
  path: string;
  query: URLSearchParams;
  signal: AbortSignal | undefined;
}

/** A server that answers by address, with what the test's function returns. */
export function fakeRoutes(
  route: (request: RoutedRequest) => Response | Error | Promise<Response> | undefined,
): { fetch: Fetch; sent: RoutedRequest[] } {
  const sent: RoutedRequest[] = [];
  const fetch: Fetch = (input, init) => {
    const url = new URL(urlOf(input));
    const request: RoutedRequest = {
      url: url.href,
      method: init?.method ?? 'GET',
      headers: headerMap(init?.headers),
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : null,
      path: url.pathname.replace(/^.*\/Api\//, ''),
      query: url.searchParams,
      signal: init?.signal ?? undefined,
    };
    sent.push(request);
    const answer = route(request) ?? problem(404, `Nothing at ${url.pathname}`);
    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  };
  return { fetch, sent };
}

/**
 * The server behind the page, with a stream of lines: it answers the newest lines, the lines
 * before a number, and a stream of news per connection that the test writes to. Everything else
 * goes to the test's own function.
 */
export function fakeStream(
  held: StreamLine[] = [],
  route: (request: RoutedRequest) => Response | Error | Promise<Response> | undefined = () =>
    undefined,
) {
  const streams: EventStream[] = [];
  const server = fakeRoutes((request) => {
    const own = route(request);
    if (own !== undefined) return own;

    if (request.path === 'Lines' && request.method === 'GET') {
      const limit = Number(request.query.get('limit') ?? '300');
      const before = request.query.get('before');
      const pool = before === null ? held : held.filter((entry) => entry.Id < Number(before));
      return json({ Lines: pool.slice(-limit), Total: held.length, MaxLines: 1000 });
    }

    if (request.path === 'Events') {
      const stream = eventStream();
      request.signal?.addEventListener('abort', () =>
        stream.fail(new DOMException('Aborted', 'AbortError')),
      );
      streams.push(stream);
      return stream.response;
    }

    return undefined;
  });
  return {
    ...server,
    streams,
    /** The connection the page holds now. */
    get stream(): EventStream {
      return streams.at(-1)!;
    },
    asked: (path: string) => server.sent.filter((request) => request.path === path),
  };
}
