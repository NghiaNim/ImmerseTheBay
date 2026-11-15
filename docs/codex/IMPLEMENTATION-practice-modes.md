# Home and real-world practice with completion review

Implement two simple Web demo entry modes: Home uses existing authored scenes; Real world accepts a scene photograph and a spoken or typed practice goal, generates a bounded 2–4-line Japanese micro-lesson, and uses the same conversation/branching/runtime. Keep Japanese, romaji and translations. Generation must inspect the image, not substitute a fixed store scene. Unknown visual details must not be invented.

The visible clerk/partner should make natural small mouth movements while speaking; dialogue audio remains Cartesia only, Dynamic ambience only. No strict lip sync guarantee. Keep identity and fixed camera constraints.

End-of-practice review: words actually encountered, completed/attempted targets, first-attempt success, transcript-match scores with denominators, hints and review suggestions. Never label transcript similarity as pronunciation accuracy. Exclude intent/idle/manual-skip events from scored attempts. Replays cannot count twice. No score when no scored attempts.

## Ownership

- Practice generation: new portal/practice/**, additive ephemeral lesson support in portal/agent/lessons.js, shared-store wiring and route registration in portal/server.js. Real image+goal structured generation with limits, TTL, tests. No edits to service.js/routes.js.
- Summary metrics: portal/agent/service.js, routes.js, new summary module/tests. Same transactional/replay boundaries as turns. Coordinate ephemeral store with generator.
- Web: portal/public/learn.html/js/css and new mode/summary UI helpers. Photo+spoken goal, mode selectors, completion review, clean disconnect before review. No backend edits.
- Root: visual mouth-action prompt composition/tests, integration, E2E, docs. Do not touch Unity/XR/settings editor changes in this Web-first slice.

Do not add frameworks or dependencies. Keep existing endpoints compatible. Real provider E2E and mocked regression must be reported separately; store generated lessons and photos only transiently, no keys/media in logs.
