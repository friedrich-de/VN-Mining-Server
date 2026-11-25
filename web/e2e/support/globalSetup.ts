import { access } from 'node:fs/promises';

/** Says early, and in words, what the tests need that is not there. */
export default async function globalSetup(): Promise<void> {
  const dll = process.env.VN_MINING_E2E_SERVER;
  if (dll === undefined || dll === '') {
    throw new Error(
      'Set VN_MINING_E2E_SERVER to the built VnMiningServer.dll, or run dev/e2e.sh, which does.',
    );
  }
  try {
    await access(dll);
  } catch {
    throw new Error(`There is no server at ${dll}. Build it first, or run dev/e2e.sh, which does.`);
  }
}
