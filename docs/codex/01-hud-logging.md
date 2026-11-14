# Codex task 1 — HUD pages + sensor data logging

Branch: `feat/hud-pages-logging`

## Context
`Assets/Scripts/SensorDebug/SensorDataCollector.cs` gathers every sensor/XR value into text sections; `SensorDataHud.cs` prints them in a head-following world-space panel (3 fixed columns, legacy `UnityEngine.UI.Text`). `DebugRegistry` lets other modules add text (shown as `SectionExtras`). On device the panel is too dense to read and nothing is recorded for later analysis.

## You own
- `Assets/Scripts/SensorDebug/**` (you may add new files there)

## Do
1. **Pages**: replace the fixed 3-column layout with pages: `Overview` (system + head + XREAL lines), `Hands`, `AR` (session, planes, mesh, anchors, images), `Devices` (XR input devices), `Sensors` (Android sensors), `Modules` (DebugRegistry output). Keep a one-line header showing page name, index/total and FPS.
2. **Page switching without touching other files**: next/prev via (a) right-hand quick pinch while the hand is in the lower-right of the view (read `HandInput.Instance`; must not fire while `HandInput` reports a pinch that started on content — simplest: require the pinch point to be within 0.25 m of the HUD panel), (b) keyboard `PageUp`/`PageDown` and `Tab` in the editor. Add public `NextPage()/PrevPage()/SetPage(string)` so the menu owner can wire a button later.
3. **Logging**: new `SensorLogger` component. When enabled, every N ms (default 100) append one row per sample to `Application.persistentDataPath/sensorlogs/<yyyyMMdd_HHmmss>.csv`: timestamp, head pos/rot, head velocity, each hand tracked flag + wrist/index-tip/thumb-tip positions + pinch cm, ARSession state, plane count, mesh chunk/vertex count, anchor count, and every Input System sensor control value (stable column order decided at start). Write on a background thread or buffered (flush every second) so the frame time is not affected. Toggle with keyboard `L` and a public `SetLogging(bool)`. Show logging state + file path + row count via `DebugRegistry`.
4. Keep `SensorDataCollector`'s public section properties working (other code may read them).

## Done when
- Compile check in `AGENTS.md` passes with no `error CS`.
- In Play mode in the editor (no device), pages switch with PageUp/PageDown, and pressing L creates a CSV with a header and rows.
- PR description lists the CSV columns and how to pull logs: `adb pull /sdcard/Android/data/com.immersethebay.app/files/sensorlogs`.
