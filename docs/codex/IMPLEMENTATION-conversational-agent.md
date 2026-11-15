# Interruptible convenience-store voice demo

## Objective and ownership

Make spoken conversation the primary interaction. The learner can interrupt the clerk, change a shopping goal, and return to checkout while the generated environment continues. Cartesia Line owns audio orchestration; the existing portal owns lesson state and Dynamic commands. This brief authorizes portal/voice/, portal/voice-worker/, the browser voice controller and integration, environment examples, and integration tests. No Unity changes.

## Implementation

1. Create a server-authorized, single-use Cartesia agent WebSocket session. Keep permanent credentials server-side and bind the session to the current lesson run.
2. Stream microphone audio and play received speech incrementally. On an interruption, stop queued playback as well as current playback. Leave closes the microphone, socket and audio resources.
3. Use one conversational clerk with bounded tools for a new shopping goal, rehearsal and return to the saved main checkpoint. Keep identity, language and scene constraints in the base instructions; pass current lesson state separately.
4. Preserve committed lesson decisions even if the spoken response is interrupted. Claim each video operation once. Reject stale runs, duplicates and revision gaps. Never replay uncertain provider commands automatically.
5. Disable competing manual progression, legacy TTS and idle invitations while voice controls the lesson. Reconcile a read-only snapshot before returning to manual mode.
6. Keep dialogue audio in Cartesia and ambience in Dynamic. Spoken acknowledgement need not wait for video generation.

## Verification gates

- Unit/integration: session authorization, missing configuration, scoped session metadata, interruption playback cancellation, duplicate/gapped decisions, disconnect cleanup and tool idempotency.
- Browser: enter/leave voice mode, failed connection recovery, microphone release, manual-mode reconciliation.
- Real provider: Japanese rehearsal, Chinese spontaneous intent, interrupt mid-reply twice, switch sandwich to bottle, return to checkout. Verify audio stops and superseded responses do not resume.
- Video: inspect actual frames independently of accepted commands; fixed view and face continuity remain an empirical gate.

Real voice and video results must be recorded separately from mocked transport tests. Current deployment credentials/capacity may block real checks; do not claim them passed.

## Sources

- [Cartesia Line SDK](https://github.com/cartesia-ai/line): interruption-aware tools and custom reasoning.
- [Cartesia agent WebSocket sessions](https://docs.cartesia.ai/line/integrations/websocket-api.md): server-created sessions and browser transport.

## Status

Implementation and provider provisioning complete; integrated validation ongoing. LiveKit was evaluated, then superseded by the user's choice of Cartesia Line; do not ship two voice orchestrators.

## Approved provider choice

On 2026-11-14 the user explicitly chose Managed Agents for demo simplicity and approved Cartesia-hosted low-latency model billing instead of using their OpenAI key for the conversation model. Their OpenAI key remains in use for existing scene planning. Do not silently substitute gpt-5.4-mini. Create a dedicated convenience-store agent rather than modify the account's existing agent.

Cartesia ends hosting Python Line SDK agents on December 1, 2026; self-hosted SDK agents remain supported. This implementation uses the managed API and browser client tools, avoiding a separately hosted Python worker or a public callback to localhost.

## Provider and evidence (2026-11-14)

Dedicated agent provisioned with Haruka and `gemini-3.5-flash-lite`, no model fallback. Existing account agent left unchanged. Account catalog did not include gpt-6-luna; user approved the managed-model change. Chinese additional-language configuration was rejected by the real API; shipped voice supports Japanese and English. Chinese textual lesson intent remains supported separately.

Real browser voice E2E passed using synthesized Japanese WAV input through a MediaStream: actual STT, managed LLM, three client-tool calls, streaming TTS, three audio_output_clear events; sandwich detour → water replacement → saved-checkpoint rejoin. Microphone tracks ended on Leave. This run used practice video; it does not test human-microphone echo/noise or prove combined live video performance. Artifacts: `/tmp/il-voice-e2e/practice-voice-events.json` and `voice-rejoin.png`.

The browser remains the sole lesson-operation sequencer through client tools; no external-decision adoption path is required. In-flight committed decisions finish through audio interruption; queued unissued old-turn tools are discarded. Unknown state after a lost response requires a safe restart, never replay of uncertain video commands.

Combined voice + live-video validation was attempted but blocked before voice startup: one run failed to load the pinned Reactor SDK from esm.sh; the next loaded it but Dynamic had no capacity after four bounded retries. Both attempts cleaned up. Independent voice and video successes must not be described as one combined pass.

A repeated voice trial exposed a missed tool invocation: the agent verbally acknowledged a sandwich request without changing the lesson. Instructions were tightened and republished to require branch_scene before acknowledgment, with a regression example and an explicit priority over rehearsal. Fresh-session retests are recorded below; prompt compliance remains probabilistic.

After the mandatory-tool prompt update, a fresh real voice run passed in 23.6 seconds with three interruption clears, sandwich → water → rejoin, successful manual-mode snapshot reconciliation, and stopped microphone tracks. A subsequent run reached the same rejoin but its test assertion raced asynchronous response logging; the harness now awaits pending response reads before asserting route history. This test correction changes no product behavior.

Final verification: 144 full-suite tests passed before the added prompt-regression assertion; the affected voice/config subset then passed 11 tests. Syntax and staged whitespace checks passed. No Unity build was required because only portal and documentation files changed.

## Joint live acceptance follow-up

On 2026-11-14 the strengthened joint test passed with real Dynamic and Cartesia in 51.469 seconds. It confirmed two interrupted assistant turns, three clear events, sandwich → water → saved-mainline rejoin, monotonic operation revisions, continuously advancing video across four native frames, manual-mode reconciliation and clean Leave. Earlier capacity/CDN failures remain above as availability evidence. Synthesized Japanese microphone input still does not verify human microphone acoustics. See IMPLEMENTATION-unity-conversation.md for native Unity parity and verification.
