# First-person cast

`ImmerseTheBay.Cast.FirstPersonCaster` sends **virtual-only, mono left-eye JPEGs** to the portal: 640×360, at most 10 fps, JPEG quality 70 by default. It includes scene renderers and world-space UI visible to the source camera. It does not include the physical room, microphone, application audio, Android UI, or the final XREAL display compositor. Casting starts off; use `SetCasting(true)` on the main thread or keyboard **V**. The HUD shows requested on/off state, connection/capture status, successfully sent fps, and JPEG bytes/s (excluding WebSocket overhead).

## What the SDK source establishes

Inspected the installed **XREAL SDK 3.1.0** package at `Library/PackageCache/com.xreal.xr@f282e2634a67/`. Paths below are relative to that package. No proprietary SDK source is copied into this repository.

| Source | Finding verified from managed source |
| --- | --- |
| `Runtime/Scripts/Android/Camera Features/FrameCaptureContext.cs`, `AutoAdaptBlendMode` | RGB `Blend`/`CameraOnly` requests fall back to `BlendMode.VirtualOnly` when `XREALPlugin.IsHMDFeatureSupported(XREAL_FEATURE_RGB_CAMERA)` is false. A busy RGB camera also makes `Blend` fall back. |
| Same file, `CreateFrameProviderByMode` | Virtual-only capture uses `NullDataFrameProvider(fps)` rather than reading RGB frames. `FrameProvider/NullDataFrameProvider.cs` advances frames at end of frame using timestamps; no camera permission/feed is needed for that provider. |
| `XREALVideoCapture.cs`, `StartVideoModeAsync` | Chooses an `RGBCameraFrameProvider` only with the RGB feature; otherwise supplies a null provider. This confirms an intended virtual-only path for devices without RGB, including the expected Air 2 Ultra case. |
| `XREALCameraInitializer.cs`, `Initialize` | An RGB-calibrated capture camera falls back to the **left display** pose/projection when RGB is unsupported. |
| `FrameBlender/FrameBlender.cs`, `CameraRenderToTarget` | Virtual-only skips background blending. Rendering uses `Camera.Render()` into a texture; `FrameCaptureContext.PreviewTexture` exposes the blend texture. |
| `XREALVideoCapture.cs`, `XREALVideoCaptureUtility.SupportedResolutions` | Without RGB, base resolution is 1280×720; the other advertised sizes are 640×360 and 1920×1080. With RGB, the base comes from `GetDeviceResolution(RGB_CAMERA)`, with half-size and 1.5× variants. |
| Same utility, `GetSupportedFrameRatesForResolution`; `Runtime/Scripts/Android/Notification/NativeConstants.cs` | Advertises `RECORD_FPS_DEFAULT`, which is **30 fps**. The portal caster deliberately uses a lower rate. |

The managed code queries native feature support; it does **not** contain a definitive model-to-capability table. The project documentation says RGB capture is available on XREAL One + Eye, not Air 2 Ultra. Treat Air 2 Ultra as virtual-only; actual feature-query results, blended capture on One + Eye, and all on-device performance remain **unverified on hardware**. Tracking/grayscale cameras are not an RGB passthrough feed. This implementation does not attempt blended capture even on RGB-capable devices.

### What the first-person sample streams

`Samples~/Camera Features/FirstPersonStreammingCast/Scripts/FirstPersonStreammingCast.cs` (the directory/class spelling is from the SDK) creates an `XREALVideoCapture`, configures BGRA32, selected resolution, default 30 fps, blend mode and audio, then calls `StartVideoModeAsync` and `StartRecordingAsync`.

Its LAN discovery broadcasts `FIND-SERVER` via UDP port **6001** (`Network/LocalServerSearcher.cs`). `Network/NetWorkBehaviour.cs` uses the sample's custom network client to join the discovered server and negotiate `useAudio`. Video goes to **`rtp://<discovered-IP>:5555`**; alternatively the sample records a local MP4. `Encoder/EncodeTypes.cs` selects local, RTMP, or RTP encoder modes based on the output path. This is not a browser WebSocket JPEG endpoint and cannot feed the canvas directly.

## Why this implementation uses a separate render camera

The SDK has the intended Air 2 Ultra virtual-only path above, but its supplied blender calls `Camera.Render()`. This project configures URP in `Assets/Editor/XrealProjectSetup.cs`. Rather than assuming the SDK's built-in rendering path works under URP, this caster uses URP's explicit render-request API. This is a pipeline compatibility decision, **not a claim that Air 2 Ultra cannot capture virtual content**.

Verified in the installed URP package `com.unity.render-pipelines.universal@947f725e83ac/Runtime/UniversalRenderPipeline.cs`: `SingleCameraRequest`, `IsRenderRequestSupported`, and `ProcessRenderRequests` render one camera to a supplied texture. `UniversalAdditionalCameraData.allowXRRendering = false` keeps the cast camera mono. The disabled cast camera copies the source's view/projection (left eye when XR stereo is enabled), never redirects the actual glasses camera, and falls back to `Camera.Render()` only when no scriptable pipeline is active. The image is resampled to 16:9; camera stacks, postprocessing, and screen-space overlay canvases are not reproduced.

