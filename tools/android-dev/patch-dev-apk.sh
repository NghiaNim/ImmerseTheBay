#!/usr/bin/env bash
# Reapply the local Air 2 Ultra display-name patch (tools/controlglasses/README.md)
# to a fresh dev APK and sign it with the local Android debug key, so it updates
# the installed com.immersethebay.app in place. Only classes.dex changes.
#
# Usage: tools/android-dev/patch-dev-apk.sh BUILT.apk OUTPUT.apk
# Env:   APKTOOL_JAR (Apktool 3.0.3 jar), PYTHON (3.11+), UNITY_ANDROID (Unity AndroidPlayer dir)
set -euo pipefail

input=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
output=$2
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
unity_android=${UNITY_ANDROID:-/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer}
export JAVA_HOME="$unity_android/OpenJDK" PATH="$unity_android/OpenJDK/bin:$PATH" # apksigner needs java
java="$JAVA_HOME/bin/java"
build_tools=$(ls -d "$unity_android"/SDK/build-tools/* | sort -V | tail -1)
apktool=${APKTOOL_JAR:-$HOME/tools-itb/apktool_3.0.3.jar}
python=${PYTHON:-python3}
keystore=${KEYSTORE:-$HOME/.android/debug.keystore}

[[ -f "$input" && -f "$apktool" && -f "$keystore" ]] || { echo "Missing input APK, Apktool jar or keystore." >&2; exit 1; }
"$python" -c 'import sys; sys.exit(sys.version_info < (3, 11))' || { echo "Python 3.11+ required (set PYTHON)." >&2; exit 1; }
[[ "$(shasum -a 256 "$apktool" | cut -d' ' -f1)" == dbf930b076c6b9be08d57c449cacefc3bdd6b71ebd59b3066fc0e1f5b14f9423 ]] \
  || { echo "Unexpected Apktool jar; use the official 3.0.3 release." >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
source_sha=$(shasum -a 256 "$input" | cut -d' ' -f1)

"$java" -jar "$apktool" d --no-res -o "$work/decoded" "$input" >/dev/null
"$python" "$repo/tools/controlglasses/patch_display.py" "$work/decoded"
"$java" -jar "$apktool" b "$work/decoded" -o "$work/rebuilt.apk" >/dev/null
"$python" "$repo/tools/controlglasses/repack.py" "$input" "$work/rebuilt.apk" classes.dex "$work/unsigned.apk" --source-sha256 "$source_sha"
"$build_tools/zipalign" -P 16 -f 4 "$work/unsigned.apk" "$work/aligned.apk"
"$build_tools/apksigner" sign --ks "$keystore" --ks-pass pass:android --ks-key-alias androiddebugkey --key-pass pass:android --out "$output" "$work/aligned.apk"
"$build_tools/apksigner" verify --print-certs "$output" | grep -E "SHA-256 digest"
"$build_tools/zipalign" -c -P 16 4 "$output"
echo "Patched and signed: $output ($(shasum -a 256 "$output" | cut -d' ' -f1))"
