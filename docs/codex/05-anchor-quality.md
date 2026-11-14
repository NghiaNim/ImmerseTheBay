# Codex task 5 — Spatial anchor mapping-quality UX (XREAL)

Branch: `feat/anchor-quality`

## Context
`Assets/Scripts/Anchors/SpatialAnchorController.cs` places/saves/restores anchors (AR Foundation 6 persistent API, implemented by `XREALAnchorSubsystem`; never gate on descriptor `supports*` flags — XREAL leaves them false) and has `RemapAll()` via the XREAL extension `ARAnchorManager.TryRemap`. Saving fails or relocalises poorly if the user hasn't scanned enough. XREAL's official "Anchors" sample has a `MapQualityIndicator` that guides the user using `ARAnchorManager.GetAnchorQuality(trackableId, estimatePose)` → `XREALAnchorEstimateQuality`.

Reference (read-only): extract the SDK tarball to a temp dir and read `package/Samples~/AR Features/Anchors/Scripts/Anchors/QualityIndicator/*.cs` and `AnchorInfo.cs`:
`mkdir -p /tmp/xrealsdk && tar -xzf Packages/com.xreal.xr.tar.gz -C /tmp/xrealsdk` (the tarball is in your worktree's `Packages/`). Do not copy sample files into the repo verbatim; write our own implementation.

## You own
- `Assets/Scripts/Anchors/**` (new files allowed)

## Do
1. `AnchorQualityGuide` component: after an anchor is placed (subscribe to a new `SpatialAnchorController.AnchorPlaced` event you add), show a ring of 12–16 segments around the anchor (procedural mesh or primitives) whose segments fill as the user views the anchor from that direction with quality `GOOD` (poll `GetAnchorQuality` with the head pose each ~0.2 s). Show a text label (world-space UI `Text`, legacy font as used in `SensorDataHud`) with "Walk around the anchor" / "Ready to save".
2. `SpatialAnchorController.SaveAll()` should wait (or warn on HUD) until the guide reports enough coverage (configurable `minCoverage`, default 0.5), with a `forceSave` bool to bypass.
3. On restore, call `TryRemap` for each restored anchor once tracking is stable for 2 s, and report remap results on the HUD.
4. HUD lines via `DebugRegistry`: coverage %, last quality per anchor.

## Done when
- No `error CS` (coordinator compiles; you can't run Unity — match the XREAL 3.1 API exactly by reading the SDK source from the extracted tarball: `package/Runtime/Scripts/Android/AR Features/XREALAnchorManagerExtension*.cs`).
- PR describes the UX and the thresholds.
