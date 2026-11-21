# Bind the prepared partner voice to the lesson run

Issue #36. Branch `codex/demo-partner-voice-binding`. Base `f9a34a0`.

Owned: Portal lesson validation, practice/scenario generation and routes, agent service and speech routes, replay compilation, their focused tests, Unity's clerk request, Tenin's live preparation/launch and display code, and this brief. Existing managed and scripted Cartesia adapters resolve the server-owned logical style. No provider credentials or official reference files belong in this task.

## Current behavior and evidence

- Tenin Live Lesson offers Auto, Female and Male before generating the lesson. The ready screen shows the resolved choice. The voice-only entry defaults to N5.
- Photo casting uses visual presentation only when the conversation partner is clearly visible. Unknown and off-camera cases use the female default. Prompt-generated partners are cast with the requested voice in the same generation call.
- The active run pins the resolved style. Managed speech reads the same snapshot field as the scripted clerk route. Unity sends run identity and revision, while Portal supplies the exact clerk line and style. Saved replay snapshots retain the style after expiry and restart. Older authored lessons without a style continue to use the default configuration.
- Full Portal checks passed with 214 tests. Standalone Tenin Android preview compiled. The real Cartesia speaker adapter returned two distinct Japanese WAVs through the exact-run HTTP route with synthetic run fixtures. [Cartesia's voice catalog](https://www.cartesia.ai/voices/library?language=ja) lists Haruka and Hiroshi.
- Both voice IDs are in the env example. The local ignored Portal config received the verified voice IDs. Managed conversation with separate voices still requires a female and male agent ID with the same tools and behavior. Live model casting, device playback and wearer validation remain pending.
