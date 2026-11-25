<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue';

import type { StreamLine } from '@/api/types';
import { type Box, characterAt, startsWord } from '@/dictionary/caret';
import { clockOf } from '@/stream/lines';

/** Part of a line, as the said text is drawn: the word looked up apart from the rest. */
interface Part {
  text: string;
  word: boolean;
}

const props = defineProps<{
  lines: readonly StreamLine[];
  selected: ReadonlySet<number>;
  /** The newest line, which the page keeps in view while it follows the game. */
  anchor: number | undefined;
  follow: boolean;
  lang: string | undefined;
  /** A tap on a word looks it up, instead of choosing the line. */
  lookUp: boolean;
  /** The word looked up, picked out in its line. */
  marked: { id: number; start: number; length: number } | undefined;
}>();

const emit = defineEmits<{
  toggle: [id: number, range: boolean];
  /** A tap on the said text while words are looked up: the character tapped, or none. */
  word: [id: number, at: { offset: number; box: Box } | null];
}>();

const list = useTemplateRef<HTMLOListElement>('list');

function scrollToAnchor(instant: boolean): void {
  if (props.anchor === undefined || list.value === null) return;
  const row = list.value.querySelector<HTMLElement>(`[data-line="${props.anchor}"]`);
  if (row === null || typeof row.scrollIntoView !== 'function') return;
  const still =
    instant || (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false);
  row.scrollIntoView({ block: 'center', behavior: still ? 'instant' : 'smooth' });
}

// While following, the newest line stays in view: the first time at once, then moving there.
watch(
  () => [props.anchor, props.follow] as const,
  async ([anchor, follow], before) => {
    if (!follow || anchor === undefined) return;
    await nextTick();
    scrollToAnchor(before === undefined || before[0] === undefined || before[1] !== follow);
  },
  { immediate: true },
);

function parts(line: StreamLine): Part[] {
  const mark = props.marked;
  if (mark?.id !== line.Id || mark.length <= 0) return [{ text: line.Text, word: false }];
  const end = mark.start + mark.length;
  return [
    { text: line.Text.slice(0, mark.start), word: false },
    { text: line.Text.slice(mark.start, end), word: true },
    { text: line.Text.slice(end), word: false },
  ].filter((part) => part.text !== '');
}

function onRowClick(event: MouseEvent, line: StreamLine): void {
  // A click that ends a text selection is for copying, not for choosing the line.
  const chosen = window.getSelection?.()?.toString() ?? '';
  if (chosen !== '') return;
  const said = event.target instanceof Element ? event.target.closest('.said') : null;
  if (props.lookUp && said !== null) {
    const at = characterAt(said, event.clientX, event.clientY);
    emit('word', line.Id, at !== undefined && startsWord(line.Text[at.offset]) ? at : null);
    return;
  }
  emit('toggle', line.Id, event.shiftKey);
}

// Holding Shift over a word looks it up, as Yomitan scans with Shift: the mouse moved with Shift
// held, or Shift pressed with the mouse over the word.

/** Where the mouse is over the lines, for Shift pressed without moving it. */
let pointer: { x: number; y: number } | undefined;
/** The character looked up last, so moving within it asks nothing new. */
let scanned: string | undefined;

function scan(target: EventTarget | null, x: number, y: number): void {
  if (!props.lookUp || !(target instanceof Element)) return;
  const said = target.closest('.said');
  const row = said?.closest<HTMLElement>('[data-line]');
  if (said === null || said === undefined || row === null || row === undefined) return;
  const id = Number(row.dataset.line);
  const line = props.lines.find((candidate) => candidate.Id === id);
  const at = line === undefined ? undefined : characterAt(said, x, y);
  // Beside a word nothing happens: the popup stays as it is.
  if (line === undefined || at === undefined || !startsWord(line.Text[at.offset])) return;
  const key = `${id}:${at.offset}`;
  if (key === scanned) return;
  scanned = key;
  emit('word', id, at);
}

function onMove(event: MouseEvent): void {
  pointer = { x: event.clientX, y: event.clientY };
  if (event.shiftKey) scan(event.target, event.clientX, event.clientY);
  else scanned = undefined;
}

function onLeave(): void {
  pointer = undefined;
  scanned = undefined;
}

function onKey(event: KeyboardEvent): void {
  if (event.key !== 'Shift' || event.repeat || pointer === undefined) return;
  if (event.target instanceof Element && event.target.closest('input, textarea, select') !== null) {
    return;
  }
  scanned = undefined;
  scan(document.elementFromPoint(pointer.x, pointer.y), pointer.x, pointer.y);
}

