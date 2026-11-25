import { expect, test } from '../support/fixtures';

test.describe('signing in', () => {
  test('asks for the API key once and keeps the browser signed in', async ({ page, server }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();

    await page.getByLabel('API key').fill('not the key');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText("That is not this server's API key.");

    await page.getByLabel('API key').fill(server.key);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByTestId('no-lines')).toBeVisible();

    // The sign-in is a cookie script cannot read and no other site can use; the key is kept nowhere.
    const cookies = await page.context().cookies();
    expect(cookies).toHaveLength(1);
    expect(cookies[0]).toMatchObject({ name: 'vnm_session', httpOnly: true, sameSite: 'Strict' });
    expect(cookies[0]!.value).not.toContain(server.key);
    const kept = await page.evaluate(
      () =>
        document.cookie +
        JSON.stringify({ ...localStorage }) +
        JSON.stringify({ ...sessionStorage }),
    );
    expect(kept).not.toContain(server.key);

    // It holds over a reload, and over the server starting again.
    await page.reload();
    await expect(page.getByTestId('no-lines')).toBeVisible();
    await server.restart();
    await page.reload();
    await expect(page.getByTestId('no-lines')).toBeVisible();

    await page.getByTestId('account').click();
    await expect(page.getByTestId('versions')).toContainText('VN Mining Server');
    await page.getByTestId('sign-out').click();
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(await page.context().cookies()).toEqual([]);
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  });

  test('gives nothing of the stream to a browser without the key', async ({
    page,
    game,
    media,
  }) => {
    await game.say({
      text: '昨日の話、覚えてる？',
      audio: media.voice,
      screenshot: media.screenshot,
    });

    const answers: Record<string, number> = {};
    for (const path of [
      'Api/Status',
      'Api/Lines',
      'Api/Events',
      'Api/Lines/1/Screenshot',
      'Api/Lines/1/Picture',
      'Api/Lines/Audio?lines=1',
      'Api/Settings',
      'Api/GameSettings',
      'Api/Dictionaries',
    ]) {
      answers[path] = (await page.request.get(`/${path}`)).status();
    }
    expect([...new Set(Object.values(answers))], JSON.stringify(answers)).toEqual([401]);

    // The page itself is no secret: it holds no data.
    expect((await page.request.get('/')).status()).toBe(200);
  });

  test('stops taking keys from an address that tried too many', async ({ page, server }) => {
    await page.goto('/');
    for (let attempt = 0; attempt < 10; attempt += 1) {
      await page.getByLabel('API key').fill(`guess ${attempt}`);
      await page.getByRole('button', { name: 'Sign in' }).click();
      await expect(page.getByRole('button', { name: 'Sign in' })).toBeEnabled();
    }

    // Now not even the right key is looked at.
    await page.getByLabel('API key').fill(server.key);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText(
      'Too many wrong keys from this address. Try again in a minute.',
    );
  });
});
