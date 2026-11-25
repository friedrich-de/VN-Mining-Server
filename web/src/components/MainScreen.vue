<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';

import {
  addNew,
  addToNewest,
  type CardContent,
  type CardMedia,
  type CardOutcome,
  newCardProblem,
  newestCard,
  undoUpdate,
  wordsInDeck,
} from '@/anki/cards';
import { AnkiError, type AnkiConnect, deviceAnki, serverAnki } from '@/anki/connect';
import { readPageSettings } from '@/anki/pageSettings';
import { syncAnki } from '@/anki/sync';
import { HttpError, UnreachableError } from '@/api/http';
import { createServerApi, type PictureOptions, type SoundOptions } from '@/api/server';
import type { DictionariesView, LookupResult, SettingsView, StreamLine } from '@/api/types';
import type { AuthEnv, Session } from '@/auth/session';
import type { Box } from '@/dictionary/caret';
import type { WordRequest } from '@/dictionary/popup';
import { wordOf } from '@/dictionary/word';
import { createLineFeed, type FeedStatus } from '@/stream/feed';
import { applyNews, clockOf, gameOf, type StreamNews, withEarlier } from '@/stream/lines';

import AccountMenu from './AccountMenu.vue';
import type { Making, Newest, Sending } from './making';
import SelectionBar from './SelectionBar.vue';
import SettingsScreen from './SettingsScreen.vue';
import TranscriptView from './TranscriptView.vue';
import WordPopup from './WordPopup.vue';

const props = defineProps<{ env: AuthEnv; session: Session }>();
const emit = defineEmits<{
  /** The server no longer takes the sign-in. */
  signedOut: [];
  /** The user asked to sign out. */
  signOut: [];
}>();

/** The language of the games' text, for the browser's fonts and line breaks. */
const LANGUAGE = 'ja';

/** How many lines the page reads at a time: the newest when it opens, and each step back. */
const PAGE_OF_LINES = 300;

/** The most lines whose voices the server joins into one sound. */
const MAX_SOUND_LINES = 30;

const api = createServerApi(props.env.fetch, props.env.base);

function messageOf(error: unknown): string {
  if (error instanceof HttpError) {
    if (error.status === 401) emit('signedOut');
    return error.message;
  }

  if (error instanceof UnreachableError) return 'The server could not be reached.';
  return error instanceof Error ? error.message : String(error);
}

// The settings, kept on the server, and where the cards go.

const settings = shallowRef<SettingsView | null>(null);
const view = ref<'lines' | 'settings'>('lines');
const pageSettings = computed(() => readPageSettings(settings.value?.Page));
const anki = computed<AnkiConnect | null>(() => {
  const value = settings.value?.Anki;
  if (value?.Where === 'device') {
    return deviceAnki(props.env.fetch, value.DeviceAddress, value.DeviceKey, api);
  }
  return value?.Where === 'server' ? serverAnki(api) : null;
});

async function loadSettings(): Promise<boolean> {
  try {
    settings.value = await api.settings();
    return true;
  } catch {
    // The page works without them; the feed tells when the sign-in has ended.
    return false;
  }
}

async function openSettings(): Promise<void> {
  if (settings.value === null && !(await loadSettings())) return;
  closeWord();
  view.value = 'settings';
}

// The server's dictionaries, for looking words up.

const dictionaries = shallowRef<DictionariesView | null>(null);

async function loadDictionaries(): Promise<void> {
  try {
    dictionaries.value = await api.dictionaries();
  } catch {
    // Without them words are not looked up; the rest of the page works.
  }
}

const enabledDictionaries = computed(
  () => dictionaries.value?.Dictionaries.filter((dictionary) => dictionary.Enabled) ?? [],
);
/** Whether the server has an audio source for words. */
const wordAudio = computed(() => dictionaries.value?.WordAudio?.Set === true);
const lookingUp = computed(
  () => pageSettings.value.dictionary.lookUp && enabledDictionaries.value.length > 0,
);

function closeSettings(): void {
  view.value = 'lines';
  // Dictionaries may have been added or arranged there.
  void loadDictionaries();
}

// The game's lines: the newest ones read when the page opens, then followed as the game sends them.

type Loaded = { kind: 'loading' } | { kind: 'ready' } | { kind: 'failed'; message: string };

const status = ref<FeedStatus>('connecting');
const loaded = shallowRef<Loaded>({ kind: 'loading' });
const lines = shallowRef<readonly StreamLine[]>([]);
/**
 * The number of the oldest line the server keeps, as far as the page knows: 0 when it only knows
 * that there are older ones than it holds.
 */
const oldest = ref<number | undefined>(undefined);

