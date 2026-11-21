#!/usr/bin/env node
// Simulated learners against a running Portal, text turns only.
//
//   node tools/sim-learner/sim.mjs [--base http://localhost:8793] [--personas a,b] [--lessons ja-konbini-001,ja-home-001,photo]
//                                  [--concurrency 4] [--keep-records] [--no-photo]
//
// Never opens a Visko/Reactor or Cartesia session: the HTTP helper refuses
// /api/token, /api/voice/* and /api/agent/speak. Returned visko commands are
// recorded, never executed.
import { mkdir, readFile, writeFile, appendFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { PERSONAS } from './personas.mjs';
import { analyzeRun } from './analyze.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '../..');
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => {
  if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
  return acc;
}, []));
const BASE = (args.base || process.env.PORTAL_URL || 'http://localhost:8793').replace(/\/$/, '');
const TOKEN = process.env.PORTAL_TOKEN || '';
const CONCURRENCY = Number(args.concurrency || 4);
const personaNames = args.personas ? String(args.personas).split(',') : Object.keys(PERSONAS);
const lessonNames = args.lessons ? String(args.lessons).split(',') : ['ja-konbini-001', 'ja-home-001', ...(args['no-photo'] ? [] : ['photo'])];
const PHOTO = { file: 'tokyo-house-interior.jpg', goal: '我想问主人鞋子应该放在哪里，然后坐下喝茶' };

const FORBIDDEN = /^\/api\/(token|voice\b|agent\/speak)/;
async function http(method, route, body, timeoutMs = 90_000) {
  if (FORBIDDEN.test(route)) throw new Error(`sim-learner refuses ${route}: no Visko/Cartesia sessions`);
  const headers = { 'content-type': 'application/json', ...(TOKEN ? { portal_token: TOKEN } : {}) };
  const t0 = performance.now();
  const res = await fetch(BASE + route, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(timeoutMs) });
  const text = await res.text();
  const latencyMs = Math.round(performance.now() - t0);
  let json; try { json = JSON.parse(text); } catch { json = { raw: text.slice(0, 500) }; }
  return { status: res.status, json, latencyMs };
}

const runStamp = new Date().toISOString().replace(/[:.]/g, '-');
const RUN_DIR = path.join(HERE, 'runs', runStamp);

