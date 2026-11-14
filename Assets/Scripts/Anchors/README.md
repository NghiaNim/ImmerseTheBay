# Anchor mapping quality

`SpatialAnchorController` automatically adds an `AnchorQualityGuide` at runtime
when one is not already attached. An explicitly attached guide allows Inspector
configuration; existing generated scenes need no changes.

Each `AnchorPlaced` event starts an independent amber ring and a world-space
legacy UI Text label. GOOD viewpoints turn their corresponding segments green.
The label changes from **Walk around the anchor** to **Ready to save** when the
configured coverage is reached. Successful saves hide the ring. Clear, deleted
anchors, and component destruction clean up the visuals; disabling the guide
hides them and unregisters its HUD provider.

## Thresholds

| Setting | Default | Behavior |
| --- | --- | --- |
| `segmentCount` | 16 | 12–16 equally spaced horizontal directions around the anchor; 22.5° sectors with the default count. |
| `minCoverage` | 0.5 | At least 8 of 16 distinct directions must have returned GOOD. Repeated samples of one direction count once. |
| `pollInterval` | 0.2 s | Quality sampling cadence, clamped to at least 0.05 s. |
| `ringRadius` | 0.22 m | Radius of the level ring centered on the anchor. |
| `viewAngle` | 45° | Head forward must point within this angle of the anchor before a GOOD sample can fill a segment. |
| Restore remap stability | 2 s | Both the AR session and the individual restored anchor must continuously report tracking. |

Quality is queried with the world-space camera pose, matching the XREAL 3.1
sample and `XREALAnchorManagerExtension`. Only
`XREAL_ANCHOR_ESTIMATE_QUALITY_GOOD` fills coverage; SUFFICIENT and INSUFFICIENT
remain unfilled. A viewpoint less than 5 cm from the anchor's vertical axis is
ignored because its horizontal direction is ambiguous. GOOD coverage is retained
for the lifetime of that anchor, including temporary tracking loss or guide
disable/enable. It is not serialized across launches.

`SaveAll()` preflights every unsaved anchor before persisting anything. Insufficient
coverage leaves the anchors unsaved and reports the first blocked anchor and its
coverage on the HUD. Scan further, then invoke Save again. `SaveAll(forceSave: true)`
explicitly bypasses the coverage check, including a disabled guide. Save failures
remain unsaved with their rings visible and their error status on the HUD.

Restored anchors receive one automatic `TryRemap` attempt each after stable
tracking. Session/anchor tracking loss, component disable, or application pause
resets the timer. Results stay visible per anchor in the spatial-anchor HUD;
failures do not loop automatically. The existing **M** shortcut / `RemapAll()`
allows an explicit retry. Restored anchors are sampled for their last quality on
the HUD but start with zero session coverage and do not show an unsaved ring.
Neither saving nor remapping is gated on the SDK's unset descriptor support flags.

## Integration and verification

This workflow covers anchors owned by `SpatialAnchorController`. `ContentSpawner`
currently creates and persists its own anchors, so its Save layout action does
not use this guide or the automatic restore remap workflow.

Coordinator/device checks:

1. Place two anchors with Space; confirm each has a ring and independent HUD
   coverage/last-quality values. Looking away, non-GOOD quality, and staying in
   the same sector must not advance coverage.
2. Save below 50%: confirm the HUD warns and nothing is persisted. Reach eight
   GOOD sectors on both anchors, save again, and confirm the rings hide only
   after successful saves. Exercise `SaveAll(true)` as the bypass.
3. Relaunch and restore. Confirm remap waits for two continuous seconds of
   session and anchor tracking; tracking loss resets the wait. Each anchor gets
   one automatic attempt. Check the per-anchor success/failure lines and M retry.
4. Clear or remove anchors, disable/enable the guide, and pause/resume the app;
   confirm visuals, HUD subscriptions, and remap timers behave as described.

All runtime C# sources passed a standalone Roslyn compile against the locally
imported Unity/package assemblies, including an Android-defined pass
(`UNITY_ANDROID`, `UNITY_6000_0_OR_NEWER`). Only existing Visko field warnings
were reported. This does not verify scene import, build stripping, or device UX.
Unity was not launched. The coordinator must run the Android Unity compile
check and import/commit `AnchorQualityGuide.cs.meta` (and this document's `.meta`)
before merging.

Requests for owners: the Content owner can route its separate placement/save/
restore workflow through the controller or expose equivalent events and quality
checks if Save layout should use the same UX. No content or scene files are
modified here.