function onNews(news: StreamNews): void {
  lines.value = applyNews(lines.value, news);
  if (news.kind === 'hello') oldest.value = news.hello.Total === 0 ? undefined : news.hello.Oldest;
  else if (news.kind === 'removed') oldest.value = news.upTo + 1;
  else if (news.kind === 'cleared') oldest.value = undefined;
  else if (oldest.value === undefined) oldest.value = news.line.Id;
}

const feed = createLineFeed(
  {
    api,
    setTimeout: (callback, ms) => window.setTimeout(callback, ms),
    clearTimeout: (handle) => window.clearTimeout(handle),
  },
  {
    onNews,
    onStatus: (next) => {
      status.value = next;
      if (next === 'signed-out') emit('signedOut');
    },
  },
);

const newestId = computed(() => lines.value[lines.value.length - 1]?.Id);

function follow(): void {
  if (loaded.value.kind === 'ready' && document.visibilityState !== 'hidden') {
    feed.start(newestId.value ?? 0);
  }
}

let loading = 0;
let loadAgain: number | null = null;

/** Reads the newest lines. Until that worked it is tried again by itself, as the feed is. */
async function loadLines(): Promise<void> {
  const mine = (loading += 1);
  if (loadAgain !== null) window.clearTimeout(loadAgain);
  loadAgain = null;
  try {
    const found = await api.lines({ limit: PAGE_OF_LINES });
    if (mine !== loading) return;
    lines.value = found.Lines;
    oldest.value =
      found.Lines.length === 0
        ? undefined
        : found.Total > found.Lines.length
          ? 0
          : found.Lines[0]!.Id;
    loaded.value = { kind: 'ready' };
    follow();
  } catch (error) {
    if (mine !== loading) return;
    loaded.value = { kind: 'failed', message: messageOf(error) };
    if (!(error instanceof HttpError && error.status === 401)) {
      loadAgain = window.setTimeout(() => void loadLines(), 5_000);
    }
  }
}

// Older lines than the page holds, read a step at a time when asked for.

const hasEarlier = computed(() => {
  const first = lines.value[0];
  return first !== undefined && oldest.value !== undefined && oldest.value < first.Id;
});
const earlier = shallowRef<{ kind: 'idle' | 'busy' } | { kind: 'failed'; message: string }>({
  kind: 'idle',
});

async function showEarlier(): Promise<void> {
  const first = lines.value[0];
  if (first === undefined || earlier.value.kind === 'busy') return;
  earlier.value = { kind: 'busy' };
  following.value = false;
  try {
    const found = await api.lines({ before: first.Id, limit: PAGE_OF_LINES });
    // The lines in view stay where they are: the page grows upwards.
    const height = document.documentElement.scrollHeight;
    lines.value = withEarlier(lines.value, found.Lines);
    // Fewer than asked for: these were the oldest the server keeps.
    if (found.Lines.length < PAGE_OF_LINES) oldest.value = lines.value[0]?.Id;
    earlier.value = { kind: 'idle' };
    await nextTick();
    window.scrollBy?.({ top: document.documentElement.scrollHeight - height, behavior: 'instant' });
  } catch (error) {
    earlier.value = { kind: 'failed', message: messageOf(error) };
  }
}

const game = computed(() => gameOf(lines.value));
const newestLine = computed(() => lines.value[lines.value.length - 1]);

const where = computed(() => {
  if (loaded.value.kind !== 'ready') return '';
  const parts: string[] = [];
  if (status.value === 'live') parts.push('Live');
  else if (status.value === 'retrying') parts.push('Not connected');
  else parts.push('Connecting');
  const newest = newestLine.value;
  parts.push(newest === undefined ? 'no lines yet' : `newest line ${clockOf(newest.At)}`);
  return parts.join(' · ');
});

// Following: the newest line stays in view until the user scrolls away.

const following = ref(true);

function userScrolled(): void {
  following.value = false;
}

/** Follows the game again, from its newest line. */
function goToNewest(): void {
  closeWord();
  following.value = true;
}

const SCROLL_KEYS = new Set(['PageUp', 'PageDown', 'Home', 'End', 'ArrowUp', 'ArrowDown']);

function onKey(event: KeyboardEvent): void {
  const target = event.target;
  if (target instanceof Element && target.closest('input, select, textarea') !== null) return;
  if (SCROLL_KEYS.has(event.key)) userScrolled();
}

// Choosing lines.

const selected = ref<Set<number>>(new Set());
let rangeFrom: number | undefined;

/**
 * Chooses lines as one run in a row, since a card's sound is one piece: a line next to the run
 * joins it, a line apart from it starts a new run, and Shift takes every line from the last one
 * chosen to this one. A line at either end of the run leaves it; one inside it starts the run anew.
 */
