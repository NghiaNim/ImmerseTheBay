# Implement Cartesia partner voice routing

This Portal package contributes to #36 in master plan #16. The base is the unified-flow PR #15 at `3e02168`. Work on `codex/demo-cartesia-routing`.

## Ownership

- `portal/agent/cartesia.js` and `portal/agent/cartesia.test.js`
- `portal/voice/routes.js` and `portal/voice/routes.test.js`
- This brief

Add server-side selection of two configured Japanese voice IDs for scripted synthesis and two matching agent IDs for managed calls. Existing runs without a pinned voice use the legacy configuration. Add mock tests for both routes and for compatibility. Scene preparation, schema, persisted run state, scripted-speech route binding, clients and deployment are owned by the other packages.

## Configuration

Set `CARTESIA_VOICE_ID_FEMALE` and `CARTESIA_VOICE_ID_MALE` to the two approved Japanese Sonic voices. Configure `CARTESIA_AGENT_ID_FEMALE` and `CARTESIA_AGENT_ID_MALE` with matching deployed managed agents that share the same tools and lesson behavior. Unpinned calls prefer `CARTESIA_VOICE_ID` or `CARTESIA_AGENT_ID` respectively, then the available female or male configuration in that order. A requested pinned style requires its matching configuration. The adapter returns 503 when that configuration is missing so a reconnect cannot silently switch the partner's voice.

The app currently mints a server-side session at `/v1/agents/websocket/{agent_id}/sessions`. The documented [Agents WebSocket voice override](https://docs.cartesia.ai/api-reference/agents/websocket) is part of the separate `/agents/stream/{agent_id}` protocol's client start event. Using two managed agent IDs keeps the current session protocol and selects the agent on the server.

## Requests for owners

- Scene and contract owners should pin the resolved logical value (`female` or `male`) alongside the run and replay metadata, and expose it at `snapshot().lessonContext.partnerVoice`. Managed sessions use that field. Keep the same value across branch/rejoin, retries and reconnects.
- The clerk playback owner should bind synthesis to the authorized current lesson/run and pass the stored value to `synthesize({ text, language, signal, partnerVoice })`. The existing generic WAV route accepts only text and language and retains default behavior. A bound clerk endpoint or an additive binding may distinguish it from ordinary learner teaching playback. Provider IDs and unresolved casting values are not valid speech arguments.
- The integration owner should provision both voice/agent pairs and verify the opening line and a reconnect against live Cartesia on S25. The tests below mock the upstream provider.

## Validation

Verify with Portal syntax checks, targeted mock tests, and the full Portal test suite. Live Cartesia credentials, provisioning and device audio tests belong to the integration run.
