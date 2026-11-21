#!/bin/sh
set -eu

: "${PORTAL_TOKEN:?Set PORTAL_TOKEN before starting the hosted Portal}"

mkdir -p data/scene-openings
for clip in /app/scene-openings/*.mp4; do
  [ -f "$clip" ] || continue
  destination="data/scene-openings/${clip##*/}"
  [ -e "$destination" ] && continue
  cp "$clip" "$destination.tmp"
  mv "$destination.tmp" "$destination"
done

exec node server.js
