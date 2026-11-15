// Replay only the lesson decision request. Never replay provider commands here.
export function createLessonOperations({ fetchImpl = fetch, headers = () => ({}), requestId = () => crypto.randomUUID(), timeoutMs = 20000 } = {}) {
  let generation = 0, runId, revision = 0, pending = false;
  const attempted = new Set();
  const stale = () => new DOMException('Lesson operation was superseded', 'AbortError');
  function reset() { generation++; runId = undefined; revision = 0; pending = false; attempted.clear(); }
  async function turn(payload, { signal, isCurrent = () => true } = {}) {
    if (pending) throw new Error('Await the current lesson operation');
    if (signal?.aborted || !isCurrent()) throw stale();
    const ownGeneration = generation;
    const current = () => ownGeneration === generation && isCurrent() && !signal?.aborted;
    const operation = { requestId: requestId(), ...(runId ? { runId } : {}), expectedRevision: revision };
    const request = structuredClone({ ...payload, operation });
    const body = JSON.stringify(request);
    pending = true;
    try {
      let decision;
      for (let attempt = 0; attempt < 2; attempt++) {
        if (!current()) throw stale();
        const deadline = AbortSignal.timeout(timeoutMs);
        const combined = signal ? AbortSignal.any([signal, deadline]) : deadline;
        try {
          const response = await fetchImpl('/api/agent/turn', { method: 'POST', headers: { ...headers(), 'Content-Type': 'application/json' }, body, signal: combined });
          if (!response.ok) {
            let data;
            try { data = await response.json(); } catch {}
            const error = new Error(data?.error || `Portal returned ${response.status}`);
            error.status = response.status; throw error;
          }
          decision = await response.json();
          break;
        } catch (error) {
          // A lost HTTP response/body can be replayed with exactly the same ID.
          // HTTP errors, malformed JSON, deadline and user cancellation cannot.
          if (!current()) throw stale();
          if (attempt || combined.aborted || !(error instanceof TypeError) || error.status) throw error;
        }
      }
      if (!current()) throw stale();
      const received = decision?.operation;
      if (!received || received.requestId !== operation.requestId || typeof received.runId !== 'string' || !received.runId
        || received.revision !== operation.expectedRevision + 1 || (runId && received.runId !== runId)
        || typeof received.replayed !== 'boolean' || !Array.isArray(decision.visko)
        || !['stay', 'next_line', 'scene_done'].includes(decision.advance)) throw new Error('The guide returned an invalid operation; restart the lesson safely');
      runId = received.runId; revision = received.revision;
      return { decision, request, claimExecution() {
        if (!current()) throw stale();
        if (attempted.has(received.requestId)) return false;
        // Claim before sending any command: partial/uncertain execution cannot replay.
        attempted.add(received.requestId);
        if (attempted.size > 256) attempted.delete(attempted.values().next().value);
        return true;
      } };
    } finally { if (ownGeneration === generation) pending = false; }
  }
  return { turn, reset };
}
