# Durable session replay (#26)

Base: PR #15, `3e02168`. Branch: `codex/demo-portal-records`.

Ownership: `portal/records/**`, the recorder invocation in `portal/agent/service.js`, and this brief. Store a bounded snapshot of only the lesson metadata needed to compile a replay, and identify recorded targets without using the Japanese text as a key. Preserve reads of old records and their original-lesson fallback. Cover expiry, restart, repeated target text, and replay after detour rejoin in regression tests.

## Storage and compatibility

New records save a versioned replay source on disk. The source contains title, level, scene and audio prompts, partner metadata, contiguous beat ranges, and a bounded vocabulary budget. Encountered lines already live in the event log, alongside target IDs to distinguish repeated text. The snapshot is internal and does not enlarge the public session list/detail responses.

A replay uses a durable saved photo only while that photo exists and belongs to the learner. Transient practice media is never copied into the record. A replay whose transient or deleted photo is unavailable starts from the stored prompt. Old records fall back to their original lesson. An already-expired legacy record lacking a snapshot cannot recover that missing lesson metadata.

Validation: Portal syntax checks, full Portal suite, and an HTTP regression exercising photo expiry, a restarted record/lesson store, partitioning two identical Japanese sentences into their original beats, and running the compiled replay. This slice does not change Android or Unity clients.
