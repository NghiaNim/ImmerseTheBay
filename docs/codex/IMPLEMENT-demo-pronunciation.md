# Playback-timed pronunciation (#19)

This slice owns Portal's Cartesia synthesis and learning audio route, and the Unity lesson example player and reading overlay. Teach with the actual sample clock and keep a static vocabulary emphasis independent of the active span. The phone Review player can use the same WAV and timing metadata.

`POST /api/agent/pronunciation` accepts a Japanese `text` of at most 120 UTF-16 code units and an optional boolean `slow`. It returns PCM WAV and an optional `X-Tenin-Speech-Ranges` JSON header with UTF-16 offsets and times in milliseconds. No timing is supplied if provider tokens cannot be matched back to the original string. The existing speech endpoints keep their response format.
