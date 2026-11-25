import { describe, expect, it } from 'vitest';

import { line } from '@/testing/fakes';

import { applyNews, clockOf, gameOf, newsOf, withEarlier } from './lines';

const held = [line(3, 'three'), line(4, 'four'), line(6, 'six')];

describe('the lines the page holds', () => {
  it('reads the server’s events, and nothing it does not know', () => {
    expect(newsOf('hello', '{"Oldest":3,"Total":2,"MaxLines":1000}')).toEqual({
      kind: 'hello',
      hello: { Oldest: 3, Total: 2, MaxLines: 1000 },
    });
    expect(newsOf('line', JSON.stringify(line(7, 'seven')))).toEqual({
      kind: 'line',
      line: line(7, 'seven'),
    });
    expect(newsOf('removed', '{"UpTo":4}')).toEqual({ kind: 'removed', upTo: 4 });
    expect(newsOf('cleared', '{}')).toEqual({ kind: 'cleared' });

    for (const [name, data] of [
      ['line', 'not json'],
      ['line', '{"Id":"7"}'],
      ['line', 'null'],
      ['removed', '{}'],
      ['hello', '[]'],
      ['watches', '{"Watches":[]}'],
    ] as const) {
      expect(newsOf(name, data), `${name} ${data}`).toBeUndefined();
    }
  });

  it('puts a new line at the end, and one that came late where it belongs', () => {
    const after = applyNews(held, { kind: 'line', line: line(7, 'seven') });
    expect(after.map((entry) => entry.Id)).toEqual([3, 4, 6, 7]);

    const between = applyNews(held, { kind: 'line', line: line(5, 'five') });
    expect(between.map((entry) => entry.Id)).toEqual([3, 4, 5, 6]);

    expect(applyNews([], { kind: 'line', line: line(1, 'one') })).toEqual([line(1, 'one')]);
  });

  it('keeps one line for a number: a line that comes again takes its place', () => {
    // The page asked for the lines after one it has, and the server sent it once more.
    expect(applyNews(held, { kind: 'line', line: line(4, 'four') })).toBe(held);

    // The game showed the line again, and this time its voice came with it.
    const quiet = [line(3, 'three'), line(4, 'four', { HasAudio: false })];
    const voiced = applyNews(quiet, { kind: 'line', line: line(4, 'four') });
    expect(voiced).toEqual([line(3, 'three'), line(4, 'four')]);
    expect(voiced).not.toBe(quiet);
  });

  it('lets go of what the server let go of', () => {
    expect(applyNews(held, { kind: 'removed', upTo: 4 }).map((entry) => entry.Id)).toEqual([6]);
    expect(applyNews(held, { kind: 'removed', upTo: 2 })).toBe(held);
    expect(applyNews(held, { kind: 'removed', upTo: 99 })).toEqual([]);
    expect(applyNews(held, { kind: 'cleared' })).toEqual([]);

    // A connection that opens says what is left: the page was away while lines went.
    const hello = (Oldest: number, Total: number) =>
      ({ kind: 'hello', hello: { Oldest, Total, MaxLines: 1000 } }) as const;
    expect(applyNews(held, hello(4, 2)).map((entry) => entry.Id)).toEqual([4, 6]);
    expect(applyNews(held, hello(1, 6))).toBe(held);
    expect(applyNews(held, hello(0, 0))).toEqual([]);
  });

  it('puts earlier lines before the ones held, each once', () => {
    const earlier = [line(1, 'one'), line(2, 'two'), line(3, 'three again')];

    expect(withEarlier(held, earlier).map((entry) => entry.Text)).toEqual([
      'one',
      'two',
      'three',
      'four',
      'six',
    ]);
    expect(withEarlier(held, [])).toBe(held);
    expect(withEarlier([], earlier)).toEqual(earlier);
  });

  it('says when a line came by the clock on the wall, and which game the lines are from', () => {
    expect(clockOf(new Date(2026, 0, 15, 9, 5).toISOString())).toBe('09:05');
    expect(clockOf('nonsense')).toBe('');

    expect(gameOf(held)).toBeUndefined();
    expect(
      gameOf([line(1, 'a', { Game: 'First Novel' }), line(2, 'b', { Game: 'Second Novel' })]),
    ).toBe('Second Novel');
    expect(gameOf([line(1, 'a', { Game: 'First Novel' }), line(2, 'b')])).toBe('First Novel');
  });
});
