import { createHash } from 'node:crypto';
import { readFile, realpath, stat } from 'node:fs/promises';
import path from 'node:path';
import { DATA_ROOT } from '../records/store.js';
import { authoredLessonScenes, authoredSceneId } from './lessons.js';

export const OPENING_CLIP_ROOT = path.resolve(DATA_ROOT, 'scene-openings');
export const MAX_OPENING_BYTES = 8 * 1024 * 1024;

export async function openingClipKey(store, lessonId, sceneId) {
  if (!/^[a-z0-9][a-z0-9-]{0,79}$/.test(lessonId) ||
      /^(?:practice|replay)-/.test(lessonId) || !authoredSceneId.test(sceneId)) return null;
  const lesson = await store.load(lessonId);
  const scene = authoredLessonScenes(lesson).find(scene => scene.id === sceneId);
  if (!scene) return null;
  const source = await readFile(await store.image(`${scene.id}.jpg`));
  return createHash('sha256')
    .update(JSON.stringify({ version: 1, model: 'reactor/visko-orbis-dynamic', lessonId, scene }))
    .update(source)
    .digest('hex');
}

export async function resolveOpeningClip(store, root, lessonId, sceneId) {
  try {
    const key = await openingClipKey(store, lessonId, sceneId);
    if (!key) return null;
    const filename = path.join(await realpath(root), `${key}.mp4`);
    if (await realpath(filename) !== filename) return null;
    const info = await stat(filename);
    return info.isFile() && info.size > 0 && info.size <= MAX_OPENING_BYTES ? filename : null;
  } catch (error) {
    if (error.code === 'ENOENT' || error.status === 404) return null;
    throw error;
  }
}
