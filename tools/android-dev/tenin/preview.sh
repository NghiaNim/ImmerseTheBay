#!/usr/bin/env bash
# Builds the Tenin phone screens alone into a small preview APK (no Unity, no Gradle), so the
# layouts can be checked on a device in about a minute. The real app gets the same sources
# through tools/android-dev/stage.sh.
#
# Usage: tools/android-dev/tenin/preview.sh [OUTPUT.apk]
# Then:  adb install -r OUTPUT.apk
#        adb shell am start -n com.immersethebay.tenin.preview/com.immersethebay.phone.TeninActivity --ez demo true [--es screen review]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "$here" rev-parse --show-toplevel)
output=${1:-$repo/Builds/tenin-preview.apk}
unity_android=${UNITY_ANDROID:-/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer}
java="$unity_android/OpenJDK/bin/java"
export JAVA_HOME="$unity_android/OpenJDK" PATH="$unity_android/OpenJDK/bin:$PATH"
gradle_lib="$unity_android/Tools/gradle/lib"
build_tools=$(ls -d "$unity_android"/SDK/build-tools/* | sort -V | tail -1)
platform="$unity_android/SDK/platforms/android-35/android.jar"
keystore=${KEYSTORE:-$HOME/.android/debug.keystore}
stdlib=$(ls "$gradle_lib"/kotlin-stdlib-[0-9]*.jar | head -1)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/classes" "$work/dex" "$work/assets/lessons"

"$java" -cp "$gradle_lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
  -no-stdlib -no-reflect -jvm-target 17 -Xjdk-release=17 -nowarn \
  -classpath "$platform:$stdlib" -d "$work/classes" "$here"/*.kt "$here"/preview/*.kt \
  "$here"/../TeninWorkflow.kt "$here"/../PracticePortal.kt "$here"/../TeninGlassesScreen.kt

"$build_tools/d8" --release --min-api 29 --lib "$platform" --output "$work/dex" \
  $(find "$work/classes" -name '*.class') "$stdlib"

cp -R "$here/assets/." "$work/assets/"
cp "$repo"/Assets/StreamingAssets/lessons/*.json "$work/assets/lessons/"
config="$repo/Builds/helloMR-baseline/Assets/StreamingAssets/visko.json"
[[ -f "$config" ]] && cp "$config" "$work/assets/visko.json"

cat > "$work/AndroidManifest.xml" <<'XML'
<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.immersethebay.tenin.preview">
    <uses-sdk android:minSdkVersion="29" android:targetSdkVersion="35" />
    <uses-permission android:name="android.permission.INTERNET" />
    <queries><intent><action android:name="android.intent.action.TTS_SERVICE" /></intent></queries>
    <application android:label="Tenin preview" android:debuggable="true" android:usesCleartextTraffic="true">
        <activity android:name="com.immersethebay.phone.TeninActivity" android:exported="true"
            android:theme="@android:style/Theme.Material.Light.NoActionBar" android:windowSoftInputMode="adjustResize">
            <intent-filter>
                <action android:name="android.intent.action.MAIN" />
                <category android:name="android.intent.category.LAUNCHER" />
            </intent-filter>
        </activity>
        <receiver android:name="com.immersethebay.phone.TeninSnapshot" android:exported="true" />
    </application>
</manifest>
XML

"$build_tools/aapt2" link -o "$work/base.apk" --manifest "$work/AndroidManifest.xml" -I "$platform" -A "$work/assets"
(cd "$work/dex" && zip -q "$work/base.apk" classes.dex)
"$build_tools/zipalign" -f 4 "$work/base.apk" "$work/aligned.apk"
mkdir -p "$(dirname "$output")"
"$build_tools/apksigner" sign --ks "$keystore" --ks-pass pass:android --out "$output" "$work/aligned.apk"
echo "Built $output"
