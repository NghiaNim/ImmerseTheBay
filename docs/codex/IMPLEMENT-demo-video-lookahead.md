# Video lookahead for the three-scene demo

Issue #27. Branch `codex/demo-video-lookahead`, inspected baseline `600d827`. This brief owns the evidence, architecture decision and validation protocol for the native video path. Runtime changes require coordination with the Unity media and Portal owners.

## Current behavior

The curated lesson presents the sandwich shelf, refrigerated drinks and checkout. Portal predicts one next main scene with `nextSceneId`. The Unity client reads at most one predicted bundled image ahead of use. After a committed turn changes scenes, it displays the new still, waits for `reset` and `set_image`, sets the prompt and starts generation. It waits for the new live display before presenting the target and reopening the speech loop. An image cache hit saves a local asset read. Upload, provider generation and first-frame display still happen on the transition.

## Provider capability and cost

- The installed build pins `inc.reactor:reactor-sdk-android:1.1.0`. Public signatures in the local AAR expose connection, command, upload, stream, stepped generation and recording APIs. A connection that joins another client's session sees the same model instance and media. [Android SDK](https://docs.reactor.inc/sdk-reference/android/reactor.md) · [Session adoption](https://docs.reactor.inc/concepts/sessions.md#multiple-connections-per-session)
- Dynamic's streaming schema exposes one running generation. A new image takes effect on the next start after reset. A live prompt change steers the current stream. Android's generic `step()` uses a separately configured stepped connection and requires model support. The published Dynamic schema specifies streaming and gives no independent preparation or transferable generation handle on that connection. [Dynamic lifecycle and commands](https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/schema.md) · [Android stepped mode](https://docs.reactor.inc/sdk-reference/android/reactor.md#stepped-generation)
- A second independent streaming session requests another GPU allocation. Waiting takes a session slot, and billing starts at ready even before generation. The published defaults are five concurrent sessions account-wide, ten creations per minute and a three-session burst. Those defaults establish neither this account's entitlement nor available Dynamic capacity. The public read-only pricing response identified USD 0.0209 per ready second for Dynamic at inspection. Ten seconds of overlap would add about USD 0.21 to each transition at that rate. [Rate limits](https://docs.reactor.inc/resources/rate-limits.md) · [Billing](https://docs.reactor.inc/resources/billing.md) · [Public pricing response](https://api.reactor.inc/pricing)

The active build should retain one live session for the wearer run. Parallel live generation needs evidence for capacity, clean teardown, identity-safe promotion and cost. No session allocation or generation was performed in this investigation.

## Two concrete paths to shorter waits

### 1. Preproduce three reviewed Dynamic opening clips

Generate one short opening clip per authored scene during a separate staging run, with at most one GPU at a time. Use the exact production reference crops and the scene and identity prompts. Where recording is enabled on the deployment, collect a short recent window with `requestClip`, download while the session is open, and terminate the creator's session. Check permission to distribute the resulting clip before packaging. Inspect each candidate for initial placeholders, first-person orientation, recognizable shelves or clerk, and the absence of spoken audio. Store the approved silent outputs with scene ID, reference and prompt digests, media geometry and version. Keep provider URLs transient. [Reactor clip capture and export](https://docs.reactor.inc/concepts/recordings.md)

Decode one likely clip locally while the current scene stays interactive. Display it only after Portal commits the exact destination for the same active run. Keep the still available for decode or asset mismatch, and start live Dynamic with the same committed destination. Crossfade to validated live media within the existing feathered surface. On a detour, cancel the unused predecode or use the exact matching approved clip. On return, reacquire the saved destination instead of advancing. Disable motion overlays for arbitrary photo scenes until they have matching approved media. Explicitly identify an on-screen prerecorded opening as such during the transition. A clip helps the visible transition without adding a GPU allocation per learner.

A smaller first experiment moves only upload time off the critical path. The SDK separates uploading a file from applying it with a model command. Upload the allowlisted predicted seed without applying it. Match any resulting reference to the live connection epoch and image hash, and verify on the real deployment that its reference survives reset before using it. The file-upload doc describes the upload URL expiry. It gives no evidence for carrying a file reference across model resets or separate sessions. Keep one upload in flight, cancel on Leave or prediction change, and use the ordinary upload if the provider definitively rejects the cached reference. [File uploads](https://docs.reactor.inc/concepts/file-uploads.md)

### 2. Run a gated live standby experiment

Give a separate, explicitly enabled development run one speculative provider session with a short server-side duration cap, one pending open, and no speculative retry after capacity or billing failure. Stop stale, abandoned or failed GPU sessions through creator termination. Retain active playback until the candidate's qualified generation is decoded, and fall back to the existing reset/reseed path as soon as the standby deadline expires. The speculative branch must have no Cartesia, record writes, mic activation, or early learner cursor changes.

First separate the logical lesson/run binding from the transport session ID. The current client uses its provider session ID for lesson turns, synchronization, snapshot and voice binding. Merely exchanging the live backend would invalidate the active lesson and lose subscriptions. A promotion needs a stable lesson lease, explicit transport epoch, and synchronized adoption for mirror clients. Consume the candidate once on the committed destination with a key containing run, prediction revision, target/scene, reference and prompt digests, and transport epoch. A detour, config/identity change, reconnect, Leave or generation boundary invalidates the candidate. Set explicit concurrency, GPU-second and memory budgets before enabling this path. A second connection to the original session does not provide isolation.

## Measurements and failure checks

- Measure at least ten transitions per path using the same three-scene lesson. Log monotonic times for committed destination, visible still, preview frame, image accepted, generation started and first accepted live frame. Separate perceived wait from live wait. Report p50/p95, cache hits, visible stale frames, peak device memory and provider ready seconds. Measure the two distinct stops in a matched run and keep wearer observations beside provider/device telemetry.
- Exercise retry, detour and return, repeated destination, predecode or upload timeout, `429`, reconnect and Leave. Check that the committed score and existing target stay responsive, only the correct scene is shown, and no speculative session remains. Capture all three destination views for review.
- The existing native first-frame floor is a local increasing decoded-frame counter. It disambiguates frames relative to a baseline but cannot establish the originating generation if a delayed WebRTC frame crosses reset. The SDK exposes timestamp and optional frame user data, while Dynamic exposes reset/start and chunk events. The live test should record which generation evidence actually arrives. If identity cannot be established from the deployment, use a separate transport for the candidate and verify pixels against reviewable captures. Do not claim seed identity from a command acknowledgement, frame count or an arbitrary elapsed delay alone. [Dynamic events](https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/schema.md#events)

## Order Coffee rollout and gesture exploration

Order Coffee is now the default pre-made lesson. Its three scene IDs are `cafe-order`, `cafe-pour` and `cafe-serve`. Ship these first, then publish equivalent openings for the other authored lessons. Photo-created lessons keep their original upload and a three-cue plan. Generated exploration assets belong to the active run.

### Opening clips

Review a real Dynamic clip against its authored scene before publishing. On a machine with FFmpeg, use:

```sh
node portal/agent/publish-opening-clip.js ja-cafe-demo-001 cafe-order /path/to/reviewed-dynamic.mp4 1.25
```

The optional offset skips unstable lead-in frames. Publishing atomically replaces a silent, 16:9, fast-start MP4 in the stable Portal `data/scene-openings/` directory. Use 2–6 seconds of continuous action. The storage key binds the lesson, authored scene, model and exact seed bytes. Bump the version when changing the global visual prompt/clip contract. The authenticated lesson scene opening route supports HTTP ranges and returns 404 on a cache miss. Keep draft exports outside the publication directory.

### Native playback and forecasting

1. Fetch the first opening on the phone while the learner chooses a lesson or sees the glasses setup. Transfer the prepared file reference to the glasses scene only for the exact selected lesson and scene. Use the same feathered surface for still, clip and Dynamic. Show the still immediately if lookup or decode misses. Route guide speech through Cartesia and preserve its microphone/feedback timing.
2. During the opening, bring up Dynamic in parallel on the committed scene. Crossfade only once the new stream can display a qualified frame. If the cached motion finishes earlier, hold its last frame until Dynamic or the existing Retry path is ready.
3. While the learner is speaking, read ahead at most one predicted mainline clip using the existing `nextSceneId`. Revision and destination checks fence what can be shown. When intent changes, drop the candidate and keep the current scene active until the new branch is ready. Keep the same return checkpoint and avoid opening a second paid Dynamic stream until the transport and cost experiment above has passed.
4. Measure time to visible motion separately from time to an interactive live frame. For the demo, use the same S25/Ultra run for cold start, warm start, branch, resume, network failure, and left/right display checks.

### Gesture-triggered branch

1. Add an explicit Look / Explore interaction. Show the pointer target when the hand ray intersects the video and use a deliberate pinch to submit it. Keep action controls and Explore mutually exclusive for each pinch. An open hand or a passing ray only highlights.
2. Capture the exact decoded video image at submit time. Convert the ray hit to the decoded frame's normalized coordinates using the same aspect crop and orientation as the material. Use the existing Portal point-vision path for grounded object evidence. Bind the request to run, revision, scene and frame; drop stale responses and leave the lesson in place when there is no reliable object hit.
3. Pass the recognized object, optional spoken intent, action and current lesson goal to the branch planner. Generate one next-scene description and an image edit from the captured frame, preserving viewpoint, lighting and visible characters. Keep raw frames and generated assets temporary per run. Run the same environment-only audio constraints and validate identity before handing the new seed to Dynamic.
4. Use the existing DAG to commit the branch and later restore the main checkpoint. Keep the current video active if planning, image or Dynamic fails. Rate-limit to one request in flight per run, and avoid recording lesson attempts as points. Evaluate with the milk pitcher as a positive case, the blank counter as a no-hit control, and returning from exploration to the pending coffee sentence.
