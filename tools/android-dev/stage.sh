#!/usr/bin/env bash
set -euo pipefail

repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
dev="${ITB_DEV_PROJECT:-$repo/Builds/helloMR-baseline}"
if [[ "$(basename "$dev")" != "helloMR-baseline" ]]; then
  echo "Stage only into the isolated helloMR-baseline development project." >&2
  exit 1
fi
if [[ ! -f "$dev/ProjectSettings/ProjectVersion.txt" || ! -d "$dev/Assets/Editor" ]]; then
  echo "Prepare the isolated XREAL 3.1 development project first." >&2
  exit 1
fi

integration="$dev/Assets/ImmerseIntegration"
mkdir -p "$integration" "$dev/Assets/StreamingAssets/lessons" "$dev/Assets/Plugins/Android"
for source in \
  Assets/Scripts/Visko/{ViskoSession,ViskoSession.Openings,ViskoSession.Explore,AndroidSpeechToText}.cs \
  Assets/Scripts/Content/ContentLibrary.cs \
  Assets/Scripts/Tracking/HandInput.cs \
  Assets/Scripts/SensorDebug/DebugRegistry.cs \
  Assets/Scripts/Net/{NetClient,Protocol,RealtimeSession}.cs \
  Assets/Scripts/Audio/{MicrophoneCapture,PcmChunkReader}.cs \
  Assets/Scripts/Learning/{ISpeechToText,EditorSpeechToText,PortalSpeechToText,LearningAgentClient,LearningAgentClient.Explore,LearningContext,LearningContract,LearningOverlay}.cs \
  Assets/Scripts/Learning/Voice/CartesiaConversationClient.cs \
  Assets/Shaders/ImmersiveVisko.shader Assets/Materials/ImmersiveVisko.mat; do
  cp "$repo/$source" "$repo/$source.meta" "$integration/"
done

cp -R "$repo/Assets/Scripts/Learning/Resources" "$integration/"
cp -R "$repo/Assets/Plugins/Android/"{ViskoReactorBridge.kt,JapaneseSpeechRecognizer.kt,LearningPermissions.androidlib,mainTemplate.gradle} "$dev/Assets/Plugins/Android/"
cp -R "$repo/Assets/StreamingAssets/lessons/." "$dev/Assets/StreamingAssets/lessons/"
cp "$repo/tools/android-dev/DevReferenceBuild.cs" "$dev/Assets/Editor/"
cp "$repo/tools/android-dev/DevLessonControls.cs" "$integration/"
cp "$repo/tools/android-dev/PhonePracticeActivity.kt"{,.meta} "$dev/Assets/Plugins/Android/"
cp -R "$repo/tools/android-dev/PhonePractice.androidlib"{,.meta} "$dev/Assets/Plugins/Android/"
# Tenin phone UI (home screen) and the workflow behind it; fonts ship as library assets.
cp "$repo/tools/android-dev/"{PracticePortal,TeninWorkflow,TeninGlassesScreen}.kt "$dev/Assets/Plugins/Android/"
cp "$repo/tools/android-dev/tenin/"{TeninUi,TeninScreens,TeninActivity,TeninPronunciation}.kt "$dev/Assets/Plugins/Android/"
mkdir -p "$dev/Assets/Plugins/Android/PhonePractice.androidlib/assets/fonts"
cp "$repo/tools/android-dev/tenin/assets/fonts/"* "$dev/Assets/Plugins/Android/PhonePractice.androidlib/assets/fonts/"
