# ImmerseTheBay infrastructure

Target: XREAL Air 2 Ultra (6DoF) + Beam Pro / supported Android phone, Unity 6000.x LTS, XREAL SDK 3.x.

## First-time setup
1. Open the project in Unity Hub (6000.0.85f2 LTS). `Assets/Editor/XrealProjectSetup.cs` runs once and applies:
   Android target, IL2CPP + ARM64, min API 29, OpenGLES3 only, Portrait, VSync off, Linear, Internet permission,
   Active Input Handling = Both, URP asset. Re-run via **Tools > XREAL > Apply Android Settings**.
2. Download `com.xreal.xr.tar.gz` from developer.xreal.com, put it in `Packages/`, then Package Manager > Add package from tarball.
3. Project Settings > XR Plug-in Management > Android: tick **XREAL**. Then Project Validation > **Fix All**.
4. XREAL settings: Initial Tracking Type = `MODE_6DOF`, Initial Input Source = `Hands` (or `Controller And Hands`),
   Support Devices includes `XREAL_DEVICE_CATEGORY_REALITY`.
5. **Tools > XREAL > Create Sensor Debug Scene**, then Build And Run to the Beam Pro.

## Modules
| Area | File | Notes |
|---|---|---|
| Sensor dump HUD | `Scripts/SensorDebug/SensorDataCollector.cs`, `SensorDataHud.cs` | Head pose, XR Hands joints (+ joint spheres), AR session, planes, spatial mesh, anchors, images, every XR InputDevice feature, all Android sensors (Input System), display/system. Head-following world-space panel. |
| Extra HUD lines | `Scripts/SensorDebug/DebugRegistry.cs` | `DebugRegistry.Register(() => "text")` from any script. |
| Spatial video / image | `Scripts/Media/SpatialMediaPlayer.cs`, `Shaders/StereoUnlit.shader` | Mono / side-by-side / top-bottom. Source = URL, absolute path, or file in `StreamingAssets/`. |
| Music waveform | `Scripts/Audio/AudioVisualizer.cs` | Waveform line + log spectrum bars from any AudioSource. |
| Microphone | `Scripts/Audio/MicrophoneCapture.cs` | Requests RECORD_AUDIO, records without playback, exposes samples / RMS / dB. Same visualiser in Microphone mode. |
| Network | `Scripts/Net/NetClient.cs` | Real-time WebSocket (auto-reconnect, heartbeat, text/binary/JSON, main-thread events, echo RTT) + HTTP GET/POST JSON. |
| Spatial anchors | `Scripts/Anchors/SpatialAnchorController.cs` | Right pinch / screen tap / Space = place, left pinch / S = save, hold left pinch 2 s / C = clear. Saved anchors restore automatically on next launch (AR Foundation 6 persistent anchors via XREALAnchorSubsystem). Parent content under `anchor.transform`; subscribe to `AnchorRestored`. Scan the room 5-15 s before saving, keep content within 3 m of its anchor. |
| Hand state | `Scripts/Tracking/HandInput.cs` | World-space pinch / index tip / palm for both hands. Read `HandInput.Instance.Left/Right` instead of polling XR Hands. |
| Place 2D video / image / 3D model | `Scripts/Content/ContentSpawner.cs`, `ContentLibrary.cs`, `PlaceableContent.cs` | Items come from `StreamingAssets/content.json`. Video/image = screen (mono or stereo), model = glTF/GLB via glTFast (URL or local), primitive. Each item gets its own ARAnchor. Save layout -> restored at the same real-world spots on next launch. |
| Grab / move / rotate / scale | `Scripts/Content/HandManipulator.cs` | Pinch near an object (or pinch while looking at a far one) to grab; second hand pinch = scale + yaw. Release re-anchors. |
| Menu | `Scripts/Content/PokeMenu.cs` | Index-finger poke panel: one button per content item + Save layout / Delete last / Clear all / Recenter / HUD on-off. Editor keys: 1-9, F5, Del, R. |
| Image tracking | `Scripts/Tracking/ImageTrackingController.cs` | XREAL has no runtime library: add images (with physical size) to `Assets/Settings/ReferenceImages.asset`; a detected image spawns the content named by the matching `markers[].contentId` in content.json. |
| Recenter | `Scripts/Tracking/RecenterController.cs` | `XRInputSubsystem.TryRecenter()` (same call XREALUtility wraps). |
| XREAL-only features | `Scripts/Xreal/XrealFeatures.cs` | Device info, glasses events (wearing, temperature, brightness, volume, buttons, disconnect, errors), runtime switch Hands/Controller and 6DoF/3DoF/0DoF, glasses multi-key double-click = recenter, mesh classification label counts. Menu buttons + editor keys I / T. Anchor remap: M key in `SpatialAnchorController`. |
| Visko (Reactor) live video | `Scripts/Visko/ViskoSession.cs`, `Plugins/Android/ViskoReactorBridge.kt`, `portal/` | Real-time generated video (dynamic + stable models) in the glasses, synced with the web test portal. API key in `portal/.env`. See [VISKO.md](VISKO.md). |
| HUD pages + logging | `Scripts/SensorDebug/` | Six pages (Overview/Hands/AR/Devices/Sensors/Modules), PgUp/PgDn or quick pinch; `L` toggles CSV logging to `persistentDataPath/sensorlogs/`. See `Scripts/SensorDebug/README.md`. |
| Immersive media | `Scripts/Media/`, `Shaders/StereoUnlit.shader` | `projection`: Flat / Dome180 / Sphere360 (content.json field), stereo per eye, Pause/Resume/Seek/Volume API. Mic emits 16-bit PCM chunks (`MicrophoneCapture.OnPcmChunk`). |
| Realtime protocol | `Scripts/Net/Protocol.cs`, `RealtimeSession.cs`, `server/` | Rooms, state replay, events, ping/pong RTT, binary audio/JPEG frames. `cd server && npm start` (port 8787). Not yet placed in the debug scene. |
| Content interaction (XRI) | `Scripts/Content/` | Placed content is `XRGrabInteractable` (hands + controller, two-hand scale); video screens get a poke control strip. Legacy pinch manipulator off by default. |
| Anchor quality | `Scripts/Anchors/AnchorQualityGuide.cs` | 16-segment coverage ring from XREAL `GetAnchorQuality`; Save waits for 50% coverage (`SaveAll(true)` forces); remap after restore. |
| First-person cast | `Scripts/Cast/`, `portal/cast/`, `portal/public/cast.html` | Left-eye render 640x360 @10 fps as JPEG over `/cast` to the portal viewer. Menu "Cast view on/off" or `V`. See [CAST.md](CAST.md). |
| Build | `Editor/BuildScript.cs` | Tools > XREAL > Build APK, or batch `-executeMethod BuildScript.BuildAndroid` -> `Builds/ImmerseTheBay.apk`. |

