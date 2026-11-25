import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { eventStream, type EventStream, line } from '@/testing/fakes';

import { createLineFeed, type FeedStatus, RETRY_DELAYS, SILENCE_MS } from './feed';
import type { StreamNews } from './lines';

/** A server whose stream of news the test opens, answers and breaks. */
function fakeEvents() {
  const opened: { stream: EventStream; signal: AbortSignal; after: number }[] = [];
  const answers: (Response | Error | 'stream')[] = [];
  return {
    opened,
    answers,
    api: {
      events(after: number, signal: AbortSignal): Promise<Response> {
        const next = answers.shift() ?? 'stream';
        if (next instanceof Error) return Promise.reject(next);
        if (next instanceof Response) return Promise.resolve(next);
        const stream = eventStream();
        signal.addEventListener('abort', () =>
          stream.fail(new DOMException('Aborted', 'AbortError')),
        );
        opened.push({ stream, signal, after });
        return Promise.resolve(stream.response);
      },
    },
    get last(): EventStream {
      return opened.at(-1)!.stream;
    },
  };
}

function start(server: ReturnType<typeof fakeEvents>, after = 0) {
  const news: StreamNews[] = [];
  const statuses: FeedStatus[] = [];
  const feed = createLineFeed(
    {
      api: server.api,
      setTimeout: (callback, ms) => window.setTimeout(callback, ms),
      clearTimeout: (handle) => window.clearTimeout(handle),
    },
    { onNews: (item) => news.push(item), onStatus: (status) => statuses.push(status) },
  );
  feed.start(after);
  return { feed, news, statuses };
}

describe('the feed of the game’s lines', () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: 1_000_000 });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('asks for the lines after the newest the page holds, and passes on what comes', async () => {
    const server = fakeEvents();
    const { feed, news, statuses } = start(server, 12);
    await vi.advanceTimersByTimeAsync(0);
    expect(statuses).toEqual(['connecting']);
    expect(server.opened[0]!.after).toBe(12);

    server.last.hello({ Oldest: 1, Total: 12 });
    server.last.line(line(13, 'おはよう。'));
    server.last.write(': beat\n\n');
    server.last.removed(1);
    server.last.cleared();
    // An event of a newer server is nothing this page knows.
    server.last.event('later', { Something: 1 });
    await vi.advanceTimersByTimeAsync(0);

    expect(statuses).toEqual(['connecting', 'live']);
    expect(news.map((item) => item.kind)).toEqual(['hello', 'line', 'removed', 'cleared']);
    feed.stop();
  });

  it('opens the stream again when it breaks, from the newest line it was given', async () => {
    const server = fakeEvents();
    server.answers.push(new TypeError('Failed to fetch'), new Response('busy', { status: 429 }));
    const { feed, news, statuses } = start(server, 3);

    // Refused twice: tries again after one, then two seconds.
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0] - 1);
    expect(server.opened).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1 + RETRY_DELAYS[1]);
    expect(server.opened).toHaveLength(1);
    expect(statuses).toEqual(['connecting', 'retrying']);

    // A greeting puts it back to the shortest wait.
    server.last.hello({ Oldest: 1, Total: 3 });
    server.last.line(line(4, 'four'));
    server.last.line(line(5, 'five'));
    await vi.advanceTimersByTimeAsync(0);
    server.last.close();
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(2);
    // Nothing said while it was away is missed, and nothing it has is asked for again.
    expect(server.opened[1]!.after).toBe(5);
    server.last.hello({ Oldest: 1, Total: 5 });
    await vi.advanceTimersByTimeAsync(0);

    expect(statuses).toEqual(['connecting', 'retrying', 'live', 'retrying', 'live']);
    expect(news.filter((item) => item.kind === 'line')).toHaveLength(2);
    feed.stop();
  });

  it('takes a stream that falls silent for a dead one', async () => {
    const server = fakeEvents();
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(0);
    server.last.hello();

    // Signs of life keep it open.
    for (let i = 0; i < 4; i += 1) {
      await vi.advanceTimersByTimeAsync(15_000);
      server.last.write(': beat\n\n');
    }
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(SILENCE_MS);
    expect(server.opened[0]!.signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(2);
    feed.stop();
  });

  it('stops for good when the server no longer takes the sign-in', async () => {
    const server = fakeEvents();
    server.answers.push(new Response(null, { status: 401 }));
    const { statuses } = start(server);

    await vi.advanceTimersByTimeAsync(60_000);

    expect(statuses).toEqual(['connecting', 'signed-out']);
    expect(server.opened).toHaveLength(0);
  });

  it('closes the stream when stopped and opens it at once when started again', async () => {
    const server = fakeEvents();
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(0);

    feed.stop();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(server.opened).toHaveLength(1);
    expect(server.opened[0]!.signal.aborted).toBe(true);

    feed.start(9);
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(2);
    expect(server.opened[1]!.after).toBe(9);
    feed.stop();
  });

  it('connects at once when asked to, instead of waiting out a retry', async () => {
    const server = fakeEvents();
    server.answers.push(new TypeError('Failed to fetch'), new TypeError('Failed to fetch'));
    const { feed } = start(server);
    await vi.advanceTimersByTimeAsync(RETRY_DELAYS[0]);
    expect(server.opened).toHaveLength(0);

    // The device is back online.
    feed.reconnectNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(1);

    // While connected there is nothing to do.
    feed.reconnectNow();
    await vi.advanceTimersByTimeAsync(0);
    expect(server.opened).toHaveLength(1);
    feed.stop();
  });
});