function toggle(id: number, range: boolean): void {
  const ids = lines.value.map((line) => line.Id);
  const at = ids.indexOf(id);
  if (at === -1) return;
  const run = ids.flatMap((lineId, index) => (selected.value.has(lineId) ? [index] : []));
  let next: number[];
  if (range && rangeFrom !== undefined && ids.includes(rangeFrom)) {
    const ends = [...run, ids.indexOf(rangeFrom), at];
    next = ids.slice(Math.min(...ends), Math.max(...ends) + 1);
  } else if (run.length === 0) {
    next = [id];
  } else {
    const first = Math.min(...run);
    const last = Math.max(...run);
    if (at === first || at === last) {
      next = selected.value.has(id)
        ? ids.slice(first, last + 1).filter((lineId) => lineId !== id)
        : [id];
    } else if (at > first && at < last) {
      next = [id];
    } else if (at === first - 1 || at === last + 1) {
      next = ids.slice(Math.min(first, at), Math.max(last, at) + 1);
    } else {
      next = [id];
    }
  }

  rangeFrom = id;
  selected.value = new Set(next);
  // Lines are chosen where the user reads, which a new line of the game must not move away from.
  if (next.length > 0) following.value = false;
}

function clear(): void {
  selected.value = new Set();
  rangeFrom = undefined;
}

const chosen = computed(() => lines.value.filter((line) => selected.value.has(line.Id)));

// A chosen line the server let go of is chosen no longer: nothing of it can go onto a card.
watch(lines, (now) => {
  if (selected.value.size === 0) return;
  const held = new Set(now.map((line) => line.Id));
  if ([...selected.value].every((id) => held.has(id))) return;
  selected.value = new Set([...selected.value].filter((id) => held.has(id)));
});

const spanText = computed(() => {
  const first = chosen.value[0];
  const last = chosen.value[chosen.value.length - 1];
  if (first === undefined || last === undefined) return '';
  const from = clockOf(first.At);
  const to = clockOf(last.At);
  return from === to ? from : `${from} to ${to}`;
});

// The preview: the picture and the sound the card would get, made once the choice settles.

const picture = shallowRef<Making>({ kind: 'waiting' });
const sound = shallowRef<Making>({ kind: 'waiting' });
const urls: string[] = [];
let making: AbortController | null = null;
let settle: number | null = null;

function release(): void {
  for (const url of urls.splice(0)) URL.revokeObjectURL(url);
}

async function make(
  into: typeof picture,
  cut: (signal: AbortSignal) => Promise<Blob>,
  signal: AbortSignal,
): Promise<void> {
  try {
    const blob = await cut(signal);
    if (signal.aborted) return;
    const url = URL.createObjectURL(blob);
    urls.push(url);
    into.value = { kind: 'ready', url };
  } catch (error) {
    if (!signal.aborted) into.value = { kind: 'failed', message: messageOf(error) };
  }
}

/** The picture of lines: the screenshot of the first of them that has one. */
function pictureFor(of: readonly StreamLine[]): PictureOptions | undefined {
  const line = of.find((candidate) => candidate.HasPicture);
  if (line === undefined) return undefined;
  const media = pageSettings.value.media;
  return {
    line: line.Id,
    format: media.pictureFormat,
    width: media.pictureWidth,
    quality: media.pictureQuality,
  };
}

/** The sound of lines: their voices one after the other, unless they are too many to join. */
function soundFor(of: readonly StreamLine[]): SoundOptions | undefined {
  const voiced = of.filter((line) => line.HasAudio).map((line) => line.Id);
  if (voiced.length === 0 || voiced.length > MAX_SOUND_LINES) return undefined;
  const media = pageSettings.value.media;
  return { lines: voiced, format: media.soundFormat, normalize: media.normalize };
}

/** Why lines have no sound, for the preview. */
function noSoundText(of: readonly StreamLine[]): string {
  return of.some((line) => line.HasAudio)
    ? `More than ${MAX_SOUND_LINES} lines with a voice. Choose fewer.`
    : of.length === 1
      ? 'This line has no voice.'
      : 'These lines have no voice.';
}

const pictureOptions = computed(() => pictureFor(chosen.value));
const soundOptions = computed(() => soundFor(chosen.value));

const previewKey = computed(() =>
  chosen.value.length === 0
    ? ''
    : JSON.stringify([
        chosen.value.map((line) => line.Id),
        pictureOptions.value,
        soundOptions.value,
      ]),
);