function lessonRun({ persona, lesson, lessonKind, file, imageRef: defaultRef = null }) {
  const sessionId = `practice-sim-${persona}-${lesson.id.slice(0, 20)}-${randomUUID().slice(0, 8)}`;
  const lines = lesson.beats.flatMap(b => b.lines);
  const st = { beat: 0, overlay: null, lastOverlay: null, route: null, done: false, lastAdvance: null, runId: undefined, revision: 0, seq: 0, imageRef: defaultRef, started: false };
  const records = [];
  async function turn(partial, meta = {}) {
    if (st.done && !meta.expectStatus) return null;
    const { imageRef, ...rest } = partial;
    if (imageRef !== undefined) st.imageRef = imageRef;
    const body = { sessionId, lessonId: lesson.id, beat: st.beat, context: {}, ...(st.imageRef ? { imageRef: st.imageRef } : {}), ...rest,
      operation: { requestId: `${sessionId}-${st.seq}`, ...(st.runId ? { runId: st.runId } : {}), expectedRevision: st.revision } };
    const before = { beat: st.beat, node: st.route, overlay: st.overlay, mainTarget: lines[st.beat] ? { jp: lines[st.beat].jp, kana: lines[st.beat].kana } : null };
    const t0 = Date.now();
    let res;
    try { res = await http('POST', '/api/agent/turn', body); }
    catch (e) { res = { status: 0, json: { error: String(e.message || e) }, latencyMs: Date.now() - t0 }; }
    const r = res.json;
    if (res.status === 200) {
      st.started = true;
      if (r.operation) { st.runId = r.operation.runId; st.revision = r.operation.revision; }
      st.route = r.route || null; st.lastAdvance = r.advance;
      if (r.advance === 'next_line') st.beat += 1; // contract: never increment on stay / detour / rejoin
      if (r.advance === 'scene_done' || r.overlay === null) st.done = true;
      st.overlay = r.overlay; if (r.overlay) st.lastOverlay = r.overlay;
    }
    const rec = { seq: st.seq++, persona, lessonId: lesson.id, lessonKind, sessionId, label: meta.label || '', meta: { ...meta, label: undefined },
      before, request: body, status: res.status, latencyMs: res.latencyMs, sentAt: new Date(t0).toISOString(), receivedAt: new Date().toISOString(),
      response: r, after: { beat: st.beat, node: st.route, done: st.done } };
    records.push(rec);
    await appendFile(file, JSON.stringify(rec) + '\n');
    process.stdout.write(`  ${persona.padEnd(11)} ${lesson.id.slice(0, 22).padEnd(22)} #${String(rec.seq).padStart(2)} ${res.status} ${String(res.latencyMs).padStart(5)}ms ${r.route?.kind || '-'}${r.route?.step ? ` ${r.route.step}/${r.route.total}` : ''} ${r.advance || r.error || ''} :: ${rec.label}\n`);
    return rec;
  }
  const ctx = {
    lesson, lessonKind,
    get beat() { return st.beat; }, get done() { return st.done; }, get lastAdvance() { return st.lastAdvance; },
    get overlay() { return st.overlay || st.lastOverlay || { jp: '', kana: '', romaji: '', zh: '', en: '' }; },
    get inDetour() { return st.route?.kind === 'detour'; },
    open: (extra = {}, meta = {}) => turn({ ...extra }, { label: 'opening turn', ...meta }),
    say: (transcript, meta = {}) => turn({ transcript, ...(meta.context ? { context: meta.context } : {}) }, meta),
    intent: (intent, meta = {}) => turn({ intent }, meta),
    pinch: (meta = {}) => turn({ context: { gesture: 'pinch' } }, { label: 'pinch', ...meta }),
    longPinch: (meta = {}) => turn({ context: { gesture: 'long_pinch' } }, { label: 'long pinch', ...meta }),
    action: (action, meta = {}) => turn({ action }, meta),
    raw: async (partial, meta = {}) => {
      // Send a deliberately inconsistent request without moving the client cursor.
      const saved = st.beat; const rec = await turn(partial, meta);
      if (rec?.status !== 200) st.beat = saved;
      return rec;
    },
  };
  return { sessionId, records, ctx, st };
}

async function pool(items, n, fn) {
  const queue = [...items]; const out = [];
  await Promise.all(Array.from({ length: Math.min(n, queue.length) }, async () => { while (queue.length) out.push(await fn(queue.shift())); }));
  return out;
}

async function joinDecisionLogs(sessionIds, since) {
  const dir = path.join(REPO, 'portal/agent/logs');
  const ids = new Set(sessionIds); const bySession = new Map();
  let files = [];
  try { files = (await readdir(dir)).filter(f => f.endsWith('.jsonl') && f.slice(0, 10) >= since.slice(0, 10)).sort(); } catch { return bySession; }
  for (const f of files) {
    for (const line of (await readFile(path.join(dir, f), 'utf8')).split('\n')) {
      if (!line.includes('practice-sim-')) continue;
      let e; try { e = JSON.parse(line); } catch { continue; }
      if (!ids.has(e.sessionId)) continue;
      if (!bySession.has(e.sessionId)) bySession.set(e.sessionId, []);
      bySession.get(e.sessionId).push({ at: e.at, source: e.source, failure: e.failure, intent: e.intent, graphNodes: e.graph?.nodes?.length, graphEdges: e.graph?.edges?.length });
    }
  }
  return bySession;
}

