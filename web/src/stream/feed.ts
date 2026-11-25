import { readEvents } from '@/api/eventStream';
import type { ServerApi } from '@/api/server';

import { newsOf, type StreamNews } from './lines';

export type FeedStatus =
  /** Connecting for the first time. */
  | 'connecting'
  /** Connected: lines arrive as the game sends them. */
  | 'live'
  /** The connection broke. The feed tries again by itself. */
  | 'retrying'
  /** The server no longer takes the sign-in. The feed has stopped. */
  | 'signed-out';

export interface FeedEnv {
  api: Pick<ServerApi, 'events'>;
  setTimeout: (callback: () => void, ms: number) => number;
  clearTimeout: (handle: number) => void;
}

export interface FeedHandlers {
  onNews: (news: StreamNews) => void;
  onStatus: (status: FeedStatus) => void;
}

/** How long the feed waits before it tries again, by the number of tries so far. */
export const RETRY_DELAYS = [1_000, 2_000, 5_000, 10_000, 20_000, 30_000] as const;

/**
 * The server writes a sign of life every 15 seconds. When three are missing, the connection is
 * taken to be dead even if the browser has not noticed: a phone that changed networks, a sleeping
 * laptop, a proxy that dropped it.
 */
export const SILENCE_MS = 45_000;

/**
 * Follows the stream of the game's lines: opens it, reads it, and opens it again when it breaks,
 * waiting longer each time. Each connection asks for the lines after the newest one it was given,
 * so nothing said while it was away is missed.
 */
export function createLineFeed(env: FeedEnv, handlers: FeedHandlers) {
  let running = false;
  let tries = 0;
  let newest = 0;
  let abort: AbortController | null = null;
  let retryTimer: number | null = null;
  let silenceTimer: number | null = null;
  let status: FeedStatus | null = null;

  function setStatus(next: FeedStatus): void {
    if (next === status) return;
    status = next;
    handlers.onStatus(next);
  }

  function clearTimers(): void {
    if (retryTimer !== null) env.clearTimeout(retryTimer);
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    retryTimer = null;
    silenceTimer = null;
  }

  /** Each sign of life puts off the moment the connection is taken to be dead. */
  function heardFromServer(connection: AbortController): void {
    if (abort !== connection) return;
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    silenceTimer = env.setTimeout(() => connection.abort(), SILENCE_MS);
  }

  function retryLater(): void {
    if (!running) return;
    setStatus('retrying');
    const delay = RETRY_DELAYS[Math.min(tries, RETRY_DELAYS.length - 1)]!;
    tries += 1;
    retryTimer = env.setTimeout(() => {
      retryTimer = null;
      void connect();
    }, delay);
  }

  async function connect(): Promise<void> {
    if (!running) return;
    const mine = new AbortController();
    abort = mine;
    try {
      heardFromServer(mine);
      const response = await env.api.events(newest, mine.signal);
      if (abort !== mine) return;
      if (response.status === 401 || response.status === 403) {
        running = false;
        clearTimers();
        setStatus('signed-out');
        return;
      }

      if (!response.ok || response.body === null) throw new Error(`answered ${response.status}`);
      await readEvents(
        response.body,
        (event) => {
          if (abort !== mine) return;
          const news = newsOf(event.name, event.data);
          if (news === undefined) return;
          if (news.kind === 'hello') {
            tries = 0;
            setStatus('live');
          } else if (news.kind === 'line') {
            newest = Math.max(newest, news.line.Id);
          }
          handlers.onNews(news);
        },
        () => heardFromServer(mine),
      );
    } catch {
      // Broken, refused, unreadable or called off: the same for the page. It tries again.
    }

    // A connection the page stopped, or one that another took the place of, leaves the rest alone.
    if (abort !== mine) return;

    // The stream ended, broke or fell silent. It is opened again.
    abort = null;
    if (silenceTimer !== null) env.clearTimeout(silenceTimer);
    silenceTimer = null;
    if (running) retryLater();
  }

  return {
    /**
     * Starts following, from the line after a number: the newest the page holds. Calling it again
     * while it runs does nothing.
     */
    start(after: number): void {
      if (running) return;
      running = true;
      tries = 0;
      newest = after;
      setStatus('connecting');
      void connect();
    },

    /** Stops following and closes the connection. */
    stop(): void {
      running = false;
      clearTimers();
      abort?.abort();
      abort = null;
    },

    /** Gives up waiting and connects at once, after the device woke or came back online. */
    reconnectNow(): void {
      if (!running || abort !== null) return;
      clearTimers();
      void connect();
    },
  };
}

export type LineFeed = ReturnType<typeof createLineFeed>;
