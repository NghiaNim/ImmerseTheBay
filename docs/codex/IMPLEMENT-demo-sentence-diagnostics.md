# Targeted sentence retry feedback

Issue #37. Branch `codex/demo-sentence-diagnostics`, base `4c8819b`. Own Portal sentence diagnostic code, reviewed reading data, attempt metadata projection, closed wire schema and focused tests. With the integration owner, extend only correction display in Unity LearningContract, LearningAgentClient and LearningOverlay.

An optional `attempt.correction` identifies one supported target span in a low-score retry. `start` and `end` are UTF-16 offsets, with an exclusive end. `jp` is the exact substring from `attempt.jp`. The same committed attempt carries the full recognized transcript in `attempt.text`. Display the short result cue first. Rehearsal then emphasizes that span if the visible target still matches `attempt.jp`. Missing or ambiguous spans keep the generic retry path. This is a comparison with speech recognition text and supplies no acoustic pronunciation judgment.