async function main() {
  const health = await http('GET', '/api/health');
  if (health.status !== 200) throw new Error(`Portal not healthy at ${BASE}: ${health.status}`);
  await mkdir(path.join(RUN_DIR, 'lessons'), { recursive: true });
  const startedAt = new Date().toISOString();
  console.log(`sim-learner → ${BASE}\nrun dir: ${RUN_DIR}`);

  const lessons = [];
  for (const id of lessonNames.filter(l => l !== 'photo')) {
    const r = await http('GET', `/api/agent/lessons/${id}`);
    if (r.status !== 200) { console.error(`lesson ${id}: ${r.status}`); continue; }
    lessons.push({ lesson: r.json, kind: 'bundled' });
  }
  let photo = null;
  if (lessonNames.includes('photo')) {
    const bytes = await readFile(path.join(REPO, 'Assets/StreamingAssets/lessons/images', PHOTO.file));
    const r = await http('POST', '/api/practice/generate', { image: `data:image/jpeg;base64,${bytes.toString('base64')}`, goal: PHOTO.goal }, 120_000);
    photo = { file: PHOTO.file, goal: PHOTO.goal, status: r.status, latencyMs: r.latencyMs, error: r.json.error || null, lessonId: r.json.lesson?.id || null, expiresAt: r.json.expiresAt || null };
    console.log(`photo practice: ${r.status} in ${r.latencyMs}ms ${r.json.lesson?.id || r.json.error || ''}`);
    if (r.status === 200) lessons.push({ lesson: r.json.lesson, kind: 'photo', imageRef: { id: `sim-photo:${PHOTO.file}`, caption: '' } });
  }
  for (const { lesson } of lessons) await writeFile(path.join(RUN_DIR, 'lessons', `${lesson.id}.json`), JSON.stringify(lesson, null, 2));

  const jobs = lessons.flatMap(l => personaNames.map(p => ({ persona: p, ...l })));
  const runs = await pool(jobs, CONCURRENCY, async ({ persona, lesson, kind, imageRef }) => {
    const file = path.join(RUN_DIR, `${kind === 'photo' ? 'photo' : lesson.id}__${persona}.jsonl`);
    const run = lessonRun({ persona, lesson, lessonKind: kind, file, imageRef });
    let crash = null;
    try { await PERSONAS[persona](run.ctx); } catch (e) { crash = String(e.stack || e); console.error(`  ${persona} on ${lesson.id} crashed: ${e.message}`); }
    return { persona, lessonId: lesson.id, lessonKind: kind, file, sessionId: run.sessionId, runId: run.st.runId, records: run.records, crash };
  });

  // Attach the Portal's own decision-log entries (source/failure) to each committed turn.
  const logs = await joinDecisionLogs(runs.map(r => r.sessionId), startedAt);
  for (const run of runs) {
    const entries = logs.get(run.sessionId) || [];
    let i = 0;
    for (const rec of run.records) if (rec.status === 200 && !rec.response.operation?.replayed) rec.decision = entries[i++] || null;
    await writeFile(run.file, run.records.map(r => JSON.stringify(r)).join('\n') + '\n');
  }

  // Remove the saved-session records these synthetic runs created, unless asked to keep them.
  const cleanup = [];
  if (!args['keep-records']) for (const run of runs) if (run.runId) {
    const r = await http('DELETE', `/api/sessions/${encodeURIComponent(run.runId)}`);
    cleanup.push({ runId: run.runId, status: r.status });
  }
  const meta = { base: BASE, startedAt, finishedAt: new Date().toISOString(), personas: personaNames, lessons: lessons.map(l => ({ id: l.lesson.id, kind: l.kind })), photo,
    runs: runs.map(({ records, ...r }) => ({ ...r, turns: records.length })), recordCleanup: args['keep-records'] ? 'kept' : cleanup, visko: 'never connected; commands recorded only' };
  await writeFile(path.join(RUN_DIR, 'meta.json'), JSON.stringify(meta, null, 2));
  const { markdown } = await analyzeRun(RUN_DIR);
  console.log('\n' + markdown);
}
main().catch(e => { console.error(e); process.exitCode = 1; });
