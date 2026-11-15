# Fixed first-person video and recoverable lesson decisions

## Plan

1. Remove visual actions that contradict the fixed first-person camera: visible customer bodies, camera approach, clerk traversal, close-ups and off-screen fetching.
2. Follow the Dynamic prompt guide: establish the subject/scene once; subsequent prompts describe one small physical change. Use positive static framing rather than repeatedly listing absent people or redescribing faces.
3. Keep the same clerk behind the counter, with locked eye-level position, orientation and framing. Preserve the approved reference and environmental-only audio/Cartesia split.
4. Run real Dynamic + Cartesia opening, detour and rejoin; capture native frames as well as UI screenshots and review face, foreground people, camera scale and background alignment separately from transport success.
5. Continue the next reliability slice: optional request IDs/run/revision, bounded replay cache, reset invalidation and read-only state snapshot. The Web client retries only a lost API response with the identical operation and never automatically replays an uncertain provider command.
6. Fault-test duplicate detour requests, concurrent duplicates, ID conflicts, stale revisions/reset, failed persistence/logging and browser retry behavior. Run full tests and integrate through a PR into main.

## Contracts and boundaries

Request `operation`: `{requestId, expectedRevision, runId?}`. Bootstrap uses revision zero without a run ID. Responses include `{requestId,runId,revision,replayed}`. Replay decisions precede cursor validation and model calls. Read-only snapshots contain run/revision/beat/last response; they do not authorize re-executing commands.

This slice provides in-process lesson-decision idempotency, not exactly-once Reactor execution or restart durability. Cache eviction, reset and revision conflict behavior must be explicit. Provider command failures remain uncertain and require recovery rather than an automatic retry. Existing Unity callers remain compatible through the legacy request format.

## Prompt evidence

The [official Dynamic prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/prompt-guide.md) warns that negation may render the named object, repeated subject/world descriptions may rebuild a scene, and multiple steering changes need several seconds to land. The existing `visitor places ...` and `camera moves closer ...` cues contradicted a stationary first-person view. The implementation removes these triggers instead of adding more negative words.

## Verification

Implemented. Full portal suite passed (143 tests before the final voice reconciliation patch). Real Dynamic + Cartesia playback + dropped-response replay passed in 58.6 seconds: five decisions, revisions 1–5, routes main → detour → detour → detour → rejoin, five advancing native 640×368 frames, clean Leave. Artifacts: `/tmp/il-fixed-view-live-retry/`.

The first attempt failed after four capacity-limited retries and disconnected cleanly; a later independent attempt succeeded. This is provider availability, not a guaranteed capacity fix.

Reviewed opening, third detour and rejoin native frames: the clerk remains behind the counter with the same broad clothing/appearance and camera composition; no extra foreground customer appeared. Shelf/product geometry and facial detail still drift. This is an improvement in this sample, not a solved identity guarantee. Keep prior failures visible in issue #1.
