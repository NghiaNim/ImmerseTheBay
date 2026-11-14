# ImmerseTheBay — Handoff (2026-11-14)

Unity 6 (6000.0.85f2 LTS) + XREAL Air 2 Ultra (6DoF) + Beam Pro. Architecture and module list: [INFRASTRUCTURE.md](INFRASTRUCTURE.md).

## Current state
| Item | State |
|---|---|
| Repo | `main` on github.com/NghiaNim/ImmerseTheBay (public). |
| Mac toolchain | Unity **6000.0.85f2 LTS** (XREAL SDK 3.1 supports 6000.0.x LTS; 6000.5 is not LTS) with Android Build Support, SDK/NDK, OpenJDK. Unity Hub signed in with an activated Personal license. `adb` present. |
| XREAL SDK | 3.1.0, referenced as `"com.xreal.xr": "file:com.xreal.xr.tar.gz"`. The tarball is **gitignored** (proprietary, public repo): each developer downloads it from developer.xreal.com/download and drops it in `Packages/`. |
| Compilation | Clean under 6000.0.85f2 with AR Foundation 6.1.1 / XR Hands 1.5.1 / XRI 3.0.8 / Input System 1.14 / glTFast 6.8 / XREAL 3.1.0. XREAL loader enabled for Android, debug scene generated, APK build via `BuildScript`. |
| Device test | Never run on hardware. |

Batch mode works once the Hub license is active, e.g. `Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildScript.BuildAndroid`.

## First run checklist (anyone)
1. Download `com.xreal.xr.tar.gz` (XREAL SDK for Unity 3.1.0) into `Packages/`.
2. Pull, open in Unity Hub with 6000.0.85f2 LTS. Accept "enable new Input System backend / restart" if asked.
3. The setup script ticks XREAL under XR Plug-in Management > Android automatically. Run Project Validation > Fix All once.
4. XREAL settings (defaults are fine): Tracking `MODE_6DOF`, Input Source `Hands`, Support Devices includes `XREAL_DEVICE_CATEGORY_REALITY`.
5. **Tools > XREAL > Import Official Samples** (XRI Starter Assets + Hands Interaction Demo, XR Hands HandVisualizer, XREAL Interaction Basics + AR Features; XREAL samples are gitignored).
6. **Tools > XREAL > Create Sensor Debug Scene** (re-run after pulling builder changes), then **Tools > XREAL > Build APK** -> `adb install -r Builds/ImmerseTheBay.apk`.

## Rig
The scene uses XREAL's official `Packages/com.xreal.xr/Runtime/Prefabs/XR Interaction Hands Setup.prefab` (XRI hands + controller interactors, EventSystem, Input Action Manager, XR Interaction Manager) plus `NotificationListener`. Our AR managers and modules are attached to its XR Origin; `HandInput` sits on its Camera Offset. The builder falls back to a minimal hand-made origin if the prefab is missing.

## XREAL SDK facts found in source (3.1.0)
- Anchors: XREAL implements the AR Foundation 6 persistent-anchor API (`TrySaveAnchorAsync`, `TryLoadAnchorAsync`, `TryEraseAnchorAsync`) but does **not** set the descriptor `supports*` flags, so never gate on them. `TryRemap` / `GetAnchorQuality` are XREAL extension methods on `ARAnchorManager`.
- Image tracking: `supportsMutableLibrary = false`. Use `Assets/Settings/ReferenceImages.asset` (image names must match `markers[].name` in content.json).
- Runtime API: `XREALPlugin.SetInputSource / SwitchInputSource / SwitchTrackingTypeAsync / GetDeviceType / IsHandTrackingSupported`, events in `XREALCallbackHandler`, mesh labels via `XRMeshSubsystem.GetFaceClassifications`. Wrapped in `Assets/Scripts/Xreal/XrealFeatures.cs`.

