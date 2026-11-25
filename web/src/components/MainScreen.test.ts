import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { SettingsView, StreamLine } from '@/api/types';
import type { Session } from '@/auth/session';
import { type Collection, createCollection } from '@/testing/ankiCollection';
import {
  env,
  fakeRoutes,
  fakeStream,
  json,
  line,
  problem,
  type RoutedRequest,
  settings,
  status,
} from '@/testing/fakes';

import MainScreen from './MainScreen.vue';

/** A scene of a game: who speaks, and a line of narration without a voice. */
function scene(): StreamLine[] {
  return [
    line(1, '昨日の話、覚えてる？', { Name: 'ユキ', Game: 'Sample Novel' }),
    line(2, 'もちろん。\n約束したからね。', { Name: 'ハル', Game: 'Sample Novel' }),
    line(3, '窓の外では雨が降っていた。', { HasAudio: false, Game: 'Sample Novel' }),
    line(4, 'じゃあ、明日の朝に行こう。', { Name: 'ユキ', Game: 'Sample Novel' }),
  ];
}

const session: Session = { status: status() };

/** Cards go to an AnkiConnect on a server, which this server calls with the key. */
function serverSettings(page?: unknown): SettingsView {
  return settings({
    Anki: {
      Where: 'server',
      DeviceAddress: 'http://127.0.0.1:8765',
      DeviceKey: '',
      ServerAddress: 'https://anki.example.org',
      ServerKeySet: true,
    },
    ...(page === undefined ? {} : { Page: page }),
  });
}

/** The server behind the page: its lines, a stream of news, and the media of lines. */
function server(
  held: StreamLine[] = scene(),
  user: {
    settings?: SettingsView;
    anki?: Collection;
    /** Holds AnkiConnect's answers to one action until the promise settles. */
    holdAnki?: { action: string; until: Promise<void> };
    /** How many times reading the lines fails before it works. */
    linesFail?: number;
  } = {},
) {
  let linesFail = user.linesFail ?? 0;
  return fakeStream(held, (request: RoutedRequest) => {
    if (request.path === 'Settings' && user.settings !== undefined) return json(user.settings);

    if (request.path === 'Lines' && request.method === 'GET' && linesFail > 0) {
      linesFail -= 1;
      return problem(503, 'The server is starting.');
    }

    if (request.path === 'Lines' && request.method === 'DELETE') {
      held.splice(0);
      return new Response(null, { status: 204 });
    }

    // The server passes the call on to the user's AnkiConnect, with the key.
    if (request.path === 'Anki' && user.anki !== undefined) {
      const call = request.body as { Action: string; Params?: unknown };
      const anki = user.anki;
      const answer = () =>
        json(anki.answer({ action: call.Action, version: 6, params: call.Params }));
      const hold = user.holdAnki;
      return hold?.action === call.Action ? hold.until.then(answer) : answer();
    }

    const pictureToAnki = /^Lines\/(\d+)\/Picture\/Anki$/.exec(request.path);
    if (pictureToAnki !== null && user.anki !== undefined) {
      const name = `vn-miner_${pictureToAnki[1]}_picture.jpg`;
      user.anki.handle('storeMediaFile', { filename: name, data: btoa('picture') });
      return json({ FileName: name });
    }

    if (request.path === 'Lines/Audio/Anki' && user.anki !== undefined) {
      const name = `vn-miner_${request.query.get('lines')!.split(',')[0]}_sound.mp3`;
      user.anki.handle('storeMediaFile', { filename: name, data: btoa('sound') });
      return json({ FileName: name });
    }

    if (/^Lines\/\d+\/Picture$/.test(request.path)) {
      return new Response(new Blob(['picture'], { type: 'image/jpeg' }));
    }

    if (request.path === 'Lines/Audio') {
      return new Response(new Blob(['sound'], { type: 'audio/mpeg' }));
    }

    return undefined;
  });
}

let page: VueWrapper | undefined;

/** The addresses of pictures and sounds the page let go of. */
let revoked: string[] = [];

/** The lines the page scrolled to, by their numbers. */
let scrolled: string[] = [];

