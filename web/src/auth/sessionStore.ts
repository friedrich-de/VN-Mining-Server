import { readonly, shallowRef } from 'vue';

import {
  type AuthEnv,
  resolveSession,
  type Session,
  signInWithKey,
  signOut as endSession,
} from './session';

export type SessionState =
  | { kind: 'loading' }
  | { kind: 'signed-in'; session: Session }
  | { kind: 'signed-out'; busy: boolean; error: string | null }
  | { kind: 'unreachable' }
  | { kind: 'failed'; message: string };

/** The page's sign-in, and what changes it. */
export function createSessionStore(env: AuthEnv) {
  const state = shallowRef<SessionState>({ kind: 'loading' });
  let run = 0;

  async function resolve(showLoading: boolean): Promise<void> {
    const mine = ++run;
    if (showLoading) state.value = { kind: 'loading' };
    const result = await resolveSession(env);
    if (mine !== run) return;
    state.value =
      result.kind === 'signed-out' ? { kind: 'signed-out', busy: false, error: null } : result;
  }

  return {
    state: readonly(state),

    start: () => resolve(true),

    stop(): void {
      run++;
    },

    retry: () => resolve(true),

    async signIn(key: string): Promise<void> {
      const mine = ++run;
      state.value = { kind: 'signed-out', busy: true, error: null };
      const result = await signInWithKey(env, key);
      if (mine !== run) return;
      state.value =
        result.kind === 'refused'
          ? { kind: 'signed-out', busy: false, error: result.message }
          : result.kind === 'signed-out'
            ? {
                kind: 'signed-out',
                busy: false,
                error: 'This browser did not keep the sign-in. The page needs its cookie.',
              }
            : result;
    },

    async signOut(): Promise<void> {
      if (state.value.kind !== 'signed-in') return;
      await endSession(env);
      await resolve(false);
    },
  };
}

export type SessionStore = ReturnType<typeof createSessionStore>;
