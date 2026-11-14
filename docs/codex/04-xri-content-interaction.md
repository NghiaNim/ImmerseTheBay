# Codex task 4 — XRI-based grab/scale for placed content

Branch: `feat/xri-content-interaction`

## Context
We are migrating the rig to XREAL's official samples + Unity XR Interaction Toolkit 3.0.8 ("XR Interaction Hands Setup" prefab: poke, near-far, ray interactors for hands and the phone controller). The coordinator is doing the rig/scene part. Today placed content (`PlaceableContent`, spawned by `ContentSpawner`) is moved by our own pinch logic in `Assets/Scripts/Content/HandManipulator.cs`.

## You own
- `Assets/Scripts/Content/**` except `PokeMenu.cs` (menu stays as is for now)

## Do
1. In `ContentSpawner.Spawn`, after the visual is built, make the root XRI-grabbable: add a `BoxCollider` fitted to `PlaceableContent.GetBounds()` (in local space), a kinematic `Rigidbody` (no gravity), and `UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable` configured for: movement type Instantaneous, track position + rotation, throw on detach off, `selectMode = Multiple` and two-hand scaling via `XRGeneralGrabTransformer` (allow two-handed scaling, clamp scale 0.05–20). Skip all of this when `PlaceableContent.locked` (image-tracked content).
2. On `selectExited` when no interactor still selects it, call `ContentSpawner.Reanchor(content)` (re-pins it to a new ARAnchor). On `selectEntered` detach from the anchor parent first (world position kept), like `HandManipulator` does.
3. Hover feedback: use `hoverEntered/Exited` to call `PlaceableContent.SetHighlight`.
4. Keep `HandManipulator` but add `public bool useLegacyPinch = false;` — when false it does nothing (so XRI and legacy never fight). Leave a short comment on how to re-enable.
5. Video screens: also add a simple world-space control strip under each video screen (play/pause, -10s, +10s, mute) as XRI-pokeable UI (`Canvas` + `TrackedDeviceGraphicRaycaster`, Unity UI `Button`s). Use `SpatialMediaPlayer.Video` (`VideoPlayer`) directly (another agent is adding a richer API to SpatialMediaPlayer on a different branch — don't depend on it).
6. Namespaces for XRI 3.x: `UnityEngine.XR.Interaction.Toolkit`, `.Interactables`, `.Transformers`, `.UI`.

## Done when
- No `error CS` (coordinator compiles; you can't run Unity — match XRI 3.0.x APIs exactly, check the package source under `Library/PackageCache/com.unity.xr.interaction.toolkit@*/Runtime` in the main checkout `/Volumes/Mac_Working/GitHub/ImmerseTheBay` if present, read-only).
- PR description explains the XRI configuration and lists anything the scene/rig owner must add (e.g. interaction layers, an `XRInteractionManager` in the scene).
