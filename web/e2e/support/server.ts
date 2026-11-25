import { type ChildProcess, spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/** A mining server of a test's own: its address, its key and where it keeps its data. */
export interface TestServer {
  /** Its address, such as http://127.0.0.1:41234. */
  url: string;
  /** The API key it was started with. */
  key: string;
  dataFolder: string;
  /** What it wrote to its log so far. */
  log(): string;
  /** Stops it and starts it again on the same data, as after an update. */
  restart(): Promise<void>;
  stop(): Promise<void>;
}

/** A port nothing listens on now. */
async function freePort(): Promise<number> {
  const probe = createServer();
  probe.listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const { port } = probe.address() as { port: number };
  probe.close();
  await once(probe, 'close');
  return port;
}

async function answers(url: string, timeoutMs: number, running: () => boolean): Promise<void> {
  const until = Date.now() + timeoutMs;
  for (;;) {
    try {
      // Without a key the server says 401: it is up.
      const response = await fetch(`${url}/Api/Status`);
      if (response.status === 401) return;
    } catch {
      // Not listening yet.
    }
    if (!running()) throw new Error('The mining server stopped while it was starting.');
    if (Date.now() > until) throw new Error(`The mining server did not answer at ${url}.`);
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
}

/**
 * Starts the built server on a port and a data folder of its own. The key is the test's, so
 * nothing has to read it from the server's files.
 */
export async function startServer(): Promise<TestServer> {
  const dll = process.env.VN_MINING_E2E_SERVER;
  if (dll === undefined || dll === '') {
    throw new Error('Set VN_MINING_E2E_SERVER to the built VnMiningServer.dll, or run dev/e2e.sh.');
  }

  const dataFolder = await mkdtemp(join(tmpdir(), 'vn-mining-e2e-'));
  const port = await freePort();
  const url = `http://127.0.0.1:${port}`;
  const key = `e2e-key-${process.pid}-${port}`;
  let output = '';
  let child: ChildProcess | undefined;

  async function start(): Promise<void> {
    const started = spawn(process.env.VN_MINING_E2E_DOTNET || 'dotnet', [dll!], {
      env: {
        ...process.env,
        VN_MINING_DATA: dataFolder,
        VN_MINING_LISTEN: url,
        VN_MINING_API_KEY: key,
        ...(process.env.VN_MINING_E2E_FFMPEG
          ? { VN_MINING_FFMPEG: process.env.VN_MINING_E2E_FFMPEG }
          : {}),
        DOTNET_NOLOGO: '1',
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    child = started;
    started.stdout.on('data', (chunk: Buffer) => (output += chunk.toString('utf8')));
    started.stderr.on('data', (chunk: Buffer) => (output += chunk.toString('utf8')));
    try {
      await answers(url, 30_000, () => started.exitCode === null);
    } catch (error) {
      started.kill('SIGKILL');
      throw new Error(`${error instanceof Error ? error.message : String(error)}\n${output}`, {
        cause: error,
      });
    }
  }

  async function end(): Promise<void> {
    const running = child;
    child = undefined;
    if (running === undefined || running.exitCode !== null) return;
    const closed = once(running, 'close');
    running.kill('SIGTERM');
    const timer = setTimeout(() => running.kill('SIGKILL'), 5_000);
    await closed;
    clearTimeout(timer);
  }

  await start();
  return {
    url,
    key,
    dataFolder,
    log: () => output,
    async restart() {
      await end();
      await start();
    },
    async stop() {
      await end();
      if (process.env.VN_MINING_E2E_KEEP === '1') {
        console.log(`The server's data folder is kept at ${dataFolder}`);
      } else {
        await rm(dataFolder, { recursive: true, force: true });
      }
    },
  };
}
