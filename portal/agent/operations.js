import { createHash } from 'node:crypto';
import { AgentError } from './lessons.js';

const plain = (v) => v && typeof v === 'object' && !Array.isArray(v);
const identifier = (v) => typeof v === 'string' && v.length > 0 && v.length <= 160 && /^[a-zA-Z0-9_.:-]+$/.test(v);
export function validateOperation(value) {
  if (!plain(value) || Object.keys(value).some((key) => !['requestId', 'runId', 'expectedRevision'].includes(key))
    || !identifier(value.requestId) || !Number.isSafeInteger(value.expectedRevision) || value.expectedRevision < 0
    || (value.runId !== undefined && !identifier(value.runId))) throw new AgentError(400, 'Invalid operation envelope');
}
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (plain(value)) return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])]));
  return value;
}
export function operationFingerprint(request) {
  return createHash('sha256').update(JSON.stringify(canonical(request))).digest('hex');
}
export function createOperationCache() { return { responses: new Map(), retired: new Map() }; }
function retire(cache, id, lessonId) {
  cache.retired.set(id, lessonId);
  while (cache.retired.size > 256) cache.retired.delete(cache.retired.keys().next().value);
}
export function replayOperation(cache, request) {
  if (!request.operation) return null;
  const id = request.operation.requestId;
  if (cache.retired.has(id)) throw new AgentError(409, 'Operation expired or invalidated; read the lesson snapshot');
  const cached = cache.responses.get(id);
  if (!cached) return null;
  if (cached.fingerprint !== operationFingerprint(request)) throw new AgentError(409, 'Request ID was already used with different input');
  const result = structuredClone(cached.response);
  result.operation.replayed = true;
  return result;
}
export function checkOperationState(request, state) {
  const operation = request.operation;
  if (!operation) return;
  if (!state) {
    if (operation.runId !== undefined || operation.expectedRevision !== 0) throw new AgentError(409, 'Lesson run is unavailable; read the lesson snapshot');
  } else if (operation.runId !== state.flow.runId || operation.expectedRevision !== state.revision) {
    throw new AgentError(409, 'Lesson revision changed; read the lesson snapshot');
  }
}
export function rememberOperation(cache, request, response) {
  if (!request.operation) return;
  cache.responses.set(request.operation.requestId, { lessonId: request.lessonId, fingerprint: operationFingerprint(request), response: structuredClone(response) });
  while (cache.responses.size > 64) {
    const id = cache.responses.keys().next().value;
    retire(cache, id, cache.responses.get(id).lessonId);
    cache.responses.delete(id);
  }
}
export function invalidateOperations(cache, lessonId) {
  for (const [id, entry] of cache.responses) if (entry.lessonId === lessonId) {
    retire(cache, id, lessonId);
    cache.responses.delete(id);
  }
}
