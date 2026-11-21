// Import a reviewed Reactor-generated source. ffmpeg and ffprobe must be on PATH.
import { execFileSync } from 'node:child_process';
import { mkdir, rename, rm, stat } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { createLessonStore } from './lessons.js';
import { MAX_OPENING_BYTES, OPENING_CLIP_ROOT, openingClipKey } from './opening-clips.js';

const [lessonId, sceneId, source, offset = '0'] = process.argv.slice(2);
if (!lessonId || !sceneId || !source || !/^\d+(?:\.\d{1,3})?$/.test(offset))
  throw new Error('Usage: node portal/agent/publish-opening-clip.js <lesson-id> <scene-id> <reviewed-source.mp4> [offset-seconds]');
const key = await openingClipKey(createLessonStore(), lessonId, sceneId);
if (!key) throw new Error('Choose a scene from an authored lesson');
await mkdir(OPENING_CLIP_ROOT, { recursive: true });

const target = path.join(OPENING_CLIP_ROOT, `${key}.mp4`);
const temp = path.join(OPENING_CLIP_ROOT, `.${randomUUID()}.mp4`);
try {
  execFileSync('ffmpeg', [
    '-nostdin', '-v', 'error', '-ss', offset, '-i', path.resolve(source), '-t', '6',
    '-map', '0:v:0', '-an', '-vf', 'scale=960:540:force_original_aspect_ratio=increase,crop=960:540,setsar=1,fps=24',
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '22', '-movflags', '+faststart', temp,
  ], { stdio: 'inherit' });
  const duration = Number(execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration',
    '-of', 'default=noprint_wrappers=1:nokey=1', temp], { encoding: 'utf8' }).trim());
  if (!(duration >= 2 && duration <= 6.2) || (await stat(temp)).size > MAX_OPENING_BYTES)
    throw new Error('Opening clip must be 2–6 seconds and at most 8 MiB');
  await rename(temp, target);
  console.log(`Published ${lessonId}/${sceneId}: ${target}`);
} finally {
  await rm(temp, { force: true });
}