watch(previewKey, (key) => {
  if (settle !== null) window.clearTimeout(settle);
  making?.abort();
  making = null;
  release();
  picture.value = { kind: 'waiting' };
  sound.value = { kind: 'waiting' };
  if (key === '') return;

  const of = chosen.value;
  const pictureCut = pictureOptions.value;
  const soundCut = soundOptions.value;
  if (pictureCut === undefined) {
    picture.value = {
      kind: 'none',
      message: of.length === 1 ? 'This line has no screenshot.' : 'These lines have no screenshot.',
    };
  }
  if (soundCut === undefined) sound.value = { kind: 'none', message: noSoundText(of) };
  if (pictureCut === undefined && soundCut === undefined) return;

  settle = window.setTimeout(() => {
    settle = null;
    const controller = new AbortController();
    making = controller;
    if (pictureCut !== undefined) {
      void make(picture, (signal) => api.picture(pictureCut, signal), controller.signal);
    }
    if (soundCut !== undefined) {
      void make(sound, (signal) => api.sound(soundCut, signal), controller.signal);
    }
  }, 300);
});

// Sending the chosen lines to Anki.

const sending = shallowRef<Sending>({ kind: 'idle' });
let lastUpdate: { anki: AnkiConnect; outcome: CardOutcome } | null = null;
/**
 * Whether the lines of the card just made were let go of. What happened to the card stays in view
 * then, with its Undo, until other lines are chosen or it is closed.
 */
const madeCard = ref(false);

watch(previewKey, (key) => {
  if (key === '' && madeCard.value) return;
  madeCard.value = false;
  if (sending.value.kind !== 'busy') sending.value = { kind: 'idle' };
  lastUpdate = null;
});

function closeMade(): void {
  madeCard.value = false;
  sending.value = { kind: 'idle' };
  lastUpdate = null;
}

/** What a card of these lines gets: their text, and the game they are from. */
function contentOf(of: readonly StreamLine[]): CardContent {
  return {
    sentence: of.map((line) => line.Text).join('\n'),
    source: of.find((line) => line.Game !== undefined)?.Game ?? game.value ?? '',
  };
}

/** The picture and sound a card of these lines gets, where the lines have them. */
function mediaOf(of: readonly StreamLine[]): CardMedia {
  return { picture: pictureFor(of), sound: soundFor(of) };
}

async function send(mode: 'newest' | 'new'): Promise<void> {
  const target = anki.value;
  const of = chosen.value;
  if (target === null || of.length === 0) return;

  sending.value = {
    kind: 'busy',
    text: mode === 'newest' ? 'Adding to your newest card…' : 'Making a new card…',
  };
  lastUpdate = null;
  const content = contentOf(of);
  const media = mediaOf(of);
  // Every change of the lines chosen makes a new set, so this tells whether they are still the ones sent.
  const sent = selected.value;

  try {
    const outcome =
      mode === 'newest'
        ? await addToNewest(target, pageSettings.value, content, media, Date.now())
        : await addNew(target, pageSettings.value, content, media);
    const name = outcome.label === '' ? 'the card' : `"${outcome.label}"`;
    if (outcome.kind === 'updated') lastUpdate = { anki: target, outcome };
    sending.value = {
      kind: 'done',
      text: outcome.kind === 'updated' ? `Added to ${name}.` : `Made a new card, ${name}.`,
      canUndo: outcome.kind === 'updated',
    };
    // The lines are done with, so they are let go of, ready for the next card. Lines chosen while the
    // card was on its way are the next card's, and stay.
    if (selected.value === sent) {
      madeCard.value = true;
      clear();
    }
  } catch (error) {
    sending.value = { kind: 'failed', text: messageOf(error) };
  }
  void lookForNewest();
}

// The card "Add to my newest card" would change, looked for while lines are chosen: again every
// few seconds and when the page comes back into view, since the card is made in another window.

const newest = shallowRef<Newest>({ kind: 'looking' });
let newestTimer: number | null = null;
let newestAsked = 0;

async function lookForNewest(): Promise<void> {
  const target = anki.value;
  if (target === null || chosen.value.length === 0 || sending.value.kind === 'busy') return;
  const asked = (newestAsked += 1);
  try {
    const found = await newestCard(target, pageSettings.value, Date.now());
    if (asked !== newestAsked) return;
    newest.value = { kind: 'found', label: found.label, minutes: found.minutes };
  } catch (error) {
    if (asked !== newestAsked) return;
    newest.value =
      error instanceof AnkiError && !error.unreachable
        ? { kind: 'none', text: error.message }
        : { kind: 'failed', text: messageOf(error) };
  }
}

const followsNewest = computed(
  () => view.value === 'lines' && chosen.value.length > 0 && anki.value !== null,
);

