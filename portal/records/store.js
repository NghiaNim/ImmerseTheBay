import { mkdir, readFile, writeFile, appendFile, readdir, rm } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { AgentError } from '../agent/lessons.js';
// Local-disk store for photos, session records and compiled replays. Keep the
// method surface small so a hosted database/object store can replace it.
export const DATA_ROOT = fileURLToPath(new URL('../data/', import.meta.url));
export const LOCAL_USER = 'local';
const PHOTO_ID = /^ph_[a-f0-9]{16}$/;
const RECORD_ID = /^[a-f0-9-]{36}$/;
const REPLAY_ID = /^rec-[a-f0-9]{12}$/;
const SCENARIO_ID = /^gen-[a-f0-9]{12}$/;
const FILE_MODE = { mode: 0o600 };
const notFound = (what) => new AgentError(404, `${what} not found`);

export function createRecordStore({ root = DATA_ROOT, now = () => new Date() } = {}) {
  const dir = (...parts) => path.join(root, ...parts);
  const readJson = async (file) => JSON.parse(await readFile(file, 'utf8'));
  const missing = (e) => e.code === 'ENOENT';
  async function writeJson(file, value) { await mkdir(path.dirname(file), { recursive: true }); await writeFile(file, JSON.stringify(value, null, 2), FILE_MODE); }

  // ---------------------------------------------------------------- photos
  async function putPhoto({ userId, jpeg, thumb, width, height }) {
    const id = `ph_${randomBytes(8).toString('hex')}`;
    const meta = { id, userId, width, height, bytes: jpeg.length, createdAt: now().toISOString() };
    await mkdir(dir('photos'), { recursive: true });
    await writeFile(dir('photos', `${id}.jpg`), jpeg, FILE_MODE);
    await writeFile(dir('photos', `${id}.thumb.jpg`), thumb, FILE_MODE);
    await writeJson(dir('photos', `${id}.json`), meta);
    return meta;
  }
  async function getPhoto(userId, id) {
    if (!PHOTO_ID.test(id)) throw notFound('Photo');
    try {
      const meta = await readJson(dir('photos', `${id}.json`));
      if (meta.userId !== userId) throw notFound('Photo');
      return meta;
    } catch (e) { if (missing(e)) throw notFound('Photo'); throw e; }
  }
  async function photoFile(userId, id, variant = 'image') {
    await getPhoto(userId, id);
    return dir('photos', variant === 'thumb' ? `${id}.thumb.jpg` : `${id}.jpg`);
  }
  async function listPhotos(userId) {
    let files;
    try { files = await readdir(dir('photos')); } catch (e) { if (missing(e)) return []; throw e; }
    const photos = [];
    for (const file of files.filter((f) => /^ph_[a-f0-9]{16}\.json$/.test(f))) {
      const meta = await readJson(dir('photos', file));
      if (meta.userId === userId) photos.push(meta);
    }
    return photos.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  }
  async function deletePhoto(userId, id) {
    await getPhoto(userId, id);
    await Promise.all(['.jpg', '.thumb.jpg', '.json'].map((suffix) => rm(dir('photos', id + suffix), { force: true })));
  }

  // ---------------------------------------------------------------- session records
  async function getRecord(userId, id) {
    if (!RECORD_ID.test(id)) throw notFound('Session');
    try {
      const record = await readJson(dir('sessions', id, 'record.json'));
      if (record.userId !== userId) throw notFound('Session');
      return record;
    } catch (e) { if (missing(e)) throw notFound('Session'); throw e; }
  }
  async function ensureRecord(fields, initialize = () => ({})) {
    if (!RECORD_ID.test(fields.id)) throw new Error('Invalid record id');
    const file = dir('sessions', fields.id, 'record.json');
    try { return await readJson(file); } catch (e) { if (!missing(e)) throw e; }
    const record = { status: 'active', ...fields, ...initialize(), startedAt: now().toISOString(), updatedAt: now().toISOString() };
    await writeJson(file, record);
    return record;
  }
  async function updateRecord(id, patch) {
    const file = dir('sessions', id, 'record.json');
    const record = { ...await readJson(file), ...patch, updatedAt: now().toISOString() };
    await writeJson(file, record);
    return record;
  }
  async function appendEvent(id, event) {
    await mkdir(dir('sessions', id), { recursive: true });
    await appendFile(dir('sessions', id, 'events.jsonl'), JSON.stringify({ at: now().toISOString(), ...event }) + '\n', FILE_MODE);
  }
  async function events(userId, id) {
    await getRecord(userId, id);
    try {
      return (await readFile(dir('sessions', id, 'events.jsonl'), 'utf8')).split('\n').filter(Boolean).map((line) => JSON.parse(line));
    } catch (e) { if (missing(e)) return []; throw e; }
  }
  async function listRecords(userId) {
    let ids;
    try { ids = await readdir(dir('sessions')); } catch (e) { if (missing(e)) return []; throw e; }
    const records = [];
    for (const id of ids.filter((name) => RECORD_ID.test(name))) {
      try { const record = await readJson(dir('sessions', id, 'record.json')); if (record.userId === userId) records.push(record); }
      catch (e) { if (!missing(e)) throw e; }
    }
    return records.sort((a, b) => b.startedAt.localeCompare(a.startedAt));
  }
  async function deleteRecord(userId, id) {
    await getRecord(userId, id);
    await rm(dir('sessions', id), { recursive: true, force: true });
  }

  // ---------------------------------------------------------------- compiled replays
  async function putReplay(userId, build) {
    const id = `rec-${randomBytes(6).toString('hex')}`;
    const lesson = await build(id);
    await writeJson(dir('replays', `${id}.json`), { userId, createdAt: now().toISOString(), lesson });
    return lesson;
  }
  async function getReplay(id) {
    if (!REPLAY_ID.test(id)) throw notFound('Lesson');
    try { return (await readJson(dir('replays', `${id}.json`))).lesson; }
    catch (e) { if (missing(e)) throw notFound('Lesson'); throw e; }
  }

  // ---------------------------------------------------------------- generated scenarios
  async function putScenario(userId, lesson) {
    if (!SCENARIO_ID.test(lesson.id)) throw new Error('Invalid scenario id');
    await writeJson(dir('scenarios', `${lesson.id}.json`), { userId, createdAt: now().toISOString(), lesson });
    return lesson;
  }
  async function readScenario(id) {
    if (!SCENARIO_ID.test(id)) throw notFound('Lesson');
    try { return await readJson(dir('scenarios', `${id}.json`)); }
    catch (e) { if (missing(e)) throw notFound('Lesson'); throw e; }
  }
  const getScenario = async (id) => (await readScenario(id)).lesson;
  async function listScenarios(userId) {
    let files;
    try { files = await readdir(dir('scenarios')); } catch (e) { if (missing(e)) return []; throw e; }
    const scenarios = [];
    for (const file of files.filter((f) => /^gen-[a-f0-9]{12}\.json$/.test(f))) {
      const entry = await readJson(dir('scenarios', file));
      if (entry.userId === userId) scenarios.push(entry);
    }
    return scenarios.sort((a, b) => b.createdAt.localeCompare(a.createdAt)).map(({ lesson, createdAt }) => ({
      id: lesson.id, title: lesson.title, level: lesson.level, startImage: lesson.startImage, partnerRole: lesson.partnerRole,
      request: lesson.scenario?.request || '', createdAt, lineCount: lesson.beats.reduce((n, b) => n + b.lines.length, 0),
    }));
  }
  async function deleteScenario(userId, id) {
    if ((await readScenario(id)).userId !== userId) throw notFound('Scenario');
    await rm(dir('scenarios', `${id}.json`), { force: true });
  }
  /** Stored lessons the turn engine can run: compiled replays and generated scenarios. */
  const getLesson = (id) => SCENARIO_ID.test(id) ? getScenario(id) : getReplay(id);

  return { putPhoto, getPhoto, photoFile, listPhotos, deletePhoto, ensureRecord, updateRecord, appendEvent, getRecord, events, listRecords, deleteRecord, putReplay, getReplay, putScenario, getScenario, listScenarios, deleteScenario, getLesson };
}

export const isReplayLesson = (id) => typeof id === 'string' && REPLAY_ID.test(id);
export const isStoredLesson = (id) => typeof id === 'string' && (REPLAY_ID.test(id) || SCENARIO_ID.test(id));

/** Lesson store that also resolves stored lessons (`rec-…` replays, `gen-…` scenarios) for the turn engine. */
export function withReplays(lessonStore, recordStore) {
  return { ...lessonStore, load: (id) => isStoredLesson(id) ? recordStore.getLesson(id) : lessonStore.load(id) };
}
