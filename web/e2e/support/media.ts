import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const ffmpeg = (): string => process.env.VN_MINING_E2E_FFMPEG || 'ffmpeg';
const ffprobe = (): string => ffmpeg().replace(/ffmpeg$/, 'ffprobe');

/** A voice and a screenshot as a game sends them, made once per test worker. */
export interface SampleMedia {
  /** A second of a tone as Ogg Vorbis, the format of a visual novel's voice files. */
  voice: Buffer;
  /** A shorter, higher tone, to tell two voices apart in a joined sound. */
  voice2: Buffer;
  /** An 800x600 picture as PNG, the size of an older game's window. */
  screenshot: Buffer;
  /** The same as JPEG, as the iOS app sends it. */
  screenshotJpeg: Buffer;
  /** A short MP3, as an audio source has of a word. */
  wordSound: Buffer;
}

let made: Promise<SampleMedia> | undefined;

export function sampleMedia(): Promise<SampleMedia> {
  made ??= (async () => {
    const folder = await mkdtemp(join(tmpdir(), 'vn-mining-e2e-media-'));
    try {
      const tone = async (name: string, hz: number, seconds: number): Promise<Buffer> => {
        const file = join(folder, name);
        const codec = name.endsWith('.mp3')
          ? ['-c:a', 'libmp3lame', '-b:a', '64k']
          : ['-c:a', 'libvorbis', '-q:a', '2'];
        await run(ffmpeg(), [
          '-v',
          'error',
          '-f',
          'lavfi',
          '-i',
          `sine=frequency=${hz}:duration=${seconds}`,
          ...codec,
          '-y',
          file,
        ]);
        return readFile(file);
      };
      const picture = async (name: string): Promise<Buffer> => {
        const file = join(folder, name);
        await run(ffmpeg(), [
          '-v',
          'error',
          '-f',
          'lavfi',
          '-i',
          'testsrc=size=800x600:rate=1',
          '-frames:v',
          '1',
          '-y',
          file,
        ]);
        return readFile(file);
      };
      return {
        voice: await tone('voice.ogg', 440, 1),
        voice2: await tone('voice2.ogg', 880, 0.5),
        screenshot: await picture('shot.png'),
        screenshotJpeg: await picture('shot.jpg'),
        wordSound: await tone('word.mp3', 660, 0.4),
      };
    } finally {
      await rm(folder, { recursive: true, force: true });
    }
  })();
  return made;
}

/** Runs ffprobe over a file's bytes and gives back what it says, as JSON. */
async function probe(data: Buffer, entries: string): Promise<unknown> {
  const folder = await mkdtemp(join(tmpdir(), 'vn-mining-e2e-probe-'));
  try {
    const file = join(folder, 'file');
    await writeFile(file, data);
    const { stdout } = await run(ffprobe(), [
      '-v',
      'error',
      '-show_entries',
      entries,
      '-of',
      'json',
      file,
    ]);
    return JSON.parse(stdout) as unknown;
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
}

/** How long a sound is in seconds, and what it is, as ffprobe reads it. */
export async function probeSound(data: Buffer): Promise<{ seconds: number; codec: string }> {
  const read = (await probe(data, 'format=duration:stream=codec_name')) as {
    format: { duration: string };
    streams: { codec_name: string }[];
  };
  return { seconds: Number(read.format.duration), codec: read.streams[0]?.codec_name ?? '' };
}

/** How large a picture is, and what it is, as ffprobe reads it. */
export async function probePicture(
  data: Buffer,
): Promise<{ width: number; height: number; codec: string }> {
  const read = (await probe(data, 'stream=codec_name,width,height')) as {
    streams: { codec_name: string; width: number; height: number }[];
  };
  const stream = read.streams[0];
  if (stream === undefined) throw new Error('This is no picture.');
  return { width: stream.width, height: stream.height, codec: stream.codec_name };
}
