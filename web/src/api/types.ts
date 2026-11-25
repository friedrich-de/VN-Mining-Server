/**
 * The server's answers, as its endpoints below /Api/ send them. Names are written with a capital,
 * and what has no value is left out.
 */

/** What the server says about itself to a signed-in page: GET /Api/Status. */
export interface Status {
  Version: string;
  /** How many lines the stream holds. */
  Lines: number;
  /** How many lines the stream keeps before the oldest go. */
  MaxLines: number;
}

/** One line of the game's text. */
export interface StreamLine {
  /** Its number. Numbers only grow, and no later line gets the number of one that is gone. */
  Id: number;
  /** When the server got it, as an ISO time. */
  At: string;
  /** What is said, with a line feed between its rows. */
  Text: string;
  /** Who says it, where the game names a speaker. */
  Name?: string;
  /** The game's title, if the game sent one. */
  Game?: string;
  /** Whether the server keeps the line's voice. */
  HasAudio: boolean;
  /** Whether the server keeps the line's screenshot. */
  HasPicture: boolean;
}

/** A part of the stream. */
export interface LinesView {
  /** Oldest first. */
  Lines: StreamLine[];
  /** How many lines the stream holds in all. */
  Total: number;
  MaxLines: number;
}

/** Where the cards go. */
export type AnkiWhere = 'off' | 'device' | 'server';

export interface AnkiView {
  Where: AnkiWhere;
  DeviceAddress: string;
  /** The browser sends it, so the page gets it back. */
  DeviceKey: string;
  ServerAddress: string;
  /** Whether a key for the server's AnkiConnect is stored. The key itself stays on the server. */
  ServerKeySet: boolean;
}

/** The settings, kept on the server so they are the same on every device. */
export interface SettingsView {
  Anki: AnkiView;
  /** What only the page reads, as it saved it. */
  Page?: unknown;
  Stream: StreamSettings;
}

/** The settings of the stream of lines. */
export interface StreamSettings {
  /** How many lines the stream keeps before the oldest go. */
  MaxLines: number;
}

/** A change of the settings. What is left out stays as it is. */
export interface SettingsChange {
  Anki?: Partial<Omit<AnkiView, 'ServerKeySet'>> & {
    /** Empty takes it away. */
    ServerKey?: string;
  };
  Page?: unknown;
  Stream?: StreamSettings;
}

/** A file the server put into the user's Anki. */
export interface StoredMedia {
  FileName: string;
}

/** A dictionary on the server. */
export interface DictionaryView {
  /** The name the calls that change it use. */
  Id: string;
  /** Its name in lookups. */
  Title: string;
  Revision: string;
  /** Whether lookups use it. */
  Enabled: boolean;
  Terms: number;
  Frequencies: number;
  /** Pitch accents and pronunciations. */
  Pitches: number;
  FrequencyMode?: 'rank-based' | 'occurrence-based';
  Author?: string;
  Url?: string;
  Attribution?: string;
  /** Where a newer revision can be downloaded. */
  DownloadUrl?: string;
  /** Its stylesheet, which the popup keeps to its definitions. */
  Styles: string;
  /** The size of its file on the server. */
  Bytes: number;
  Imported: string;
}

export type ImportState = 'idle' | 'downloading' | 'importing' | 'done' | 'failed';

/** What the import that runs, or ran last, does, and what waits behind it. */
export interface DictionaryImport {
  State: ImportState;
  /** A file's name or an address. */
  Source?: string;
  /** From 0 to 1. */
  Progress: number;
  Title?: string;
  Error?: string;
  /** How many more dictionaries wait to be imported after this one. */
  Waiting: number;
  /** What the last imports came to, the oldest first. */
  Finished: DictionaryImportOutcome[];
}

/** What an import came to: the dictionary's title when it went in, or why it did not. */
export interface DictionaryImportOutcome {
  /** Counted up from 1 since the server started: a page tells by it which ones it has not shown. */
  Number: number;
  /** A file's name or an address. */
  Source: string;
  Title?: string;
  Error?: string;
}

export interface DictionariesView {
  /** In the order lookups read them. */
  Dictionaries: DictionaryView[];
  Import: DictionaryImport;
  /** Whether the user may add, arrange and remove them: administrators may. */
  MayManage: boolean;
  /** Where words' sounds come from. */
  WordAudio?: WordAudioView;
}

/** Where words' sounds come from. Its address stays on the server: it often holds a key. */
export interface WordAudioView {
  /** Whether an administrator set an audio source. */
  Set: boolean;
  /** The source's host, for administrators only. */
  Host?: string;
}

/** The sounds the audio source has for a word, by name; their addresses stay on the server. */
export interface WordAudioSourcesView {
  Sources: string[];
}

/** The audio source as an administrator set it, and what trying it with 食べる found. */
export interface WordAudioSaved {
  WordAudio: WordAudioView;
  Tried: 'found' | 'notFound' | 'failed' | 'none';
}

/**
 * What a lookup finds, as the dictionary popup reads it. Unlike the server's other answers, its
 * names start small.
 */
export interface LookupResult {
  /** The part of the text the word was found as. */
  matched: string;
  deinflected: string;
  /** The inflections undone, in the order they were undone. */
  trace: { name: string; description?: string }[];
  term: {
    expression: string;
    reading: string;
    /** Its parts of speech, with spaces between them ("v1 vt"). */
    rules: string;
    glossaries: {
      dictionary: string;
      /** Yomitan's glossary of one definition, as JSON. */
      glossary: string;
      definitionTags: string;
      termTags: string;
      tags: { name: string; category: string; order: number; notes: string; score: number }[];
    }[];
    frequencies: {
      dictionary: string;
      frequencies: { value: number; displayValue?: string }[];
    }[];
    pitches: {
      dictionary: string;
      pitches: { position: number; pattern?: string; nasal?: number[]; devoice?: number[] }[];
      transcriptions?: string[];
    }[];
  };
}

export interface Lookup {
  results: LookupResult[];
}
