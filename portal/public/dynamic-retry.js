// Reactor JS SDK 3.4.0 exposes retry_after_ms / retryAfter in milliseconds.
const abortError = () => new DOMException('Connection cancelled', 'AbortError');
const timeoutError = () => new Error('Dynamic connection retry budget expired. Try again after the cooldown.');

export function rateLimitKind(error) {
  const status = Number(error?.status ?? error?.statusCode ?? error?.response?.status);
  if (Number.isFinite(status) && status !== 0 && status !== 429) return null;
  const message = String(error?.message || '');
  const limited = status === 429 || error?.code === 'RATE_LIMITED' || error?.name === 'RateLimitedError' || /\b429\b/.test(message);
  if (!limited) return null;
  return /capacity/i.test(message) ? 'capacity' : 'rate_limit';
}

export function retryDelay(error, attempt, now = Date.now(), random = Math.random) {
  const sdkMilliseconds = error?.retry_after_ms ?? error?.retryAfter;
  if (Number.isFinite(sdkMilliseconds) && sdkMilliseconds >= 0) return Math.min(86400000, sdkMilliseconds);
  const headers = error?.headers ?? error?.response?.headers;
  const header = error?.retryAfterHeader ?? (typeof headers?.get === 'function' ? headers.get('retry-after') : headers?.['retry-after'] ?? headers?.['Retry-After']);
  if (header != null && String(header).trim()) {
    const seconds = Number(header);
    const milliseconds = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(header) - now;
    if (Number.isFinite(milliseconds)) return Math.min(86400000, Math.max(0, milliseconds));
  }
  return Math.min(60000, Math.round(8000 * 2 ** (attempt - 1) * (0.8 + 0.4 * random())));
}

export function waitForRetry(milliseconds, signal, onTick = () => {}) {
  if (signal?.aborted) return Promise.reject(abortError());
  return new Promise((resolve, reject) => {
    const end = Date.now() + milliseconds;
    let timeout, interval;
    const finish = (error) => { clearTimeout(timeout); clearInterval(interval); signal?.removeEventListener('abort', cancel); error ? reject(error) : resolve(); };
    const cancel = () => finish(abortError());
    signal?.addEventListener('abort', cancel, { once: true });
    interval = setInterval(() => onTick(Math.max(0, Math.ceil((end - Date.now()) / 1000))), 250);
    timeout = setTimeout(() => finish(), milliseconds);
    onTick(Math.ceil(milliseconds / 1000));
  });
}

function withinBudget(operation, milliseconds, signal) {
  if (signal?.aborted) return Promise.reject(abortError());
  return new Promise((resolve, reject) => {
    let timer;
    const finish = (error, value) => { clearTimeout(timer); signal?.removeEventListener('abort', cancel); error ? reject(error) : resolve(value); };
    const cancel = () => finish(abortError());
    signal?.addEventListener('abort', cancel, { once: true });
    timer = setTimeout(() => finish(timeoutError()), milliseconds);
    Promise.resolve().then(operation).then((value) => finish(null, value), (error) => finish(error));
  });
}

export async function connectWithCapacityRetry({ connect, signal, getCooldown, setCooldown, getReason = () => 'rate_limit', onWait = () => {}, now = Date.now, random = Math.random, wait = waitForRetry, budgetMs = 90000, maxAttempts = 4 }) {
  const deadline = now() + budgetMs;
  let attempts = 0;
  while (attempts < maxAttempts) {
    if (signal?.aborted) throw abortError();
    const delay = Math.max(0, getCooldown() - now());
    if (delay >= deadline - now()) throw timeoutError();
    if (delay) await wait(delay, signal, (seconds) => onWait({ seconds, attempt: attempts + 1, maxAttempts, reason: getReason() }));
    if (signal?.aborted) throw abortError();
    const remaining = deadline - now();
    if (remaining <= 0) throw timeoutError();
    attempts++;
    try { return await withinBudget(() => connect(attempts), remaining, signal); }
    catch (error) {
      if (signal?.aborted || error?.name === 'AbortError') throw abortError();
      const reason = rateLimitKind(error);
      if (!reason) throw error;
      setCooldown(Math.max(getCooldown(), now() + retryDelay(error, attempts, now(), random)), reason);
      if (error?.noRetry) throw error; // Session allocated or cleanup unconfirmed: never create another automatically.
      if (attempts >= maxAttempts) throw new Error(`Dynamic ${reason === 'capacity' ? 'has no available capacity' : 'rate limit persists'} after ${maxAttempts} attempts. Try again after the cooldown.`);
    }
  }
}