onMounted(() => window.addEventListener('keydown', onKey));
onBeforeUnmount(() => window.removeEventListener('keydown', onKey));
</script>

<template>
  <ol
    ref="list"
    class="lines"
    aria-label="The game's lines"
    @mousemove="onMove"
    @mouseleave="onLeave"
  >
    <li
      v-for="line in lines"
      :key="line.Id"
      class="line"
      :class="{ selected: selected.has(line.Id), current: line.Id === anchor }"
      :data-line="line.Id"
      :aria-current="line.Id === anchor ? 'true' : undefined"
    >
      <button
        class="check"
        type="button"
        role="checkbox"
        :aria-checked="selected.has(line.Id)"
        :aria-label="`Line of ${clockOf(line.At)}`"
        @click="emit('toggle', line.Id, $event.shiftKey)"
      >
        <span class="box">
          <svg v-if="selected.has(line.Id)" viewBox="0 0 24 24" width="18" height="18">
            <path fill="currentColor" d="m9.5 16.2-4-4L4 13.7l5.5 5.5L20 8.7l-1.5-1.5z" />
          </svg>
        </span>
      </button>
      <span class="time">
        <span>{{ clockOf(line.At) }}</span>
        <span class="has">
          <svg
            v-if="line.HasAudio"
            viewBox="0 0 24 24"
            width="14"
            height="14"
            role="img"
            aria-label="With voice"
          >
            <title>With voice</title>
            <path
              fill="currentColor"
              d="M3 9v6h4l5 5V4L7 9zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4"
            />
          </svg>
          <svg
            v-if="line.HasPicture"
            viewBox="0 0 24 24"
            width="14"
            height="14"
            role="img"
            aria-label="With screenshot"
          >
            <title>With screenshot</title>
            <path
              fill="currentColor"
              d="M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2M8.5 13.5l2.5 3 3.5-4.5 4.5 6H5z"
            />
          </svg>
        </span>
      </span>
      <div class="text" :class="{ words: lookUp }" @click="onRowClick($event, line)">
        <p v-if="line.Name !== undefined" class="speaker" :lang="lang">{{ line.Name }}</p>
        <p class="said" :lang="lang" :data-word-text="lookUp ? '' : undefined">
          <!-- Each part as it is, with no space of the template's around it. -->
          <span
            v-for="(part, index) in parts(line)"
            :key="index"
            :class="{ word: part.word }"
            v-text="part.text"
          />
        </p>
      </div>
    </li>
  </ol>
</template>

<style scoped>
.lines {
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
  padding: 8px 0;
  list-style: none;
}

.line {
  display: grid;
  grid-template-columns: 44px 64px 1fr;
  align-items: start;
  gap: 0 8px;
  padding: 10px 16px 10px 8px;
  border-radius: var(--radius);
}

.line.selected {
  background: var(--surface);
}

.line.current {
  background: color-mix(in srgb, var(--accent) 22%, var(--bg));
}

.check {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  margin-top: -6px;
  padding: 0;
  border: 0;
  background: transparent;
  cursor: pointer;
}

.box {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 2px solid var(--text-muted);
  border-radius: var(--radius);
}

.selected .box {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--on-accent);
}

.time {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding-top: 6px;
  font-size: 13px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.has {
  display: flex;
  gap: 6px;
  min-height: 14px;
}

.speaker {
  font-size: 13px;
  color: var(--text-muted);
}

.text {
  min-width: 0;
  cursor: pointer;
}

.words .said {
  cursor: text;
}

.word {
  border-radius: 2px;
  background: color-mix(in srgb, var(--accent) 45%, transparent);
  color: var(--text-strong);
}

.said {
  font-size: 22px;
  line-height: 1.5;
  color: var(--text-strong);
  white-space: pre-line;
  overflow-wrap: anywhere;
}

.current .said {
  font-weight: 500;
}

@media (width <= 600px) {
  .line {
    grid-template-columns: 44px 1fr;
    padding: 8px 12px 8px 4px;
  }

  .time {
    grid-column: 2;
    grid-row: 1;
    flex-direction: row;
    align-items: center;
    gap: 8px;
    padding-top: 0;
    font-size: 12px;
  }

  .text {
    grid-column: 2;
    grid-row: 2;
  }

  .check {
    grid-row: 1 / span 2;
  }

  .said {
    font-size: 20px;
  }
}
</style>
