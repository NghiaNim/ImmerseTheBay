# Sensor HUD and CSV recording

The collector attaches `SensorLogger` at runtime if its GameObject has no logger. Existing generated scenes need no changes. The component starts idle; enable `logOnEnable` in the inspector to start recording whenever the component is enabled.

## Controls and integration

- `PageUp`: previous page. `PageDown` or `Tab`: next page. `Up`/`Down`: scroll long pages.
- Right-hand quick pinch over the labeled `< PREV` / `NEXT >` zones at the lower-right of the HUD: previous / next page. A pinch must begin and end within 0.25 m of the panel, remain in the navigation strip, and finish within `quickPinchSeconds` (default 0.4 s). A pinch begun elsewhere or held too long cannot turn a page by moving into the strip. This uses the brief's proximity guard; `HandInput` currently has no explicit content-ownership flag.
- `SensorDataHud.NextPage()`, `PrevPage()`, and `SetPage(string)` can be wired by the menu owner. Names are case insensitive: Overview, Hands, AR, Devices, Sensors, Modules. Unknown names do nothing.
- `L` or `SensorLogger.SetLogging(bool)` toggles recording independently of the HUD GameObject's visibility. The logger lives with the collector. Modules shows recording/stopping/off status, file path, row count, and errors via `DebugRegistry`.
- `sampleIntervalMs` defaults to 100 ms and uses unscaled time. Slow frames produce one current sample rather than a burst of catch-up rows.

Overview shows system, head and providers registered from `ImmerseTheBay.Xreal`; Modules still shows all registry providers. All existing collector section properties and `DebugRegistry.Build()` remain available.

## CSV schema and storage

Each recording creates `Application.persistentDataPath/sensorlogs/yyyyMMdd_HHmmss.csv`. Names use UTC. Same-second recordings append `_1`, `_2`, etc.; earlier files are never overwritten. Files are UTF-8 without a BOM, with invariant decimal points and standard CSV quoting.

There are 39 fixed columns, in this exact order:

```text
timestamp_utc,elapsed_seconds,head_position_x_m,head_position_y_m,head_position_z_m,head_rotation_x,head_rotation_y,head_rotation_z,head_rotation_w,head_velocity_x_m_s,head_velocity_y_m_s,head_velocity_z_m_s,left_tracked,left_wrist_x_m,left_wrist_y_m,left_wrist_z_m,left_index_tip_x_m,left_index_tip_y_m,left_index_tip_z_m,left_thumb_tip_x_m,left_thumb_tip_y_m,left_thumb_tip_z_m,left_pinch_cm,right_tracked,right_wrist_x_m,right_wrist_y_m,right_wrist_z_m,right_index_tip_x_m,right_index_tip_y_m,right_index_tip_z_m,right_thumb_tip_x_m,right_thumb_tip_y_m,right_thumb_tip_z_m,right_pinch_cm,ar_session_state,plane_count,mesh_chunk_count,mesh_vertex_count,anchor_count
```

Head position and quaternion rotation come from the XR head device in tracking space; the editor falls back to the main camera's local pose when no XR head device exists. Velocity is the XR device velocity in metres/second. Missing pose/velocity values are empty cells. Head quaternion components are x/y/z/w, not Euler angles.

Each hand has a 0/1 tracked flag; wrist, index tip and thumb tip are world positions in metres, using the existing collector hand subsystem and HandInput's tracking-space transform. Unavailable joints/pinch distance are empty cells. Pinch distance is centimetres. A missing AR manager records a count of zero; AR session state is the AR Foundation enum name.

After the fixed columns, one `sensor:<control.path>` column is added for every control (including parent and leaf controls) of every Input System `Sensor` present when recording starts, sorted by ordinal control path. For example, `sensor:/Accelerometer/acceleration` contains `x y z`, while its child x/y/z columns each contain a scalar. Sensor quaternions contain `x y z w`. Other numeric controls use invariant formatting. Unreadable controls and removed/disabled devices retain empty cells. Devices appearing later are included only in the next recording, so the current header never changes.

All Unity/Input System reads happen on the main thread. A dedicated worker owns file creation, writes and flushing every second. The queue holds at most 1024 rows; if full, recording stops with a visible error instead of blocking a frame or growing without bound. Row count is the number of data rows written to the writer (excluding the header); up to one second can still be buffered. Stopping drains all accepted rows. Disable, quit and application pause wait for the worker to flush and close; resume starts a new file if recording was active before pause. Ordinary L/API stops finish asynchronously.

Pull device logs with:

```sh
adb pull /sdcard/Android/data/com.immersethebay.app/files/sensorlogs
```

## Validation and coordinator handoff

No Unity process was launched. A standalone Roslyn compile of the changed SensorDebug scripts and existing HandInput passed against the installed Unity 6000.0.85f2 Android/package references. A temporary .NET harness exercised the actual CSV helpers and background writer: page wraparound/name selection, registry filtering, escaping, decimal-comma locale, 39 fixed columns, empty hand fields, 2000-row queue drains, same-second filenames, periodic flushing, row counts and main-thread completion dispatch all passed. These checks do not replace the coordinator's Unity import/compile and Play mode checks.

Coordinator checks: import new assets, run the compile check from AGENTS.md, then verify page navigation and L recording in Play mode without a device. On hardware, check navigation-zone reach/readability, pinch isolation while manipulating content, sensor values and Android pause/resume.

Requests for owners: Unity was intentionally not run, so `SensorLogger.cs` and this README have no generated `.meta` files; generate and commit them during import. Menu integration is optional through the public APIs above. No changes to other owners' files are required.
