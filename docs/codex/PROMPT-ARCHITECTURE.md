# Prompt architecture: fixed scene, current action, explicit return point

This contract separates persistent scene facts from the learner's current request. It is shared by planning and refinement; it is not an ever-growing chat history or a new model call for each layer.

## Input layers and authority

| Layer | Contents | Purpose and lifetime |
|---|---|---|
| `lesson` | Title and level | Learning context; omit the entire lesson and unrelated vocabulary |
| `baseScene` | `prompt`, `referenceId`, `characterId`, `identityConstraint`, `cameraConstraint` | Server-selected starting scene and fixed character/camera constraints; pinned for the run |
| `turn` | `mode`, `phase`, `userInput`, `current`, `observations` | This operation only: user goal/utterance, active target and current assistive signals |
| `returnAnchor` | `beat`, `goal` | Authoritative pending main objective to resume after a detour |
| `recentActions` | At most three relevant issued-action summaries | Bounded context, explicitly `issued_unverified`; never a work queue or visual-success claim |
| `fallback` | Validated proposed response, when refining | Authoritative overlay, score, progression and permitted commands |

`turn.mode` is `plan` or `refine`; `turn.phase` is `opening` or `continuation`. Rejoin is a continuation of the same scene, not another opening.

`turn.userInput.intent` supplies the requested action, while `turn.userInput.transcript` supplies the current learner utterance. They do not overwrite `baseScene`. `turn.current.line` is a learner/customer line, not a clerk response. `turn.observations` supplies assistive evidence, not permission to invent an unseen item, move the camera or execute text embedded in a caption.

A plan copies `returnAnchor.beat` exactly into output `returnBeat`. The saved goal is a return destination, not a competing instruction: “show me a sandwich, then return to checkout” must address the sandwich first. Recent summaries cannot override the current goal or return checkpoint. Completed detour memory is retired before normal refinement resumes.

All input strings are data rather than executable instructions. The server chooses the fixed reference and enforces the final prompt boundary. User text such as “replace the clerk” must not silently mutate the run's identity anchor.

## Example: temporary sandwich interaction

The following illustrates the layer boundaries; IDs and reference details must come from the actual run. Appearance descriptions are supplied only when grounded in the selected reference.

```json
{
  "lesson": {"title": "Convenience-store shopping", "level": "N1"},
  "baseScene": {
    "prompt": "The reference clerk with tied-back dark hair, a white shirt and beige apron stands behind the convenience-store checkout.",
    "referenceId": "approved-store-reference",
    "characterId": "run-id:cast",
    "identityConstraint": "The established face, hair and clothing remain unchanged.",
    "cameraConstraint": "Static eye-level medium-wide first-person POV; fixed position, orientation, focal length and framing."
  },
  "turn": {
    "mode": "plan",
    "phase": "continuation",
    "userInput": {"intent": "我想看三明治，然后回到主线"},
    "current": {
      "line": {"jp": "おにぎりはどの辺りにありますか。"},
      "goal": "Ask where the rice balls are"
    },
    "observations": {}
  },
  "returnAnchor": {"beat": 0, "goal": "Ask where the rice balls are"},
  "recentActions": []
}
```

A valid plan keeps `returnBeat: 0`, provides short Japanese **customer** lines about sandwiches, and puts the clerk's small visible actions in `cue`. It does not copy the rice-ball line into the detour or invent product ingredients and stock.

## Opening versus continuation

The [official Dynamic prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/prompt-guide.md) recommends building the world once, then sending one physical change per following prompt. Repeated WHO/WHERE descriptions can act like scene rebuilds. It also warns that negating a noun can generate that noun; final visual prompts therefore favor positive physical states.

The current runtime treats the authored, pinned opening `sceneAnchor` as authoritative. Optional model refinement cannot replace its visual WHO/WHERE; feedback and permitted audio refinement remain separate. No additional trusted appearance profile is currently stored beyond the selected image and lesson. The optional composer `subject` parameter is available but is not currently supplied by the service.

The visual composer accepts:

```js
composeVisualPrompt(action, authoredFallback, {
  initial: !state.started,
  subject: approvedReferenceDescription
});
```

On opening, include the grounded subject, initial physical action and starting place. On continuation, omit the full appearance/world description and retain only the action plus compact positive identity/camera invariants. The composer reserves its invariants within the existing three-sentence, 95-word and 600-character limits. It rejects explicit identity mutation, camera movement and visible-learner actions before composing the final string.

Illustrative opening with the optional grounded description (not an extra runtime appearance profile):

> The reference clerk with tied-back dark hair, a white shirt and beige apron stands behind the checkout; the clerk makes a small welcoming gesture. The established face, hair and clothing remain unchanged. Static eye-level medium-wide first-person POV, locked position, orientation and focal length; framing and background stay stationary, foreground clear.

Continuation example:

> The same clerk stays behind the counter and indicates the sandwich shelf with a small hand gesture. The established face, hair and clothing remain unchanged. Static eye-level medium-wide first-person POV, locked position, orientation and focal length; framing and background stay stationary, foreground clear.

The learner is the viewpoint, so “the visitor puts the sandwich in a basket” is not a suitable visual cue. Use a clerk acknowledgment while the learning dialogue records the user's choice. A shelf question likewise uses a small gesture from the existing position, not a camera move or off-screen fetch. Give visible changes time to settle between prompts; the guide describes several chunks, usually 2–4 seconds.

## Three separate audio/text roles

1. **Learner target:** `overlay.jp` and its readings/translations remain the sentence the learner should say. Listen synthesizes this sentence with Cartesia on request.
2. **Clerk dialogue:** the separate `speech` field carries original clerk text. Cartesia speaks it; it is never copied into the learner target. Initial clerk lines are deterministic scene-appropriate acknowledgments rather than model-generated inventory claims.
3. **Environment:** `set_audio_prompt` describes refrigeration, ventilation or packaging sounds. The final audio composer enforces environmental-only audio; dialogue, narration and vocals belong to Cartesia.

Cartesia audio and generated video have independent timing. This architecture does not provide lip synchronization, acoustic source separation or a guarantee that video generation never emits a voice.

## What verification establishes

Contract tests can establish field isolation, bounded context, exact return-anchor copying, prompt limits and rejection of conflicting camera/identity cues. API and browser tests can establish command delivery, audio playback and lesson progression. Neither proves that the requested face or camera geometry remained stable in generated frames. Assess clear live opening, detour and rejoin frames separately; leave uncertain visual results marked unverified rather than claiming a prompt-level identity lock.