function stopNewest(): void {
  if (newestTimer !== null) window.clearInterval(newestTimer);
  newestTimer = null;
  newestAsked += 1;
}

watch(
  [followsNewest, anki, pageSettings],
  ([follows], before) => {
    stopNewest();
    if (!follows) return;
    // Only a new look, not every line chosen, empties what is shown.
    if (before === undefined || !before[0]) newest.value = { kind: 'looking' };
    void lookForNewest();
    newestTimer = window.setInterval(() => void lookForNewest(), 10_000);
  },
  { immediate: true },
);

function onFocus(): void {
  if (followsNewest.value) void lookForNewest();
}

async function undo(): Promise<void> {
  const update = lastUpdate;
  if (update === null) return;
  lastUpdate = null;
  sending.value = { kind: 'busy', text: 'Putting the card back…' };
  try {
    await undoUpdate(update.anki, update.outcome);
    sending.value = { kind: 'done', text: 'The card is as it was.', canUndo: false };
  } catch (error) {
    sending.value = { kind: 'failed', text: messageOf(error) };
  }
}

// Syncing Anki with AnkiWeb now, from the bar, rather than when it next would by itself.

type Syncing = { kind: 'idle' } | { kind: 'busy' } | { kind: 'done' | 'failed'; text: string };

const syncing = shallowRef<Syncing>({ kind: 'idle' });
let syncShown: number | null = null;

function endSyncNote(): void {
  if (syncShown !== null) window.clearTimeout(syncShown);
  syncShown = null;
  syncing.value = { kind: 'idle' };
}

async function syncNow(): Promise<void> {
  const target = anki.value;
  if (target === null || syncing.value.kind === 'busy') return;
  endSyncNote();
  syncing.value = { kind: 'busy' };
  try {
    syncing.value = { kind: 'done', text: await syncAnki(target) };
    syncShown = window.setTimeout(endSyncNote, 6_000);
  } catch (error) {
    syncing.value = { kind: 'failed', text: messageOf(error) };
  }
}

// Looking a word up: a tap on it opens the popup, from which a card of it is made.

const wordRequest = shallowRef<WordRequest | null>(null);
/** The line and place of the word looked up, and how long the word found there is. */
const marked = ref<{ id: number; start: number; length: number } | undefined>(undefined);

function closeWord(): void {
  wordRequest.value = null;
  marked.value = undefined;
}

function onWord(id: number, at: { offset: number; box: Box } | null): void {
  const line = lines.value.find((candidate) => candidate.Id === id);
  if (at === null || line === undefined) {
    // A tap beside the words closes the popup, or else chooses the line as it does without one.
    if (wordRequest.value !== null) closeWord();
    else toggle(id, false);
    return;
  }
  marked.value = { id, start: at.offset, length: 0 };
  wordRequest.value = { text: line.Text.slice(at.offset, at.offset + 64), box: at.box };
}

function onFound(length: number): void {
  if (marked.value !== undefined) marked.value = { ...marked.value, length };
}

watch(view, closeWord);

/**
 * The lines a card of the word gets: the chosen lines when the word is in one of them, else its
 * own line; and where the word is in their text.
 */
function wordLines(): { of: readonly StreamLine[]; start: number } | undefined {
  const mark = marked.value;
  const line = lines.value.find((candidate) => candidate.Id === mark?.id);
  if (mark === undefined || line === undefined) return undefined;
  const of = selected.value.has(line.Id) ? chosen.value : [line];
  let start = mark.start;
  for (const before of of.slice(0, of.indexOf(line))) start += before.Text.length + 1;
  return { of, start };
}

function wordContent(
  result: LookupResult,
  inLine: boolean,
  selection = '',
): CardContent | undefined {
  const found = wordLines();
  if (found === undefined) return undefined;
  // A word looked up in a definition is not in the sentence to be picked out.
  const word = wordOf(result, inLine ? found.start : -1);
  return { ...contentOf(found.of), word: { ...word, selection } };
}

const wordCardProblem = computed(() =>
  anki.value === null
    ? 'Choose where your cards go in the settings to make cards of words.'
    : (newCardProblem(pageSettings.value, true) ?? ''),
);

