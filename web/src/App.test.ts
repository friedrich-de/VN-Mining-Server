import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';

import App from './App.vue';
import {
  env,
  fakeStream,
  json,
  problem,
  type RoutedRequest,
  SESSION_URL,
  settings,
  status,
  STATUS_URL,
} from './testing/fakes';

/**
 * A server that keeps a sign-in as the real one keeps its cookie: Status answers only once the
 * key was given, and no longer after a sign-out.
 */
function server(
  options: {
    key?: string;
    signedIn?: boolean;
    /** How many requests get no answer before the server is back. */
    down?: number;
    /** The browser throws the server's cookie away. */
    noCookie?: boolean;
  } = {},
) {
  const key = options.key ?? 'the-key';
  const state = { signedIn: options.signedIn ?? false, down: options.down ?? 0 };
  const fake = fakeStream([], (request: RoutedRequest) => {
    if (state.down > 0) {
      state.down -= 1;
      return new TypeError('Failed to fetch');
    }
    if (request.path === 'Session' && request.method === 'POST') {
      if ((request.body as { Key?: string }).Key !== key) {
        return problem(401, "That is not this server's API key.");
      }
      state.signedIn = options.noCookie !== true;
      return new Response(null, { status: 204 });
    }
    if (request.path === 'Session' && request.method === 'DELETE') {
      state.signedIn = false;
      return new Response(null, { status: 204 });
    }
    if (!state.signedIn) return problem(401, "Sign in with the server's API key.");
    if (request.path === 'Status') return json(status({ Version: '0.1.0', MaxLines: 500 }));
    if (request.path === 'Settings') return json(settings());
    if (request.path === 'Dictionaries') {
      return json({
        Dictionaries: [],
        Import: { State: 'idle', Progress: 0, Waiting: 0, Finished: [] },
        MayManage: true,
      });
    }
    return undefined;
  });
  return { ...fake, state };
}

async function open(fake: ReturnType<typeof server>) {
  const page = mount(App, { props: { env: env(fake.fetch) }, attachTo: document.body });
  await flushPromises();
  return page;
}

describe('the page', () => {
  it('opens at once in a browser the server knows', async () => {
    const fake = server({ signedIn: true });

    const page = await open(fake);

    expect(fake.sent[0]?.url).toBe(STATUS_URL);
    expect(page.find('form').exists()).toBe(false);
    expect(page.get('[data-testid="versions"]').text()).toBe(
      'VN Mining Server 0.1.0, keeping the newest 500 lines',
    );
    expect(page.get('[data-testid="no-lines"] h1').text()).toBe('No lines yet');
    page.unmount();
  });

  it('asks for the API key in a browser the server does not know, and signs in with it', async () => {
    const fake = server();

    const page = await open(fake);
    expect(page.get('h1').text()).toBe('Sign in');

    await page.get('#sign-in-key').setValue('  the-key ');
    await page.get('form').trigger('submit');
    await flushPromises();

    const sent = fake.sent.find((request) => request.url === SESSION_URL);
    expect(sent?.body).toEqual({ Key: 'the-key' });
    expect(page.get('[data-testid="signed-in-as"]').text()).toBe(
      "Signed in with the server's API key",
    );
    page.unmount();
  });

  it('shows why a key was refused, in the server’s words, and lets the user try again', async () => {
    const fake = server();

    const page = await open(fake);
    await page.get('#sign-in-key').setValue('wrong');
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(page.get('[role="alert"]').text()).toBe("That is not this server's API key.");
    expect(page.get('button[type="submit"]').attributes('disabled')).toBeUndefined();

    await page.get('#sign-in-key').setValue('the-key');
    await page.get('form').trigger('submit');
    await flushPromises();
    expect(page.find('[data-testid="signed-in-as"]').exists()).toBe(true);
    page.unmount();
  });

  it('does not send an empty key', async () => {
    const fake = server();

    const page = await open(fake);
    const before = fake.sent.length;
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(fake.sent).toHaveLength(before);
    page.unmount();
  });

  it('says so when the browser does not keep the sign-in', async () => {
    const fake = server({ noCookie: true });
    const page = await open(fake);

    await page.get('#sign-in-key').setValue('the-key');
    await page.get('form').trigger('submit');
    await flushPromises();

    expect(page.get('[role="alert"]').text()).toBe(
      'This browser did not keep the sign-in. The page needs its cookie.',
    );
    page.unmount();
  });

  it('signs out and shows the sign-in again', async () => {
    const fake = server({ signedIn: true });
    const page = await open(fake);

    await page.get('[data-testid="sign-out"]').trigger('click');
    await flushPromises();

    expect(
      fake.sent.some((request) => request.url === SESSION_URL && request.method === 'DELETE'),
    ).toBe(true);
    expect(page.get('h1').text()).toBe('Sign in');
    page.unmount();
  });

  it('asks for the key again once the server no longer takes the sign-in', async () => {
    const fake = server({ signedIn: true });
    const page = await open(fake);
    expect(page.find('[data-testid="signed-in-as"]').exists()).toBe(true);

    // The key was changed on the server: the stream of lines is refused when it is opened again.
    fake.state.signedIn = false;
    fake.streams.at(-1)!.close();
    await new Promise((resolve) => setTimeout(resolve, 1_100));
    await flushPromises();

    expect(page.get('h1').text()).toBe('Sign in');
    page.unmount();
  });

  it('says that the server cannot be reached and tries again when asked', async () => {
    const fake = server({ signedIn: true, down: 1 });

    const page = await open(fake);
    expect(page.get('h1').text()).toBe('The server could not be reached');

    await page.get('button').trigger('click');
    await flushPromises();

    expect(page.find('[data-testid="signed-in-as"]').exists()).toBe(true);
    page.unmount();
  });
});
