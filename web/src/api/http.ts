export type Fetch = typeof fetch;

/** The server answered, but not with what was asked for. */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'HttpError';
  }
}

/** The server did not answer at all. */
export class UnreachableError extends Error {
  constructor() {
    super('The server could not be reached.');
    this.name = 'UnreachableError';
  }
}

/** What went wrong, in the server's words where it gave any. */
export async function problemText(response: Response): Promise<string> {
  try {
    const body: unknown = await response.clone().json();
    if (body !== null && typeof body === 'object') {
      const { detail, title } = body as { detail?: unknown; title?: unknown };
      if (typeof detail === 'string' && detail.length > 0) return detail;
      if (typeof title === 'string' && title.length > 0) return title;
    }
  } catch {
    // Not JSON: the status has to do.
  }
  return `The server answered ${response.status}.`;
}

/** Sends a request and tells "no answer" apart from "an answer that says no". */
export async function send(fetchFn: Fetch, url: string, init: RequestInit): Promise<Response> {
  let response: Response;
  try {
    response = await fetchFn(url, init);
  } catch (error) {
    // A request the page called off is no sign of a server that cannot be reached.
    if (init.signal?.aborted === true) throw error;
    throw new UnreachableError();
  }
  if (!response.ok) {
    throw new HttpError(response.status, await problemText(response));
  }
  return response;
}

/** Reads one of the server's JSON answers. The browser sends the sign-in cookie by itself. */
export async function getJson<T>(fetchFn: Fetch, url: string, signal?: AbortSignal): Promise<T> {
  const response = await send(fetchFn, url, {
    headers: { Accept: 'application/json' },
    ...(signal === undefined ? {} : { signal }),
  });
  return (await response.json()) as T;
}