async function addWordCard(
  result: LookupResult,
  inLine: boolean,
  selection: string,
  sound: string | undefined,
): Promise<string> {
  const target = anki.value;
  const found = wordLines();
  const content = wordContent(result, inLine, selection);
  if (target === null || found === undefined || content === undefined) {
    throw new Error('The line of this word is gone. Look the word up again.');
  }
  try {
    const media = {
      ...mediaOf(found.of),
      wordAudio: wordAudio.value,
      ...(sound === undefined ? {} : { wordAudioChoice: sound }),
    };
    const outcome = await addNew(target, pageSettings.value, content, media);
    const name = `"${outcome.label === '' ? result.term.expression : outcome.label}"`;
    return outcome.noWordAudio === true
      ? `Made a new card, ${name}, without the word's sound: the audio source has none for it.`
      : `Made a new card, ${name}.`;
  } catch (error) {
    throw error instanceof HttpError ? new Error(messageOf(error)) : error;
  }
}

async function wordsKnown(results: LookupResult[]): Promise<boolean[]> {
  const target = anki.value;
  if (target === null) return results.map(() => false);
  const contents = results.map((result) => wordContent(result, true));
  if (contents.some((content) => content === undefined)) return results.map(() => false);
  return wordsInDeck(target, pageSettings.value, contents as CardContent[]);
}

// The connection lives while the page is in view. Coming back to the page from the game shows
// the game's newest line, unless the user is in the middle of something.

function onVisibility(): void {
  if (document.visibilityState === 'hidden') {
    feed.stop();
    return;
  }
  if (chosen.value.length === 0 && wordRequest.value === null && view.value === 'lines') {
    following.value = true;
  }
  if (loaded.value.kind === 'ready') follow();
  else void loadLines();
  onFocus();
}

function onOnline(): void {
  if (loaded.value.kind === 'ready') feed.reconnectNow();
  else void loadLines();
}

/** The stream was emptied in the settings. The feed says so too, when it is connected. */
function onCleared(): void {
  onNews({ kind: 'cleared' });
  clear();
}

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibility);
  window.addEventListener('online', onOnline);
  window.addEventListener('keydown', onKey);
  window.addEventListener('focus', onFocus);
  void loadLines();
  void loadSettings();
  void loadDictionaries();
});

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibility);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('keydown', onKey);
  window.removeEventListener('focus', onFocus);
  stopNewest();
  if (syncShown !== null) window.clearTimeout(syncShown);
  feed.stop();
  loading += 1;
  if (loadAgain !== null) window.clearTimeout(loadAgain);
  if (settle !== null) window.clearTimeout(settle);
  making?.abort();
  release();
});
</script>