async function open(fake: ReturnType<typeof server>) {
  page = mount(MainScreen, {
    props: { env: env(fake.fetch), session },
    attachTo: document.body,
  });
  await flushPromises();
  return page;
}

async function settle(ms = 0): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await flushPromises();
}

function setVisibility(state: 'visible' | 'hidden'): void {
  vi.spyOn(document, 'visibilityState', 'get').mockReturnValue(state);
  document.dispatchEvent(new Event('visibilitychange'));
}

const texts = (wrapper: VueWrapper): string[] =>
  wrapper.findAll('.line .said').map((said) => said.text());

describe('the main screen', () => {
  beforeEach(() => {
    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'performance', 'Date'],
    });
    let next = 0;
    revoked = [];
    scrolled = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:test/${(next += 1)}`);
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation((url) => revoked.push(url));
    vi.spyOn(HTMLElement.prototype, 'scrollIntoView').mockImplementation(function (
      this: HTMLElement,
    ) {
      scrolled.push(this.dataset.line ?? '');
    });
  });

  afterEach(() => {
    page?.unmount();
    page = undefined;
    vi.useRealTimers();
  });

  it('says how a game gets its lines here while there are none', async () => {
    const fake = server([], { settings: settings() });
    const screen = await open(fake);

    expect(screen.get('[data-testid="no-lines"] h1').text()).toBe('No lines yet');
    expect(screen.get('[data-testid="no-lines"]').text()).toContain('mining.txt');
    expect(screen.get('[data-testid="title"]').text()).toBe('Visual novel');

    // The file for the game is in the settings.
    await screen.get('[data-testid="get-game-file"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="game-file"]').attributes('href')).toBe(
      'https://vn.example/base/Api/GameSettings',
    );
  });

  it('shows the newest lines with who says them, and what the server keeps of each', async () => {
    const fake = server();
    const screen = await open(fake);

    expect(fake.asked('Lines')[0]!.query.get('limit')).toBe('300');
    expect(screen.get('[data-testid="title"]').text()).toBe('Sample Novel');
    expect(texts(screen)).toEqual([
      '昨日の話、覚えてる？',
      'もちろん。\n約束したからね。',
      '窓の外では雨が降っていた。',
      'じゃあ、明日の朝に行こう。',
    ]);
    expect(screen.get('.line .said').attributes('lang')).toBe('ja');
    expect(screen.findAll('.line .speaker').map((name) => name.text())).toEqual([
      'ユキ',
      'ハル',
      'ユキ',
    ]);
    expect(screen.get('.line .time').text()).toContain('21:01');

    // The narration has a screenshot and no voice.
    const narration = screen.findAll('.line')[2]!;
    expect(narration.find('[aria-label="With voice"]').exists()).toBe(false);
    expect(narration.find('[aria-label="With screenshot"]').exists()).toBe(true);

    // The newest line is the one the page keeps in view.
    expect(screen.get('[aria-current="true"] .said').text()).toBe('じゃあ、明日の朝に行こう。');
    expect(scrolled.at(-1)).toBe('4');
  });

  it('takes each line as the game sends it, from the newest it holds', async () => {
    const fake = server();
    const screen = await open(fake);
    expect(fake.asked('Events')[0]!.query.get('after')).toBe('4');
    expect(screen.get('[data-testid="where"]').text()).toBe('Connecting · newest line 21:04');

    fake.stream.hello({ Oldest: 1, Total: 4 });
    fake.stream.line(line(5, 'おはよう。', { Name: 'ハル', HasAudio: false }));
    await settle();

    expect(texts(screen).at(-1)).toBe('おはよう。');
    expect(screen.get('[data-testid="where"]').text()).toBe('Live · newest line 21:05');
    expect(scrolled.at(-1)).toBe('5');

    // The game shows the line again after its menu, now with its voice: one line, not two.
    fake.stream.line(line(5, 'おはよう。', { Name: 'ハル' }));
    await settle();
    expect(texts(screen)).toHaveLength(5);
    expect(screen.findAll('.line')[4]!.find('[aria-label="With voice"]').exists()).toBe(true);
  });

  it('lets go of the lines the server let go of, chosen ones too', async () => {
    const fake = server();
    const screen = await open(fake);
    fake.stream.hello({ Oldest: 1, Total: 4 });
    await settle();

    await screen.findAll('.line .check')[0]!.trigger('click');
    await screen.findAll('.line .check')[1]!.trigger('click');
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('2 lines selected');

    fake.stream.removed(1);
    await settle();
    expect(texts(screen)).toHaveLength(3);
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('1 line selected');

    fake.stream.cleared();
    await settle();
    expect(screen.get('[data-testid="no-lines"] h1').text()).toBe('No lines yet');
    expect(screen.find('[data-testid="selected-count"]').exists()).toBe(false);
  });

  it('reads earlier lines when asked, a step at a time, until there are no more', async () => {
    const many = Array.from({ length: 450 }, (_, index) => line(index + 1, `line ${index + 1}`));
    const fake = server(many);
    const screen = await open(fake);
    expect(texts(screen)).toHaveLength(300);
    expect(texts(screen)[0]).toBe('line 151');

    await screen.get('[data-testid="show-earlier"]').trigger('click');
    await settle();

    expect(fake.asked('Lines')[1]!.query.get('before')).toBe('151');
    expect(texts(screen)).toHaveLength(450);
    expect(texts(screen)[0]).toBe('line 1');
    // These were the oldest: there is nothing more to ask for.
    expect(screen.find('[data-testid="show-earlier"]').exists()).toBe(false);
    // Reading back is not following the game.
    expect(screen.get('[data-testid="follow"]').attributes('aria-checked')).toBe('false');
  });

  it('tries again by itself when the lines cannot be read', async () => {
    const fake = server(scene(), { linesFail: 1 });
    const screen = await open(fake);

    expect(screen.get('[role="alert"]').text()).toContain('The server is starting.');
    expect(fake.asked('Events')).toHaveLength(0);

    await settle(5_000);
    expect(texts(screen)).toHaveLength(4);
    expect(fake.asked('Events')).toHaveLength(1);
  });

  it('selects lines one by one and as a range, and previews the picture and the sound', async () => {
    const fake = server();
    const screen = await open(fake);

    const checks = screen.findAll('.line .check');
    await checks[0]!.trigger('click');
    await checks[2]!.trigger('click', { shiftKey: true });
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('3 lines selected');
    expect(screen.findAll('[aria-checked="true"].check')).toHaveLength(3);
    expect(screen.text()).toContain('21:01 to 21:03');

    // Nothing is made until the choice settles.
    expect(fake.asked('Lines/1/Picture')).toHaveLength(0);
    await settle(300);
    // The picture is the first line's screenshot, the sound the voices of the lines that have one.
    const picture = fake.asked('Lines/1/Picture')[0]!;
    expect([picture.query.get('format'), picture.query.get('width')]).toEqual(['jpeg', '640']);
    const sound = fake.asked('Lines/Audio')[0]!;
    expect([sound.query.get('lines'), sound.query.get('format')]).toEqual(['1,2', 'mp3']);
    expect(screen.get('[data-testid="preview-picture"] img').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="sound"]').attributes('src')).toMatch(/^blob:/);
    expect(screen.get('[data-testid="play-sound"]').attributes('disabled')).toBeUndefined();

    // One line less: what was made is thrown away and made anew.
    await checks[0]!.trigger('click');
    await settle(300);
    expect(fake.asked('Lines/Audio').at(-1)?.query.get('lines')).toBe('2');
    expect(fake.asked('Lines/2/Picture')).toHaveLength(1);
    expect(revoked).toContain('blob:test/1');

    await screen
      .findAll('button')
      .find((button) => button.text() === 'Clear')!
      .trigger('click');
    expect(screen.find('[data-testid="selected-count"]').exists()).toBe(false);
  });

  it('keeps the chosen lines in one run, as the sound of a card is one piece', async () => {
    const fake = server();
    const screen = await open(fake);
    const checks = screen.findAll('.line .check');
    const chosen = () =>
      screen
        .findAll('.line .check')
        .flatMap((check, index) => (check.attributes('aria-checked') === 'true' ? [index] : []));

    // A line apart from the run starts a new one.
    await checks[0]!.trigger('click');
    await checks[2]!.trigger('click');
    expect(chosen()).toEqual([2]);
    // A line next to it joins it, at either end.
    await checks[1]!.trigger('click');
    await checks[0]!.trigger('click');
    expect(chosen()).toEqual([0, 1, 2]);
    // An end leaves it; a line inside it starts it anew.
    await checks[2]!.trigger('click');
    expect(chosen()).toEqual([0, 1]);
    await checks[2]!.trigger('click');
    await checks[1]!.trigger('click');
    expect(chosen()).toEqual([1]);
    // Shift takes every line between.
    await checks[2]!.trigger('click');
    await checks[0]!.trigger('click', { shiftKey: true });
    expect(chosen()).toEqual([0, 1, 2]);
  });

  it('says what a line lacks, and refuses more voices than the server joins', async () => {
    const bare = [
      line(1, '静かな部屋。', { HasAudio: false, HasPicture: false }),
      ...Array.from({ length: 31 }, (_, index) => line(index + 2, `声 ${index}`)),
    ];
    const fake = server(bare);
    const screen = await open(fake);

    const checks = screen.findAll('.line .check');
    await checks[0]!.trigger('click');
    await settle(300);
    expect(screen.get('[data-testid="preview-picture"]').text()).toBe(
      'This line has no screenshot.',
    );
    expect(screen.text()).toContain('This line has no voice.');
    expect(fake.sent.some((request) => /Picture|Audio/.test(request.path))).toBe(false);

    await checks[31]!.trigger('click', { shiftKey: true });
    await settle(300);
    expect(screen.text()).toContain('More than 30 lines with a voice. Choose fewer.');
    expect(fake.asked('Lines/Audio')).toHaveLength(0);
    // The picture is that of the first line that has one.
    expect(fake.asked('Lines/2/Picture')).toHaveLength(1);
  });

  it('stops following when the user scrolls or chooses, and follows again when asked', async () => {
    const fake = server();
    const screen = await open(fake);
    fake.stream.hello({ Oldest: 1, Total: 4 });
    await settle();
    const follow = screen.get('[data-testid="follow"]');
    expect(follow.attributes('aria-checked')).toBe('true');
    expect(scrolled.at(-1)).toBe('4');

    await screen.get('main').trigger('wheel');
    expect(follow.attributes('aria-checked')).toBe('false');
    fake.stream.line(line(5, 'おはよう。'));
    await settle();
    expect(scrolled.at(-1)).toBe('4');

    await follow.trigger('click');
    await settle();
    expect(scrolled.at(-1)).toBe('5');

    // The button over the lines goes to the newest line too, and goes away.
    expect(screen.find('[data-testid="go-to-newest"]').exists()).toBe(false);
    await screen.get('main').trigger('touchmove');
    fake.stream.line(line(6, 'いい天気だね。'));
    await settle();
    await screen.get('[data-testid="go-to-newest"]').trigger('click');
    await settle();
    expect(follow.attributes('aria-checked')).toBe('true');
    expect(scrolled.at(-1)).toBe('6');
    expect(screen.find('[data-testid="go-to-newest"]').exists()).toBe(false);

    // A line being chosen stays where it is while the game goes on.
    await screen.findAll('.line .check')[5]!.trigger('click');
    expect(follow.attributes('aria-checked')).toBe('false');
  });

  it('shows the newest line again when the user comes back from the game', async () => {
    const fake = server();
    const screen = await open(fake);
    fake.stream.hello({ Oldest: 1, Total: 4 });
    await settle();
    await screen.get('main').trigger('wheel');

    // The game is in front: the connection is closed.
    setVisibility('hidden');
    await settle();
    expect(fake.asked('Events')[0]!.signal?.aborted).toBe(true);

    // Back on the page: it asks for what the game showed meanwhile and goes there.
    setVisibility('visible');
    await settle();
    expect(fake.asked('Events')).toHaveLength(2);
    expect(fake.asked('Events')[1]!.query.get('after')).toBe('4');
    fake.stream.hello({ Oldest: 1, Total: 6 });
    fake.stream.line(line(5, 'おはよう。'));
    fake.stream.line(line(6, 'いい天気だね。'));
    await settle();

    expect(texts(screen)).toHaveLength(6);
    expect(screen.get('[data-testid="follow"]').attributes('aria-checked')).toBe('true');
    expect(scrolled.at(-1)).toBe('6');
  });

  it('shows that the connection broke, keeps what it had and misses nothing', async () => {
    const fake = server();
    const screen = await open(fake);
    fake.stream.hello({ Oldest: 1, Total: 4 });
    fake.stream.line(line(5, 'おはよう。'));
    await settle();

    fake.streams[0]!.close();
    await settle();
    expect(screen.get('[data-testid="retrying"]').text()).toContain('Trying again');
    expect(texts(screen)).toHaveLength(5);

    await settle(1_000);
    expect(fake.asked('Events')[1]!.query.get('after')).toBe('5');
    fake.stream.hello({ Oldest: 1, Total: 5 });
    await settle();
    expect(screen.find('[data-testid="retrying"]').exists()).toBe(false);
  });

  it('adds the lines, picture and sound to the card Yomitan just made, and takes it back', async () => {
    const anki = createCollection();
    anki.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: {
        Expression: '約束',
        Sentence: '<b>約束</b>したからね。',
        MainDefinition: 'promise',
      },
    });
    const fake = server(scene(), { settings: serverSettings(), anki });
    const screen = await open(fake);

    await screen.findAll('.line .check')[1]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-to-newest"]').trigger('click');
    await settle();

    // Lapis's fields are guessed by their names; the word Yomitan looked up stays bold.
    const note = [...anki.notes.values()][0]!;
    expect(note.fields).toMatchObject({
      Expression: '約束',
      MainDefinition: 'promise',
      Sentence: 'もちろん。<br><b>約束</b>したからね。',
      Picture: '<img src="vn-miner_2_picture.jpg">',
      SentenceAudio: '[sound:vn-miner_2_sound.mp3]',
      MiscInfo: 'Sample Novel',
    });
    expect([...anki.media.keys()]).toEqual(['vn-miner_2_picture.jpg', 'vn-miner_2_sound.mp3']);
    expect(fake.asked('Lines/Audio/Anki')[0]!.query.get('lines')).toBe('2');
    expect(screen.get('[data-testid="sending"]').text()).toContain('Added to "約束".');
    // The lines are let go of once the card has them; what happened stays, with its Undo.
    expect(screen.find('[data-testid="selected-count"]').exists()).toBe(false);
    expect(screen.findAll('.line.selected')).toHaveLength(0);

    await screen.get('[data-testid="undo"]').trigger('click');
    await settle();
    expect(note.fields.Sentence).toBe('<b>約束</b>したからね。');
    expect(note.fields.Picture).toBe('');
    expect(screen.get('[data-testid="sending"]').text()).toContain('The card is as it was.');

    await screen.get('[data-testid="close-sent"]').trigger('click');
    expect(screen.find('[data-testid="sending"]').exists()).toBe(false);
  });

  it('leaves a card as it is where the lines have nothing for a field', async () => {
    const anki = createCollection();
    anki.makeNote({
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '雨', SentenceAudio: '[sound:from-yomitan.mp3]', MiscInfo: 'mine' },
    });
    // A line without a voice, of a game that does not say its title.
    const fake = server([line(1, '窓の外では雨が降っていた。', { HasAudio: false })], {
      settings: serverSettings(),
      anki,
    });
    const screen = await open(fake);

    await screen.findAll('.line .check')[0]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-to-newest"]').trigger('click');
    await settle();

    expect([...anki.notes.values()][0]!.fields).toMatchObject({
      Sentence: '窓の外では雨が降っていた。',
      Picture: '<img src="vn-miner_1_picture.jpg">',
      SentenceAudio: '[sound:from-yomitan.mp3]',
      MiscInfo: 'mine',
    });
  });

  it('lets go of the lines of a new card, and keeps them when the card fails', async () => {
    const anki = createCollection();
    const choices = { anki: { deck: 'Mining', noteType: 'Basic', fields: { Front: 'sentence' } } };
    const fake = server(scene(), { settings: serverSettings(choices), anki });
    const screen = await open(fake);

    await screen.findAll('.line .check')[0]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="sending"]').text()).toContain('Made a new card');
    expect(screen.find('[data-testid="selected-count"]').exists()).toBe(false);
    expect(screen.find('[data-testid="undo"]').exists()).toBe(false);

    // Choosing lines again starts afresh: the note about the last card goes.
    await screen.findAll('.line .check')[0]!.trigger('click');
    await settle(300);
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('1 line selected');
    expect(screen.get('[data-testid="sending"]').text()).toBe('');

    // A card Anki refuses leaves the lines chosen, to try again.
    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="sending"]').text()).toContain(
      'This deck has a card like this already.',
    );
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('1 line selected');
    expect(anki.notes.size).toBe(1);
  });

  it('makes a new card in the chosen deck, and refuses a card made too long ago', async () => {
    // In the afternoon, so that the card of an hour ago was made today.
    vi.setSystemTime(new Date(2026, 10, 24, 15, 0));
    const anki = createCollection();
    anki.makeNote({
      id: Date.now() - 60 * 60_000,
      deck: 'Mining',
      modelName: 'Lapis',
      fields: { Expression: '昨日' },
    });
    const choices = {
      anki: {
        deck: 'Mining',
        noteType: 'Lapis',
        fields: { Expression: 'sentence', Sentence: 'sentence', Picture: 'picture' },
        tags: ['mined'],
        newestWithinMinutes: 15,
      },
    };
    const fake = server(scene(), { settings: serverSettings(choices), anki });
    const screen = await open(fake);

    await screen.findAll('.line .check')[0]!.trigger('click');
    await settle(300);
    // The card made an hour ago is not taken: the page says so before anything is pressed.
    expect(screen.get('[data-testid="newest-card"]').text()).toContain(
      'Your newest card was made 60 minutes ago',
    );
    expect(screen.get('[data-testid="add-to-newest"]').attributes('disabled')).toBeDefined();
    await screen.get('[data-testid="add-to-newest"]').trigger('click');
    await settle();
    expect(anki.calls.map((call) => call.action)).not.toContain('updateNoteFields');

    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    const added = [...anki.notes.values()].at(-1)!;
    expect(added).toMatchObject({
      deck: 'Mining',
      modelName: 'Lapis',
      tags: ['mined'],
      fields: { Expression: '昨日の話、覚えてる？', Sentence: '昨日の話、覚えてる？' },
    });
    expect(added.fields.Picture).toBe('<img src="vn-miner_1_picture.jpg">');
    // Nothing asks for the sound, so none is made.
    expect(fake.asked('Lines/Audio/Anki')).toHaveLength(0);
    expect(screen.get('[data-testid="sending"]').text()).toContain(
      'Made a new card, "昨日の話、覚えてる？".',
    );
    expect(screen.find('[data-testid="undo"]').exists()).toBe(false);
  });

  it('keeps lines chosen while the card was on its way, for the next card', async () => {
    const anki = createCollection();
    const choices = { anki: { deck: 'Mining', noteType: 'Basic', fields: { Front: 'sentence' } } };
    let release = () => {};
    const until = new Promise<void>((resolve) => (release = resolve));
    const fake = server(scene(), {
      settings: serverSettings(choices),
      anki,
      holdAnki: { action: 'addNote', until },
    });
    const screen = await open(fake);

    const checks = screen.findAll('.line .check');
    await checks[0]!.trigger('click');
    await settle(300);
    await screen.get('[data-testid="add-new"]').trigger('click');
    await settle();
    await checks[2]!.trigger('click');
    await settle(300);
    release();
    await settle();

    expect([...anki.notes.values()][0]!.fields.Front).toBe('昨日の話、覚えてる？');
    expect(screen.get('[data-testid="sending"]').text()).toContain('Made a new card');
    expect(screen.get('[data-testid="selected-count"]').text()).toBe('1 line selected');
    expect(checks[2]!.attributes('aria-checked')).toBe('true');
  });

  it('syncs Anki with AnkiWeb from the bar, and says how it went', async () => {
    const anki = createCollection();
    const fake = server(scene(), { settings: serverSettings(), anki });
    const screen = await open(fake);
    await settle();

    const button = screen.get('[data-testid="sync-anki"]');
    expect(button.attributes('aria-label')).toBe('Sync Anki now');
    await button.trigger('click');
    expect(screen.get('[data-testid="sync-status"]').text()).toBe('Syncing Anki with AnkiWeb…');
    expect(button.attributes('disabled')).toBeDefined();
    await settle();
    expect(anki.sync.count).toBe(1);
    expect(screen.get('[data-testid="sync-status"]').text()).toContain(
      'Anki synced: AnkiWeb and your Anki already had the same cards.',
    );
    expect(button.attributes('disabled')).toBeUndefined();
    // The note goes by itself after a while.
    await settle(6_000);
    expect(screen.find('[data-testid="sync-status"]').exists()).toBe(false);

    // A sync that fails says why, and stays until it is closed.
    anki.sync.error = 'A synchronization is already in progress';
    await screen.get('[data-testid="sync-anki"]').trigger('click');
    await settle();
    const note = screen.get('[data-testid="sync-status"]');
    expect(note.attributes('role')).toBe('alert');
    expect(note.text()).toContain('Anki is syncing with AnkiWeb already.');
    await settle(60_000);
    await screen.get('[data-testid="close-sync-status"]').trigger('click');
    expect(screen.find('[data-testid="sync-status"]').exists()).toBe(false);
  });

  it('offers to set up Anki when cards go nowhere, and opens the settings', async () => {
    const fake = server(scene(), { settings: settings() });
    const screen = await open(fake);
    await settle();
    expect(screen.find('[data-testid="sync-anki"]').exists()).toBe(false);

    await screen.findAll('.line .check')[0]!.trigger('click');
    expect(screen.find('[data-testid="add-to-newest"]').exists()).toBe(false);
    await screen.get('[data-testid="set-up-anki"]').trigger('click');
    await settle();
    expect(screen.find('[data-testid="where-off"]').exists()).toBe(true);
    expect(screen.get('[data-testid="open-settings"]').attributes('aria-pressed')).toBe('true');

    await screen.get('[data-testid="settings-back"]').trigger('click');
    expect(screen.findAll('.line .check')).not.toHaveLength(0);
  });

  it('is empty at once when the stream is emptied in the settings', async () => {
    const fake = server(scene(), { settings: settings() });
    const screen = await open(fake);
    await settle();

    await screen.get('[data-testid="open-settings"]').trigger('click');
    await settle();
    await screen.get('[data-testid="empty-stream"]').trigger('click');
    await screen.get('[data-testid="empty-stream-yes"]').trigger('click');
    await settle();
    expect(screen.get('[data-testid="emptied"]').text()).toBe('The stream is empty.');

    await screen.get('[data-testid="settings-back"]').trigger('click');
    expect(screen.get('[data-testid="no-lines"] h1').text()).toBe('No lines yet');
  });

  it('tells the app when the sign-in has ended', async () => {
    const fake = fakeRoutes(() => problem(401, "Sign in with the server's API key."));
    page = mount(MainScreen, { props: { env: env(fake.fetch), session } });
    await settle();

    expect(page.emitted('signedOut')).toHaveLength(1);
    // Nothing is tried again: the app asks for the key.
    await settle(60_000);
    expect(fake.sent.filter((request) => request.path === 'Lines')).toHaveLength(1);
  });

  it('closes the connection when the page is closed', async () => {
    const fake = server();
    const screen = await open(fake);
    screen.unmount();
    page = undefined;

    expect(fake.asked('Events')[0]!.signal?.aborted).toBe(true);
  });
});
