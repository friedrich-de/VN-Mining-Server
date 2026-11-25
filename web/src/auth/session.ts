import { type Fetch, HttpError, UnreachableError } from '@/api/http';
import { createServerApi } from '@/api/server';
import type { Status } from '@/api/types';

/** A browser the server knows. Its sign-in is a cookie the page cannot read. */
export interface Session {
  status: Status;
}

export type Resolution =
  | { kind: 'signed-in'; session: Session }
  | { kind: 'signed-out' }
  | { kind: 'unreachable' }
  | { kind: 'failed'; message: string };

export type KeyResult = Resolution | { kind: 'refused'; message: string };

/** Everything signing in touches, so tests can stand in for the browser. */
export interface AuthEnv {
  fetch: Fetch;
  /** The page's address. */
  base: string;
  /** The server's version, as the page was built with it. */
  version: string;
}

function failure(error: unknown): Resolution {
  if (error instanceof UnreachableError) return { kind: 'unreachable' };
  return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
}

/** Asks the server whether this browser is signed in. */
export async function resolveSession(env: AuthEnv): Promise<Resolution> {
  try {
    const status = await createServerApi(env.fetch, env.base).status();
    return { kind: 'signed-in', session: { status } };
  } catch (error) {
    if (error instanceof HttpError && error.status === 401) return { kind: 'signed-out' };
    return failure(error);
  }
}

/** Signs the browser in with the server's API key. The server keeps the sign-in in a cookie. */
export async function signInWithKey(env: AuthEnv, key: string): Promise<KeyResult> {
  try {
    await createServerApi(env.fetch, env.base).signIn(key);
  } catch (error) {
    // A wrong key, or too many of them: the server says which.
    if (error instanceof HttpError && (error.status === 401 || error.status === 429)) {
      return { kind: 'refused', message: error.message };
    }
    return failure(error);
  }
  return resolveSession(env);
}

/** Ends the browser's sign-in. */
export async function signOut(env: AuthEnv): Promise<void> {
  try {
    await createServerApi(env.fetch, env.base).signOut();
  } catch {
    // The page asks the server again either way, and shows what it says.
  }
}
