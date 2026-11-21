#!/usr/bin/env bash
# Records the phone's logs to a file for glasses-flow evals, and keeps recording across wireless-adb drops.
# Start it before a test session and stop it with Ctrl-C afterwards; then run flow_timeline.py on the file.
#
# Usage: tools/eval/capture-device-log.sh OUTPUT.log
# Env:   ADB (default: Unity's bundled adb), ADB_SERIAL (default: the only connected device)
set -uo pipefail

out=${1:?usage: capture-device-log.sh OUTPUT.log}
adb=${ADB:-/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}
serial=${ADB_SERIAL:-$("$adb" devices | awk 'NR > 1 && $2 == "device" { print $1; exit }')}
[[ -n "$serial" ]] || { echo "No device connected. Pair and connect wireless debugging first." >&2; exit 1; }
echo "Recording $serial to $out (Ctrl-C to stop)"

while true; do
  # Resume from the last recorded line so a reconnect leaves no gap.
  last=$(grep -aoE '^[0-9]{2}-[0-9]{2} [0-9:.]{12}' "$out" 2>/dev/null | tail -1)
  "$adb" -s "$serial" logcat -v threadtime -b main,system,crash,events ${last:+-T "$last"} >> "$out" 2>/dev/null
  echo "#### capture interrupted $(date '+%H:%M:%S'), reconnecting" >> "$out"
  sleep 3
  [[ "$serial" == *:* ]] && "$adb" connect "$serial" > /dev/null 2>&1
done
