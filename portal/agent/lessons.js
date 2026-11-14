import { readdir, readFile, realpath } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { hasBannedTerm } from './guardrails.js';
export const LESSON_ROOT = fileURLToPath(new URL('../../Assets/StreamingAssets/lessons/', import.meta.url));
export class AgentError extends Error { constructor(status, message) { super(message); this.status = status; } }
const plain = (v) => v && typeof v === 'object' && !Array.isArray(v);
const text = (s) => typeof s === 'string' && s.trim().length > 0 && s.length <= 2000;
export function flattenLesson(lesson) { return lesson.beats.flatMap((beat, beatIndex) => beat.lines.map((line) => ({ line, beat, beatIndex }))); }
export function validateLesson(lesson) {
  if (!plain(lesson) || !/^[a-z0-9][a-z0-9-]{0,79}$/.test(lesson.id) || !['title', 'level', 'scenePrompt', 'audioPrompt'].every((k) => text(lesson[k]))
      || typeof lesson.startImage !== 'string' || !Array.isArray(lesson.beats) || !lesson.beats.length || lesson.beats.length > 100) throw new AgentError(422, 'Invalid lesson metadata');
  for (const beat of lesson.beats) {
    if (!plain(beat) || !text(beat.goal) || !Array.isArray(beat.lines) || !beat.lines.length || beat.lines.length > 50 || !Array.isArray(beat.vocab)) throw new AgentError(422, 'Invalid lesson beat');
    if (beat.lines.some((line) => !plain(line) || !['jp', 'kana', 'romaji', 'zh', 'en', 'cue'].every((k) => text(line[k])))) throw new AgentError(422, 'Invalid lesson line');
    if (beat.vocab.some((v) => !plain(v) || !['jp', 'kana', 'en', 'objectHint'].every((k) => text(v[k])))) throw new AgentError(422, 'Invalid lesson vocabulary');
  }
  if (hasBannedTerm(JSON.stringify(lesson))) throw new AgentError(422, 'Lesson contains a blocked character or brand');
  return lesson;
}
export function createLessonStore(root = LESSON_ROOT) {
  async function resolvePublicFile(relative) {
    const base = await realpath(root);
    const requested = path.resolve(base, relative);
    const actual = await realpath(requested);
    // Refuse symlinks as well as traversal, including symlinks to private/.
    if (actual !== requested || !actual.startsWith(base + path.sep)) throw new AgentError(404, 'Not found');
    return actual;
  }
  async function load(id) {
    if (typeof id !== 'string' || !/^[a-z0-9][a-z0-9-]{0,79}$/.test(id)) throw new AgentError(400, 'Invalid lessonId');
    try {
      const lesson = validateLesson(JSON.parse(await readFile(await resolvePublicFile(`${id}.json`), 'utf8')));
      if (lesson.id !== id) throw new AgentError(422, 'Lesson id does not match filename');
      return lesson;
    } catch (e) { if (e.code === 'ENOENT') throw new AgentError(404, 'Lesson not found'); throw e; }
  }
  async function list() {
    let files;
    try { files = await readdir(root); } catch (e) { if (e.code === 'ENOENT') return []; throw e; }
    const lessons = [];
    for (const file of files.sort().filter((name) => /^[a-z0-9][a-z0-9-]*\.json$/.test(name))) {
      const lesson = await load(file.slice(0, -5));
      lessons.push({ id: lesson.id, title: lesson.title, level: lesson.level, startImage: lesson.startImage, lineCount: flattenLesson(lesson).length });
    }
    return lessons;
  }
  async function image(filename) {
    if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]*\.(jpg|jpeg|png|webp)$/i.test(filename)) throw new AgentError(404, 'Image not found');
    try { return await resolvePublicFile(`images/${filename}`); } catch (e) { if (e.code === 'ENOENT') throw new AgentError(404, 'Image not found'); throw e; }
  }
  return { load, list, image };
}
