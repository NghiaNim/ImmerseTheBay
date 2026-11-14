# AGENTS.md — ImmerseTheBay

Unity 6000.0.85f2 LTS project for XREAL Air 2 Ultra (6DoF AR glasses) + Beam Pro (Android). Read `docs/HANDOFF.md` and `docs/INFRASTRUCTURE.md` first.

## Ground rules
- Work only inside the files/folders your task brief (`docs/codex/*.md`) says you own. Need a change elsewhere? Write it under "Requests for owners" in your PR description instead of editing.
- Never edit: `Packages/manifest.json`, `ProjectSettings/`, `Assets/Editor/SensorDebugSceneBuilder.cs`, `Assets/Editor/XrealProjectSetup.cs`, `Assets/Scenes/`, `Assets/XR/` (owned by the integration stream).
- Never commit `Packages/com.xreal.xr.tar.gz` (proprietary SDK, gitignored), `Library/`, `Logs/`, `Builds/`, secrets or API keys.
- Commit the `.meta` file for every new asset/script. Unity generates them on import; if you cannot run Unity, create the script without a .meta and say so in the PR.
- One branch per task (name given in the brief), PR into `main`. Commit messages: imperative, no AI/tool attribution lines.

## Code conventions
- C# namespace `ImmerseTheBay.<Area>`; one feature per folder under `Assets/Scripts/`.
- Show module status on the in-glasses HUD with `DebugRegistry.Register(() => "...")` (register in OnEnable, unregister in OnDisable).
- Hands: read `ImmerseTheBay.Tracking.HandInput.Instance.Left/Right` (world-space pinch, index tip, palm). Don't poll XR Hands yourself.
- Data-driven content lives in `Assets/StreamingAssets/content.json` (see `ContentLibrary`).
- Background-thread callbacks (sockets, native SDK events) must marshal to the main thread via a `ConcurrentQueue<Action>` drained in `Update`.
- No new package dependencies without asking in the PR.

## Verify (macOS dev machine with Unity installed)
```bash
# compile check (must print no "error CS")
/Applications/Unity/Hub/Editor/6000.0.85f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit \
  -projectPath "$PWD" -buildTarget Android -logFile Logs/agent-compile.log; grep "error CS" Logs/agent-compile.log
# optional full APK build
/Applications/Unity/Hub/Editor/6000.0.85f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit \
  -projectPath "$PWD" -buildTarget Android -executeMethod BuildScript.BuildAndroid -logFile Logs/agent-build.log
```
Close the Unity editor GUI on that project first (batch mode cannot open a project that is already open).
If Unity is not available (e.g. cloud sandbox), keep changes compile-safe by sticking to APIs already used in the codebase, and state in the PR that the compile check was not run.