`AsyncGPUReadback.Request(..., TextureFormat.RGBA32, ...)` copies GPU-owned data to a managed array before it expires. Only that array crosses to a worker; JPEG encoding and WebSocket sends run there. Native/worker status changes go through a `ConcurrentQueue<Action>` drained in `Update`. There is at most one readback/encode/send in flight; busy frames are skipped. A send times out after five seconds and forces reconnect, with backoff up to 15 seconds. Off/disable/pause cancels the connection. GPU resources survive pending readback and are released on the main thread. If async readback is unsupported or fails, the HUD explains why no frames are being sent; there is no blocking `ReadPixels` fallback.

The Unity 6000.0 installed XML reference confirms the signatures used. Unity documents [`EncodeArrayToJPG`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.EncodeArrayToJPG.html) as thread safe and [`AsyncGPUReadback.Request`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.AsyncGPUReadback.Request.html) as supporting RGBA32 format conversion. Image orientation is still a device check: the caster defaults to reversing GPU rows (`flipVertically`); the viewer also has an orientation checkbox for diagnosis.

## Configuration and wire contract

The caster reads only `portalUrl` and optional `portalToken` from `StreamingAssets/visko.json` using its own `JsonUtility` class. Android packaged StreamingAssets are loaded with `UnityWebRequest`, matching the project's existing approach. Missing/malformed config is shown on the HUD and prevents connection; it does not silently connect to a default host. No Reactor key is needed or shipped.

```json
{ "portalUrl": "http://192.168.1.20:8790", "portalToken": "" }
```

- Publisher: `ws://192.168.1.20:8790/cast?role=publisher` (HTTPS maps to WSS).
- Viewer: `/cast?role=viewer`, same origin as `/cast.html`. Enter the portal token in the password field when needed; it is held in memory only.
- If configured, both append `portal_token=<URL-encoded token>`, checked against `PORTAL_TOKEN` before upgrade.
- One complete binary JPEG per WebSocket message, no envelope or base64. The relay limits payloads to 2 MiB and checks JPEG start/end markers; browsers validate image decoding.
- One publisher at a time; additional publishers close with 4409. Viewers cannot upload (4403). Publisher changes are announced to viewers as `{ "type": "cast_status", "casting": true|false }`.
- The hub skips congested viewers, pings peers every 15 seconds, and terminates unresponsive peers on the next heartbeat. It retains no images. The canvas decodes one frame at a time and replaces its pending frame with the newest arrival; it shows fps, KiB/s, disconnects, and stale frames. Both ends reconnect. Disconnecting the page clears its image and stops retries.

Use the existing LAN development configuration or HTTPS/WSS deployment. Portal authentication is shared across cast roles; this is a single-wearer development relay, not a multi-user session router.

## Requests for owners (integration required)

These changes are outside this task's owned paths and were deliberately left to their owners:

1. **Scene/integration owner:** add one `FirstPersonCaster` component to a scene object via `SensorDebugSceneBuilder`. Assign the XR main camera or leave `sourceCamera` empty to resolve `Camera.main`. Add a device/menu toggle calling `SetCasting(bool)`; V alone is an editor/keyboard fallback. Keep `castOnStart` off unless explicitly wanted.
2. **Portal/server owner:** import and call `attachCast(server)` from `./cast/castHub.js` before listening. **Also migrate the existing `/sync` WebSocketServer to `noServer` with explicit path routing.** Its current `{ server, path: '/sync' }` listener rejects `/cast` upgrades, so simply attaching a second path server is insufficient. Preserve existing `/sync` auth and connection handlers. Minimal shape:

   ```js
   import { attachCast } from './cast/castHub.js';
   const wss = new WebSocketServer({ noServer: true, maxPayload: 1 << 20 });
   server.on('upgrade', (req, socket, head) => {
     const path = new URL(req.url, 'http://localhost').pathname;
     if (path === '/sync') {
       wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, req));
     } else if (path !== '/cast') socket.destroy();
   });
   const detachCast = attachCast(server); // returns idempotent shutdown/dispose function
   ```

   Merge this routing with any other workstream's WebSocket endpoints rather than rejecting their paths. The relay removes its listener/timer and terminates peers on server close; call `detachCast()` before shutdown if open WebSockets prevent the HTTP server from closing.
3. **Portal/page owner:** add a link from `public/index.html` to `/cast.html`. No existing portal files or package dependencies were changed. Optionally add the checks below to the existing npm check script.
4. **Coordinator:** import scripts in Unity and commit generated `.meta` files (including the Cast folder). Unity was not launched per task instructions, so new script/folder metadata has not been generated. Run the Unity Android compile check and the hardware checks below.

## Verification

```sh
node --check portal/cast/castHub.js
node --check portal/cast/castHub.test.js
node --check portal/public/cast.js
node --test portal/cast/castHub.test.js
```

The tests use actual HTTP upgrade parsing and `ws` framing over paired in-memory streams, avoiding a TCP listener. They cover multiple viewers, publisher lifecycle/replacement, authentication for both roles, coexistence with `/sync`, role restrictions, invalid JPEGs, and oversized messages. No Unity compile, graphics output, browser rendering, or device/LAN performance test is claimed here.

After owner integration: configure the Beam Pro's reachable portal URL, open `/cast.html`, connect, and enable casting. Verify an asymmetric scene (text/arrows) is upright and matches the left eye, world-space HUD/content appear, fps stays at or below 10, and bytes/s advances. Toggle off/on, pause/resume the app, disable/re-enable the component, restart the portal, disconnect/reconnect a viewer, and throttle one viewer while another stays live. Confirm the normal Visko `/sync` connection still works. The coordinator owns Unity compilation; no Unity process was started for this task.
