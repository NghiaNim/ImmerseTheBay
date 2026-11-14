import test from 'node:test';
import assert from 'node:assert/strict';
import { connectWithCapacityRetry, rateLimitKind, retryDelay, waitForRetry } from '../public/dynamic-retry.js';

const capacity = () => Object.assign(new Error('429 no available capacity'), { status: 429 });
function clockRun(connect, extra = {}) {
  let clock = 0, cooldown = 0, reason = 'rate_limit';
  const waits = [], notices = [];
  return { waits, notices, run: () => connectWithCapacityRetry({ connect, now: () => clock, random: () => 0.5,
    getCooldown: () => cooldown, setCooldown: (until, why) => { cooldown = until; reason = why; }, getReason: () => reason,
    wait: async (ms, _signal, tick) => { waits.push(ms); tick(Math.ceil(ms / 1000)); clock += ms; }, onWait: (notice) => notices.push(notice), ...extra }),
  getCooldown: () => cooldown };
}

test('two capacity failures retry with backoff then return one successful session', async () => {
  let attempts = 0;
  const fixture = clockRun(async () => { if (++attempts < 3) throw capacity(); return 'session'; });
  assert.equal(await fixture.run(), 'session'); assert.equal(attempts, 3);
  assert.deepEqual(fixture.waits, [8000, 16000]); assert.deepEqual(fixture.notices.map((x) => x.attempt), [2, 3]);
  assert(fixture.notices.every((x) => x.reason === 'capacity'));
});

test('four attempts exhaust with cooldown retained for the next Begin', async () => {
  let attempts = 0; const fixture = clockRun(async () => { attempts++; throw capacity(); });
  await assert.rejects(fixture.run(), /after 4 attempts/); assert.equal(attempts, 4);
  assert.deepEqual(fixture.waits, [8000, 16000, 32000]); assert(fixture.getCooldown() > 56000);
});

test('authorization, network failures and already allocated sessions do not retry', async () => {
  for (const error of [Object.assign(new Error('Forbidden'), { status: 403 }), new Error('Network failed'), Object.assign(capacity(), { noRetry: true })]) {
    let attempts = 0; const fixture = clockRun(async () => { attempts++; throw error; });
    await assert.rejects(fixture.run(), (actual) => actual === error); assert.equal(attempts, 1); assert.deepEqual(fixture.waits, []);
  }
});

test('cancel a real wait immediately without an additional connection', async () => {
  const controller = new AbortController(); let attempts = 0, cooldown = 0;
  const result = connectWithCapacityRetry({ connect: async () => { attempts++; throw capacity(); }, signal: controller.signal,
    getCooldown: () => cooldown, setCooldown: (until) => { cooldown = until; }, onWait: () => controller.abort() });
  await assert.rejects(result, { name: 'AbortError' }); assert.equal(attempts, 1); assert(cooldown > Date.now());
});

test('a fresh Begin honors an existing page cooldown before its first attempt', async () => {
  const sequence = []; let clock = 1000;
  const value = await connectWithCapacityRetry({ connect: async () => { sequence.push('connect'); return 'session'; }, now: () => clock,
    getCooldown: () => 9000, setCooldown: () => {}, wait: async (ms) => { sequence.push(ms); clock += ms; } });
  assert.equal(value, 'session'); assert.deepEqual(sequence, [8000, 'connect']);
});

test('Retry-After uses documented SDK milliseconds, HTTP seconds/date, without shortening the provider cooldown', () => {
  assert.equal(retryDelay({ retry_after_ms: 1234 }, 1), 1234);
  assert.equal(retryDelay({ retryAfter: 2500 }, 1), 2500);
  assert.equal(retryDelay({ retryAfterHeader: '12' }, 1), 12000);
  assert.equal(retryDelay({ headers: new Headers({ 'retry-after': new Date(30000).toUTCString() }) }, 1, 10000), 20000);
  assert.equal(retryDelay({ retry_after_ms: 120000 }, 1), 120000);
  assert.equal(retryDelay({}, 1, 0, () => 0), 6400); assert.equal(retryDelay({}, 1, 0, () => 1), 9600);
  assert.equal(rateLimitKind({ status: 429, message: 'Too many requests' }), 'rate_limit');
  assert.equal(rateLimitKind({ status: 403, message: '429 capacity mentioned in rejected request' }), null);
});

test('ninety second budget stops waiting/connecting rather than starting a late attempt', async () => {
  let attempts = 0; const fixture = clockRun(async () => { attempts++; throw Object.assign(capacity(), { retry_after_ms: 60000 }); });
  await assert.rejects(fixture.run(), /budget expired/); assert.equal(attempts, 2); assert.deepEqual(fixture.waits, [60000]);
});

test('pre-aborted waits reject without starting timers or countdown', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(waitForRetry(1000, controller.signal, () => assert.fail('Unexpected countdown')), { name: 'AbortError' });
});

test('Retry-After beyond budget preserves provider minimum without another attempt', async () => {
  let attempts = 0; const fixture = clockRun(async () => { attempts++; throw Object.assign(capacity(), { retryAfterHeader: '120' }); });
  await assert.rejects(fixture.run(), /budget expired/); assert.equal(attempts, 1); assert.equal(fixture.getCooldown(), 120000); assert.deepEqual(fixture.waits, []);
});