## Parallel workstreams
Each stream owns its files. Touch another stream's files only via a small, announced change. Shared files (owner = Stream A): `Assets/Editor/SensorDebugSceneBuilder.cs`, `Assets/Editor/XrealProjectSetup.cs`, `Packages/manifest.json`, `docs/INFRASTRUCTURE.md`.

### A. Build, SDK & integration (owner of shared files)
- Done: clean compile on LTS, SDK imported, XREAL loader auto-enabled, XREAL features wrapped in `Scripts/Xreal/XrealFeatures.cs`, APK build script.
- Remaining XREAL items: Project Validation > Fix All review, virtual controller custom buttons (`XREALVirtualController` / `XREALButton`), notification popup UI, mesh classification colouring (labels are already counted on the HUD), RGB camera / recording (One + Eye only).
- First device build; record working settings in INFRASTRUCTURE.md.
- Done = APK runs on Beam Pro, HUD shows head pose + hands.

### B. Sensors & HUD — `Assets/Scripts/SensorDebug/`
- Verify every HUD section on device (head, hands, planes, mesh, anchors, Android sensors, display).
- Readability: paging/tabs, font size, toggle sections. Add CSV/JSON logging of samples to `persistentDataPath`.
- Done = all sections populated on device, logging toggle works.

### C. Content placement & interaction — `Assets/Scripts/Content/`, `Assets/StreamingAssets/content.json`
- Device-test grab / far-grab / two-hand scale; tune pinch thresholds (`HandInput`), poke depth (`PokeMenu`).
- Video controls (play/pause/seek/volume) on screens; loading indicator; model shader check in builds (glTFast shaders are added to Always Included by the scene builder).
- Optional: swap own manipulation for XRI interactors once XREAL "XR Interaction Hands Setup" prefab is in.
- Done = place video + model, move/scale them, save layout, relaunch, they return in place.

### D. Anchors & image tracking — `Assets/Scripts/Anchors/`, `Assets/Scripts/Tracking/ImageTrackingController.cs`, `RecenterController.cs`
- Device-test save/load/erase; measure relocalisation time and drift.
- Add real marker images to `StreamingAssets/markers/` + `markers` entries; confirm runtime library is supported on XREAL (else create an `XRReferenceImageLibrary` asset path).
- Done = anchors persist across launches; marker spawns its content.

### E. Media, audio, mic — `Assets/Scripts/Media/`, `Assets/Scripts/Audio/`, `Assets/Shaders/`
- Test SBS / TB stereo with real spatial videos (check eye order, `swapEyes`), 180/360 sphere playback (new mesh + shader mode).
- Verify which mic Android uses with glasses attached; mic -> network streaming hook (PCM frames to `NetClient.SendBinary`).
- Done = stereo video looks correct in glasses, waveform reacts to music and voice.

### F. Networking & backend — `Assets/Scripts/Net/`
- Replace `wss://echo.websocket.org` with the team server; define message schema (JSON envelope `{type, ...}`).
- Reconnect/latency check on Beam Pro over Wi-Fi; optional binary audio/video streaming.
- Visko: no public API found (visko.ai has only browser demos). Integrate once the team has docs/keys; keep keys out of git (local config file ignored by `.gitignore`).
- Done = app exchanges real-time messages with team server, RTT shown on HUD.

## Conventions
- Namespaces: `ImmerseTheBay.<Area>`. One feature per folder under `Assets/Scripts/`.
- Every module may expose status via `DebugRegistry.Register(() => "...")` so it shows on the HUD.
- Data-driven content goes in `StreamingAssets/content.json`, not hard-coded.
- Scene is generated by `SensorDebugSceneBuilder`; prefer adding components there over hand-editing the scene (avoids scene merge conflicts). If you hand-edit, own the scene for that PR.
- Branch per stream (`feat/<stream>-<topic>`), PR into `main`. Commit `.meta` files with their assets.
- Don't commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`, APKs, or secrets.
