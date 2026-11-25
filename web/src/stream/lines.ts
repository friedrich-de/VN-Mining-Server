import type { StreamLine } from '@/api/types';

/** What the server says about the stream when the page connects. */
export interface Hello {
  /** The number of the oldest line it keeps, or 0 when it keeps none. */
  Oldest: number;
  Total: number;
  MaxLines: number;
}

/** One piece of news of the stream. */
export type StreamNews =
  | { kind: 'hello'; hello: Hello }
  | { kind: 'line'; line: StreamLine }
  /** The server let go of its oldest lines, up to and with this number. */
  | { kind: 'removed'; upTo: number }
  | { kind: 'cleared' };

/** The news in one of the server's events, or nothing for an event the page does not know. */
export function newsOf(name: string, data: string): StreamNews | undefined {
  let value: unknown;
  try {
    value = JSON.parse(data);
  } catch {
    return undefined;
  }
  if (value === null || typeof value !== 'object') return undefined;
  const fields = value as Record<string, unknown>;
  if (name === 'hello' && typeof fields.Oldest === 'number' && typeof fields.Total === 'number') {
    return { kind: 'hello', hello: value as Hello };
  }
  if (name === 'line' && typeof fields.Id === 'number' && typeof fields.Text === 'string') {
    return { kind: 'line', line: value as StreamLine };
  }
  if (name === 'removed' && typeof fields.UpTo === 'number') {
    return { kind: 'removed', upTo: fields.UpTo };
  }
  if (name === 'cleared') return { kind: 'cleared' };
  return undefined;
}

/**
 * The lines the page holds after a piece of news. They stay in the order of their numbers, and a
 * line that comes again takes the place of the one held: it may have gained its voice or picture.
 * The same list comes back when nothing changes.
 */
export function applyNews(lines: readonly StreamLine[], news: StreamNews): readonly StreamLine[] {
  switch (news.kind) {
    case 'cleared':
      return lines.length === 0 ? lines : [];

    case 'hello':
      // What the server no longer keeps cannot be made a card of.
      if (news.hello.Total === 0) return lines.length === 0 ? lines : [];
      return dropUpTo(lines, news.hello.Oldest - 1);

    case 'removed':
      return dropUpTo(lines, news.upTo);

    case 'line': {
      const line = news.line;
      const last = lines[lines.length - 1];
      if (last === undefined || last.Id < line.Id) return [...lines, line];
      const at = lines.findIndex((held) => held.Id >= line.Id);
      if (lines[at]!.Id === line.Id) {
        return same(lines[at]!, line)
          ? lines
          : [...lines.slice(0, at), line, ...lines.slice(at + 1)];
      }
      return [...lines.slice(0, at), line, ...lines.slice(at)];
    }
  }
}

function dropUpTo(lines: readonly StreamLine[], upTo: number): readonly StreamLine[] {
  const first = lines.findIndex((line) => line.Id > upTo);
  if (first === 0) return lines;
  return first === -1 ? [] : lines.slice(first);
}

function same(a: StreamLine, b: StreamLine): boolean {
  return (
    a.Text === b.Text &&
    a.Name === b.Name &&
    a.Game === b.Game &&
    a.HasAudio === b.HasAudio &&
    a.HasPicture === b.HasPicture
  );
}

/** Older lines put before the ones held. A line held already stays as it is. */
export function withEarlier(
  lines: readonly StreamLine[],
  earlier: readonly StreamLine[],
): readonly StreamLine[] {
  const first = lines[0]?.Id ?? Number.POSITIVE_INFINITY;
  const older = earlier.filter((line) => line.Id < first);
  return older.length === 0 ? lines : [...older, ...lines];
}

/** When a line came, as the clock on the wall showed it: 21:07. */
export function clockOf(at: string): string {
  const time = new Date(at);
  if (Number.isNaN(time.getTime())) return '';
  const two = (value: number): string => String(value).padStart(2, '0');
  return `${two(time.getHours())}:${two(time.getMinutes())}`;
}

/** The game the newest lines are from, if the game says its title. */
export function gameOf(lines: readonly StreamLine[]): string | undefined {
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const game = lines[index]!.Game;
    if (game !== undefined && game !== '') return game;
  }
  return undefined;
}
