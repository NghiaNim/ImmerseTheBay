#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: sh portal/deploy/stage.sh SOURCE_OPENING_CACHE EMPTY_STAGING_DIRECTORY" >&2
  exit 2
fi

cache="$(cd "$1" && pwd)"
stage="$(cd "$2" && pwd)"
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
if [ -n "$(ls -A "$stage")" ]; then
  echo "Staging directory must be empty." >&2
  exit 2
fi

# Archive only committed server code and authored course assets.
git -C "$root" archive HEAD portal Assets/StreamingAssets/lessons > "$stage/source.tar"
tar -xf "$stage/source.tar" -C "$stage"
rm "$stage/source.tar"
cp "$root/portal/deploy/Dockerfile" "$stage/Dockerfile"
mkdir "$stage/scene-openings"
# Preserve the source directory in the archive even without optional clips.
touch "$stage/scene-openings/.keep"

cd "$stage"
node --input-type=module - "$cache" <<'NODE'
import { copyFile } from 'node:fs/promises';
import path from 'node:path';
import { authoredLessonScenes, createLessonStore } from './portal/agent/lessons.js';
import { resolveOpeningClip } from './portal/agent/opening-clips.js';

const cache = process.argv[2];
const lessons = createLessonStore();
for (const { id } of await lessons.list()) {
  const lesson = await lessons.load(id);
  for (const scene of authoredLessonScenes(lesson)) {
    const clip = await resolveOpeningClip(lessons, cache, id, scene.id);
    if (clip) {
      await copyFile(clip, path.join('scene-openings', path.basename(clip)));
      console.log(`Staged ${id}/${scene.id}`);
    } else if (id === 'ja-cafe-demo-001') {
      throw new Error(`Missing judge demo clip for ${id}/${scene.id}`);
    }
  }
}
NODE
