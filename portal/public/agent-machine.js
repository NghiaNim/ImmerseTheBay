// Small UI lifecycle; lesson progression remains authoritative on the server.
export function createAgentMachine({ onChange = () => {} } = {}) {
  let state = 'idle';
  function send(event, { complete = false } = {}) {
    let next;
    if (event === 'stop') next = 'idle';
    else if (event === 'begin' && ['idle', 'recovering'].includes(state)) next = 'observing';
    else if (event === 'observe' && ['observing', 'waiting', 'recovering'].includes(state)) next = 'observing';
    else if (event === 'plan' && state === 'observing') next = 'planning';
    else if (event === 'act' && state === 'planning') next = 'acting';
    else if (event === 'done' && state === 'acting') next = complete ? 'complete' : 'waiting';
    else if (event === 'fail' && state !== 'idle') next = 'recovering';
    else return false;
    state = next; onChange(state); return true;
  }
  return { send, get state() { return state; } };
}

// No repeated model polling: eligibility changes arm/disarm one quiet timer.
export function createIdleInvitation({ eligible, invite, delayMs = 20000, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  let timer = null, nodeId = null, generation = 0;
  const invited = new Set();
  function suspend() { generation++; if (timer !== null) clearTimer(timer); timer = null; }
  function sync(nextNode = nodeId) {
    if (nextNode !== nodeId) { suspend(); nodeId = nextNode; }
    if (!nodeId || invited.has(nodeId) || !eligible()) { suspend(); return; }
    if (timer !== null) return;
    const ownGeneration = generation, ownNode = nodeId;
    timer = setTimer(() => {
      timer = null;
      if (ownGeneration !== generation || ownNode !== nodeId || invited.has(ownNode) || !eligible()) return;
      invited.add(ownNode);
      if (invited.size > 256) invited.delete(invited.values().next().value);
      invite(ownNode);
    }, delayMs);
  }
  function activity() { suspend(); sync(); }
  function reset() { suspend(); nodeId = null; invited.clear(); }
  return { sync, activity, suspend, reset };
}
