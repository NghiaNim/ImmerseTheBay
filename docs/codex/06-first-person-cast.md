# Codex task 6 — Glasses first-person view to the web portal

Branch: `feat/first-person-cast`

## Context
The web portal (`portal/`, Node + `/sync` WebSocket hub, page in `portal/public/`) already mirrors the Visko video between web and Unity. We also want the portal to show what the wearer sees (rendered virtual content, optionally blended with the RGB camera where supported). XREAL ships a "FirstPersonViewStreamingCast" sample and capture classes (`XREALCaptureBehaviour`, `XREALVideoCapture`, `FrameCaptureContext`, `RGBCameraFrameProvider`, ...). Docs say RGB camera access is only for XREAL One + Eye; Air 2 Ultra may only support virtual-only capture — verify in source.

Reference (read-only): `mkdir -p /tmp/xrealsdk && tar -xzf Packages/com.xreal.xr.tar.gz -C /tmp/xrealsdk`, then read `package/Samples~/Camera Features/FirstPersonStreammingCast/**` and `package/Runtime/Scripts/**/Capture*`, `*Record*`.

## You own
- `Assets/Scripts/Cast/**` (new)
- `portal/public/cast.html`, `portal/public/cast.js` (new files only; don't edit existing portal files — ask in the PR for a link from index.html)
- `portal/cast/**` (new server-side helpers if needed; wire-up into `server.js` is a request for the owner)

## Do
1. Write `docs/CAST.md` (new file) summarising what the XREAL capture API can do on Air 2 Ultra (virtual-only vs blended, resolutions, what the sample streams to and how), with file/class references.
2. Implement the simplest robust path: `FirstPersonCaster` in Unity grabs frames of the virtual scene (prefer XREAL's capture pipeline if it supports Air 2 Ultra; else render the main camera's left eye into a RenderTexture at 640x360 @ 10 fps), JPEG-encodes on a worker (use `AsyncGPUReadback` + `ImageConversion.EncodeArrayToJPG`), and sends binary frames over a WebSocket to the portal at `ws://<portal>/cast` (portal URL from `StreamingAssets/visko.json` field `portalUrl`; parse with `JsonUtility` into your own class).
3. `portal/cast/castHub.js`: a `ws` server handler for path `/cast` that relays binary JPEG frames from the Unity client to all viewer clients (export a function `attachCast(server)`; the owner will call it from server.js). `cast.html/js`: shows the latest frame on a `<canvas>` with fps counter.
4. HUD line via `DebugRegistry`: casting on/off, fps, bytes/s. Toggle with keyboard `V` and public `SetCasting(bool)`.

## Done when
- `node --check` passes on new JS files.
- No `error CS` (coordinator compiles; you can't run Unity).
- `docs/CAST.md` clearly states what was verified from source vs assumed.