### content.json
```json
{
  "items": [
    { "id": "my-video", "label": "My Video", "type": "video", "source": "videos/clip.mp4", "layout": "Mono", "size": 1.2 },
    { "id": "my-sbs",   "label": "3D Clip",  "type": "video", "source": "https://example.com/sbs.mp4", "layout": "SideBySide", "size": 1.2 },
    { "id": "chair",    "label": "Chair",    "type": "model", "source": "models/chair.glb", "size": 0.8 },
    { "id": "ball",     "label": "Ball",     "type": "primitive", "source": "Sphere", "size": 0.2 }
  ],
  "markers": [
    { "name": "poster", "image": "markers/poster.jpg", "widthMeters": 0.3, "contentId": "my-video" }
  ]
}
```
`source` = URL, absolute path, or path relative to `Assets/StreamingAssets/`. `type`: video | image | model | primitive. `layout`: Mono | SideBySide | TopBottom. `size` = screen width or the model's largest dimension in metres.

## XREAL SDK 3.x capabilities (from docs.xreal.com)
| Capability | Air 2 Ultra | How |
|---|---|---|
| 6DoF head tracking (also 3DoF / 0DoF / 0DoF-stab) | Yes | XREAL settings `Initial Tracking Type`; recenter: `XREALUtility.GetInputSubsystem()?.TryRecenter()` |
| Hand tracking (26 joints, poke / near-far / pinch via XRI) | Yes | XR Hands + XRI, `Input Source = Hands`, "Hands Interaction Demo" sample |
| Phone / Beam Pro as 3DoF controller, custom virtual buttons, vibration | Yes | `XREALVirtualController`, Input System bindings |
| Gaze interaction | Yes | Docs "Gaze" page |
| Plane detection (horizontal / vertical) | Yes | `ARPlaneManager` |
| Spatial mesh (depth meshing) | Yes | `ARMeshManager` |
| Mesh classification (wall, floor, ceiling, table, door ...) | Yes | "AR Meshing Manager" + "Mesh Classification Tracking", `NRMeshingVertexSemanticLabel` |
| Image tracking (up to 5 images, 1 tracked at a time) | Yes | `ARTrackedImageManager` + Reference Image Library |
| XREAL markers | Yes | Docs "XREAL Markers" page |
| Spatial anchors: create, save, load, remap, erase (persist across sessions) | Yes | `ARAnchorManager`, `XREALAnchorSubsystem`, "Anchors" sample |
| RGB camera frames (YUV) and MR recording | Docs list only XREAL One + Eye accessory | "Camera Features" sample |
| Notification popups (system warnings: battery, temperature, tracking lost) | Yes | Docs "Notification Popup" page |
| MRTK3 integration | Yes | Docs MRTK3 page |
| Multi-view / multi-pass stereo rendering | Yes | XREAL settings `Stereo Rendering Mode` |