<template>
  <div class="screen">
    <header class="bar">
      <span class="brand">VN Miner</span>
      <div v-if="loaded.kind === 'ready'" class="playing-now">
        <p class="title" data-testid="title">{{ game ?? 'Visual novel' }}</p>
        <p class="where muted" data-testid="where">{{ where }}</p>
      </div>
      <div class="bar-end">
        <button
          v-if="anki !== null"
          class="button icon"
          :class="{ turning: syncing.kind === 'busy' }"
          type="button"
          aria-label="Sync Anki now"
          title="Sync Anki now"
          data-testid="sync-anki"
          :disabled="syncing.kind === 'busy'"
          :aria-busy="syncing.kind === 'busy'"
          @click="syncNow"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path
              fill="currentColor"
              d="M12 4a8 8 0 0 0-7.4 5h2.2A6 6 0 0 1 17.7 8.3L15 11h7V4l-2.9 2.9A8 8 0 0 0 12 4Zm7.4 11h-2.2A6 6 0 0 1 6.3 15.7L9 13H2v7l2.9-2.9A8 8 0 0 0 19.4 15Z"
            />
          </svg>
        </button>
        <button
          class="button icon"
          type="button"
          aria-label="Settings"
          title="Settings"
          data-testid="open-settings"
          :aria-pressed="view === 'settings'"
          @click="view === 'settings' ? (view = 'lines') : openSettings()"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path
              fill="currentColor"
              d="M19.4 13a7.5 7.5 0 0 0 0-2l2.1-1.6-2-3.5-2.5 1a7.3 7.3 0 0 0-1.7-1L15 3.3h-4l-.4 2.6a7.3 7.3 0 0 0-1.7 1l-2.5-1-2 3.5L6.6 11a7.5 7.5 0 0 0 0 2l-2.1 1.6 2 3.5 2.5-1c.5.4 1.1.7 1.7 1l.4 2.6h4l.4-2.6c.6-.3 1.2-.6 1.7-1l2.5 1 2-3.5ZM13 15.5A3.5 3.5 0 1 1 13 8.5a3.5 3.5 0 0 1 0 7Z"
              transform="translate(-1 0)"
            />
          </svg>
        </button>
        <AccountMenu :session="session" @sign-out="emit('signOut')" />
      </div>
    </header>

    <SettingsScreen
      v-if="view === 'settings' && settings !== null"
      :api="api"
      :fetch="env.fetch"
      :base="env.base"
      :settings="settings"
      @close="closeSettings"
      @saved="(next) => (settings = next)"
      @cleared="onCleared"
    />

    <p
      v-if="syncing.kind !== 'idle'"
      class="sync-note"
      :class="{ error: syncing.kind === 'failed' }"
      :role="syncing.kind === 'failed' ? 'alert' : 'status'"
      data-testid="sync-status"
    >
      <span>{{ syncing.kind === 'busy' ? 'Syncing Anki with AnkiWeb…' : syncing.text }}</span>
      <button
        v-if="syncing.kind !== 'busy'"
        class="link"
        type="button"
        data-testid="close-sync-status"
        @click="endSyncNote"
      >
        Close
      </button>
    </p>

    <p
      v-if="status === 'retrying' && loaded.kind === 'ready'"
      class="banner"
      role="status"
      data-testid="retrying"
    >
      The connection to the server broke. Trying again…
    </p>

    <div v-if="view === 'lines' && loaded.kind === 'ready' && lines.length > 0" class="toolbar">
      <div class="switches">
        <button
          class="switch"
          type="button"
          role="switch"
          data-testid="follow"
          :aria-checked="following"
          @click="following = !following"
        >
          <span class="knob" aria-hidden="true" />
          Follow the game
        </button>
      </div>
    </div>

    <main
      v-if="view === 'lines'"
      class="main"
      @wheel.passive="userScrolled"
      @touchmove.passive="userScrolled"
    >
      <section v-if="loaded.kind === 'loading'" class="note" role="status">
        <p class="muted">Reading the game's lines…</p>
      </section>

      <section v-else-if="loaded.kind === 'failed'" class="note" role="alert">
        <h1>The game's lines could not be read</h1>
        <p class="muted">{{ loaded.message }}</p>
        <p><button class="button" type="button" @click="loadLines">Try again</button></p>
      </section>

      <section v-else-if="lines.length === 0" class="note" data-testid="no-lines">
        <h1>No lines yet</h1>
        <p class="muted">
          Put the file mining.txt into the game's folder and start the game. Each line the game
          shows then comes up here by itself, with its voice and a screenshot.
        </p>
        <p>
          <button class="button" type="button" data-testid="get-game-file" @click="openSettings">
            Get mining.txt in the settings
          </button>
        </p>
      </section>

      <template v-else>
        <p v-if="hasEarlier" class="earlier">
          <button
            class="button"
            type="button"
            data-testid="show-earlier"
            :disabled="earlier.kind === 'busy'"
            @click="showEarlier"
          >
            {{ earlier.kind === 'busy' ? 'Reading…' : 'Show earlier lines' }}
          </button>
          <span v-if="earlier.kind === 'failed'" class="error small" role="alert">
            {{ earlier.message }}
          </span>
        </p>
        <TranscriptView
          :lines="lines"
          :selected="selected"
          :anchor="newestId"
          :follow="following && wordRequest === null"
          :lang="LANGUAGE"
          :look-up="lookingUp"
          :marked="marked"
          @toggle="toggle"
          @word="onWord"
        />
      </template>
    </main>

    <WordPopup
      v-if="lookingUp && view === 'lines'"
      :api="api"
      :dictionaries="dictionaries?.Dictionaries ?? []"
      :reload="loadDictionaries"
      :request="wordRequest"
      :add-card="wordCardProblem === '' ? addWordCard : undefined"
      :no-cards="wordCardProblem"
      :in-deck="wordCardProblem === '' ? wordsKnown : undefined"
      :word-audio="wordAudio"
      :look="pageSettings.dictionary"
      @found="onFound"
      @close="closeWord"
    />

    <div v-if="view === 'lines'" class="dock">
      <button
        v-if="!following && newestId !== undefined && loaded.kind === 'ready'"
        class="button now"
        type="button"
        data-testid="go-to-newest"
        @click="goToNewest"
      >
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path
            fill="currentColor"
            d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8m8.94 3A9 9 0 0 0 13 3.06V1h-2v2.06A9 9 0 0 0 3.06 11H1v2h2.06A9 9 0 0 0 11 20.94V23h2v-2.06A9 9 0 0 0 20.94 13H23v-2zM12 19a7 7 0 1 1 0-14 7 7 0 0 1 0 14"
          />
        </svg>
        Go to the newest line
      </button>
      <footer v-if="chosen.length > 0 || madeCard" class="bottom">
        <SelectionBar
          v-if="chosen.length > 0"
          :count="chosen.length"
          :span="spanText"
          :picture="picture"
          :sound="sound"
          :anki-ready="anki !== null"
          :sending="sending"
          :newest="newest"
          @clear="clear"
          @newest="send('newest')"
          @add="send('new')"
          @undo="undo"
          @settings="openSettings"
        />
        <p
          v-else
          class="made"
          :class="{ error: sending.kind === 'failed' }"
          :role="sending.kind === 'failed' ? 'alert' : 'status'"
          data-testid="sending"
        >
          <span v-if="sending.kind !== 'idle'">{{ sending.text }}</span>
          <button
            v-if="sending.kind === 'done' && sending.canUndo"
            class="link"
            type="button"
            data-testid="undo"
            @click="undo"
          >
            Undo
          </button>
          <button
            v-if="sending.kind !== 'busy'"
            class="link"
            type="button"
            data-testid="close-sent"
            @click="closeMade"
          >
            Close
          </button>
        </p>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.screen {
  display: flex;
  flex-direction: column;
  min-height: 100%;
}

