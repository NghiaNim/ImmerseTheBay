# Cartesia managed conversation agent

Despite the historical folder name, this integration does **not** run a Python or LiveKit worker. Cartesia hosts the conversational agent. The browser streams microphone PCM over the managed WebSocket, plays Cartesia audio, and executes three client tools through the existing portal lesson API. The browser remains the sole Visko driver.

## Provisioning

Node 22+ is sufficient; no new package dependency. Keep `CARTESIA_API_KEY`, `CARTESIA_VOICE_ID`, and resulting `CARTESIA_AGENT_ID` in ignored `portal/.env`. The portal also uses `PORTAL_TOKEN` for its own authenticated API; it is never sent to Cartesia. No OpenAI key is sent to the managed agent. The existing scene planner still uses the configured OpenAI key/model independently.

From the repository root:

```sh
node --test portal/voice-worker/config.test.mjs
node --env-file=portal/.env portal/voice-worker/provision.mjs
# Explicitly creates dedicated tools/agent and updates ignored portal/.env:
node --env-file=portal/.env portal/voice-worker/provision.mjs --create
```

The default command only checks the catalog and prints the proposed non-secret configuration. Creation records resource IDs after each successful mutation in `provisioned.json`. It reuses recorded tool IDs after a partial failure and refuses to create a second agent if an agent receipt already exists. Existing unrelated agents are never overwritten. An ambiguous network failure during a POST requires account inspection before retrying; the Cartesia create endpoint does not provide a documented idempotency guarantee.

The user approved managed LLM billing. `gemini-3.5-flash-lite` was the fastest allowed model in the account catalog on November 14, 2026 (catalog average 596 ms, not an application latency measurement). The faster `gpt-5.4-mini` is explicitly excluded by user preference. `gpt-6-luna` was absent from the complete 13-model catalog. Model fallback is disabled. Provisioning stops if the chosen model is unavailable.

## Language limitation

Live configuration validation rejected Chinese as an additional language: multilingual agents currently accept English, Spanish, French, Hindi, Japanese and their locales. This agent starts in Japanese with English available for explanation. **Chinese microphone recognition is not verified or supported by this managed configuration.** Do not advertise Chinese voice support based only on the LLM understanding Chinese text.

## Tools and interruption contract

| Client tool | Parameters | Portal operation |
| --- | --- | --- |
| `branch_scene` | `{intent: string}` (500 characters maximum) | New shopping detour |
| `return_to_main` | `{}` | `action: "return_to_main"` |
| `rehearse_line` | `{transcript: string}` (1000 characters maximum) | Score actual learner utterance |

All tools require a `client_tool_result` matching `tool_call_id`, with a compact string result and `is_error` on failure. The browser serializes lesson mutations, verifies the bound run against the authoritative snapshot, and uses request IDs/revisions for deduplication. Return accepted decision metadata promptly; never wait for video generation before replying to the agent. Tool result success means the decision committed, not that video finished.

On `audio_output_clear`, stop playback and discard queued audio immediately. Cancel unissued stale work where possible. Once an HTTP lesson mutation is issued, preserve and adopt its committed decision even if the learner interrupts or Cartesia ignores a late tool result. Audio cancellation must never erase a committed revision. Reconnect using a fresh server-created session; do not reuse session tokens or replay stale tool calls. Transport/control implementation lives in the portal voice modules, not this configuration folder.

Clerk audio comes only from Cartesia during voice mode; video prompts request environmental sound. The agent never promises exact visual alignment or face identity guarantees.

## Verification scope

Configuration unit tests and real API provisioning are verified. Creating an agent does not prove microphone capture, barge-in, multilingual recognition, tool execution, or video rendering. Those require the browser end-to-end checks. The receipt contains public resource IDs only, no credentials.

## Official references

- [Managed agent configuration](https://docs.cartesia.ai/agents/configuration.md)
- [Managed model catalog and billing](https://docs.cartesia.ai/agents/models.md)
- [Client tools](https://docs.cartesia.ai/agents/client-tools.md)
- [WebSocket audio, interruption and session tokens](https://docs.cartesia.ai/line/integrations/websocket-api.md)
- [Line SDK migration](https://docs.cartesia.ai/agents/migrate-from-line-sdk.md): Cartesia-hosted Python SDK agents end December 1, 2026; self-hosted Line remains an alternative for custom provider keys. This project selected managed agents explicitly.
