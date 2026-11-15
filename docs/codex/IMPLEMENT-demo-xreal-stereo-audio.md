# Balance XREAL scripted voice playback

Issues #18 and #39. Branch `codex/demo-xreal-stereo-audio`, based on `4c8819b`.
Own `portal/agent/cartesia.js`, `portal/agent/routes.js`,
`portal/agent/cartesia-routes.test.js`, and this brief.

The wearer heard the clerk in the left ear. The saved Cartesia sample is
44.1 kHz mono PCM16. Unity can opt into an explicit two-channel file through
`X-Tenin-Audio-Layout: stereo` on `/api/agent/clerk-speech` and
`/api/agent/speak`. Each mono sample, including its sign and amplitude,
is written to both channels. Valid stereo WAVs pass through unchanged.
The opt-in validates RIFF and retains the existing size limit. Requests
without the header retain their current audio and JSON contracts.

Unity owns sending the header for scripted clerk speech and pronunciation
examples. The targeted Portal suite passed 16 checks. A local replay of an
actual Cartesia WAV returned 183,456 identical left/right sample pairs.
`ffprobe` read the output as 44.1 kHz stereo PCM16. Wearer verification
must compare clerk, example and score cue in both ears on the signed app.
Managed conversation and generated ambience use separate playback paths.