.bar {
  position: sticky;
  z-index: 10;
  top: 0;
  display: flex;
  align-items: center;
  gap: 8px 16px;
  min-height: 64px;
  padding: 8px max(16px, env(safe-area-inset-right)) 8px max(16px, env(safe-area-inset-left));
  padding-top: max(8px, env(safe-area-inset-top));
  background: var(--surface);
}

.brand {
  flex: 0 0 auto;
  font-size: 18px;
  font-weight: 600;
  color: var(--text-strong);
}

.playing-now {
  flex: 1 1 auto;
  min-width: 0;
}

.title {
  overflow: hidden;
  font-weight: 600;
  color: var(--text-strong);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.where {
  overflow: hidden;
  font-size: 13px;
  font-variant-numeric: tabular-nums;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-end {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
}

.icon {
  width: 44px;
  padding: 0;
  background: transparent;
}

.icon[aria-pressed='true'] {
  background: var(--raised);
}

.banner {
  padding: 8px 16px;
  background: var(--raised);
  font-size: 13px;
  text-align: center;
}

.sync-note {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px 12px;
  flex-wrap: wrap;
  padding: 4px 16px;
  background: var(--raised);
  font-size: 13px;
  text-align: center;
}

.sync-note.error {
  color: var(--danger);
}

.made {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 16px;
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  font-size: 14px;
}

.made.error {
  color: var(--danger);
}

.made .link,
.sync-note .link {
  padding: 4px 0;
  border: 0;
  background: none;
  color: var(--text);
  text-decoration: underline;
  cursor: pointer;
}

.turning svg {
  animation: turn 1s linear infinite;
}

@keyframes turn {
  to {
    transform: rotate(360deg);
  }
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 8px 12px;
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  padding: 12px 16px 4px;
}

.earlier {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 8px 16px;
  padding: 12px 16px 0;
}

.earlier .error {
  color: var(--danger);
  font-size: 13px;
}

.switches {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-height: 44px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--text);
  cursor: pointer;
}

.knob {
  position: relative;
  width: 36px;
  height: 20px;
  border-radius: 10px;
  background: var(--raised-hover);
}

.knob::after {
  content: '';
  position: absolute;
  top: 3px;
  left: 3px;
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: var(--text-muted);
  transition: transform 120ms ease;
}

.switch[aria-checked='true'] .knob {
  background: var(--accent);
}

.switch[aria-checked='true'] .knob::after {
  background: var(--on-accent);
  transform: translateX(16px);
}

.main {
  flex: 1 1 auto;
}

.note {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 100%;
  max-width: 560px;
  margin: 0 auto;
  padding: 32px 16px;
}

.note h1 {
  font-size: 22px;
  font-weight: 600;
  color: var(--text-strong);
}

.dock {
  position: sticky;
  z-index: 10;
  bottom: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  /* Only its button and bar take taps; the lines behind the space between stay reachable. */
  pointer-events: none;
}

.dock > * {
  pointer-events: auto;
}

.now {
  margin: 0 max(16px, env(safe-area-inset-right)) 12px 16px;
  border-radius: 999px;
  box-shadow: 0 2px 10px rgb(0 0 0 / 0.5);
}

.bottom {
  align-self: stretch;
  padding: 12px max(16px, env(safe-area-inset-right)) 12px max(16px, env(safe-area-inset-left));
  padding-bottom: max(12px, env(safe-area-inset-bottom));
  background: var(--surface);
  box-shadow: 0 -1px 0 var(--border);
}

@media (width <= 600px) {
  .brand {
    display: none;
  }

  .bar {
    min-height: 56px;
  }

  .toolbar {
    padding-top: 8px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .knob::after {
    transition: none;
  }

  .turning svg {
    animation: none;
  }
}
</style>
