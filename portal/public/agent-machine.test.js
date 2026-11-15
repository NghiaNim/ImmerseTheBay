import test from 'node:test';
import assert from 'node:assert/strict';
import { createAgentMachine, createIdleInvitation } from './agent-machine.js';

function clock() {
  let time = 0, id = 0;
  const jobs = new Map();
  return {
    setTimer: (fn, delay) => { jobs.set(++id, { fn, at: time + delay }); return id; },
    clearTimer: (key) => jobs.delete(key),
    advance(ms) {
      const until = time + ms;
      while (true) {
        const next = [...jobs].filter(([, job]) => job.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
        if (!next) break;
        time = next[1].at; jobs.delete(next[0]); next[1].fn();
      }
      time = until;
    },
    get pending() { return jobs.size; },
  };
}

test('agent lifecycle guards duplicate planning/acting and supports recovery and completion', () => {
  const seen = []; const machine = createAgentMachine({ onChange: (state) => seen.push(state) });
  assert.equal(machine.state, 'idle'); assert.equal(machine.send('plan'), false);
  assert.equal(machine.send('begin'), true); assert.equal(machine.send('observe'), true);
  assert.equal(machine.send('plan'), true); assert.equal(machine.send('observe'), false);
  assert.equal(machine.send('act'), true); assert.equal(machine.send('act'), false);
  machine.send('done'); assert.equal(machine.state, 'waiting');
  machine.send('observe'); machine.send('plan'); machine.send('fail');
  assert.equal(machine.state, 'recovering');
  machine.send('observe'); machine.send('plan'); machine.send('act'); machine.send('done', { complete: true });
  assert.equal(machine.state, 'complete'); assert.equal(machine.send('observe'), false);
  machine.send('stop'); assert.equal(machine.state, 'idle');
  assert.ok(seen.includes('recovering'));
});

test('quiet invitation occurs once per node after 20 seconds and resets for a new node', () => {
  const timer = clock(), calls = [];
  const idle = createIdleInvitation({ ...timer, eligible: () => true, invite: (node) => calls.push(node) });
  idle.sync('node-1'); timer.advance(19999); assert.deepEqual(calls, []);
  idle.sync('node-1'); timer.advance(1); assert.deepEqual(calls, ['node-1']);
  idle.sync('node-1'); timer.advance(60000); assert.deepEqual(calls, ['node-1']);
  idle.sync('node-2'); timer.advance(20000); assert.deepEqual(calls, ['node-1', 'node-2']);
});

test('user activity postpones invitation and mic/TTS/hidden/nonplaying states disarm it', () => {
  const timer = clock(), calls = []; let allowed = true;
  const idle = createIdleInvitation({ ...timer, eligible: () => allowed, invite: (node) => calls.push(node) });
  idle.sync('node'); timer.advance(15000); idle.activity(); timer.advance(15000);
  assert.deepEqual(calls, []);
  allowed = false; idle.sync('node'); assert.equal(timer.pending, 0); timer.advance(50000);
  assert.deepEqual(calls, []);
  allowed = true; idle.sync('node'); timer.advance(19999); assert.deepEqual(calls, []);
  timer.advance(1); assert.deepEqual(calls, ['node']);
});

test('eligibility is rechecked on timer fire and leave invalidates pending callbacks', () => {
  const timer = clock(), calls = []; let allowed = true;
  const idle = createIdleInvitation({ ...timer, eligible: () => allowed, invite: (node) => calls.push(node) });
  idle.sync('old'); allowed = false; timer.advance(20000); assert.deepEqual(calls, []);
  allowed = true; idle.sync('old'); idle.reset(); timer.advance(20000); assert.deepEqual(calls, []);
  idle.sync('new'); timer.advance(20000); assert.deepEqual(calls, ['new']);
});
