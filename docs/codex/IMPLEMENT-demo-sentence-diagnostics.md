# Targeted sentence retry feedback

Issue #37. Branch `codex/demo-sentence-diagnostics`, base `4c8819b`. Own Portal sentence diagnostic code, reviewed reading data, attempt metadata projection, closed wire schema and focused tests. With the integration owner, extend only correction display in Unity LearningContract, LearningAgentClient and LearningOverlay.

An optional `attempt.correction` identifies one supported target span in a low-score retry. `start` and `end` are UTF-16 offsets, with an exclusive end. `jp` is the exact substring from `attempt.jp`. The same committed attempt carries the full recognized transcript in `attempt.text`. Display the short result cue first. Rehearsal then emphasizes that span if the visible target still matches `attempt.jp`. Missing or ambiguous spans keep the generic retry path. This is a comparison with speech recognition text and supplies no acoustic pronunciation judgment.

After the 1.8-second result emphasis, the glasses replace the result card with the word to practice and the recognized text. They underline the identified reading group or bold the span in the sentence layout. A changed target visit, completion, or closed result clears the suggestion. The established phone score card stays visible during its usual result window.

Verification included the full 227-case Portal suite, the Portal syntax check, and the Android assembly compiler with these Unity files substituted into its response file and output directed to a temporary directory. Signed installation, optical readability and microphone recognition still require the shared S25 and Air 2 Ultra.
