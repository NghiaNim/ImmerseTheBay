# Feedback after every rehearsed sentence

Issues #25 and #37. Base `86f209e`, branch `codex/demo-every-sentence-feedback`. Own rehearsal request routing and related tests in `portal/agent/{service.js,summary.test.js}` and sentence feedback in `Assets/Scripts/Learning/{LearningAgentClient.cs,LearningContract.cs,LearningOverlay.cs}`.

The native Speak control and the managed Rehearse Line tool submit a practice attempt. The Portal scores the transcript against the displayed target, including off-target wording that resembles a request to branch. The separate branch control and tool continue to request detours. A retry on the same target preserves the current generation.

The glasses display the result in the central field. The card remains while the app prepares the next scene or plays the next voice line. The last accepted sentence publishes its result before the session summary. A recognized utterance receives a sentence match score. Recognition failure keeps its existing microphone retry cue.

Test the same target with an off-target shopping phrase, the correct phrase, a hinted phrase and an explicit detour. Check the result while video is switching, while the clerk is speaking and on the last sentence. Measure the physical view through Air 2 Ultra before recording the final video.
