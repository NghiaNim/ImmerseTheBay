# Align demo words and romaji in the glasses

Demo master plan #16, language presentation #23. Branch `codex/demo-reading-groups`, based on the clerk captions work. Own the Portal reading-group helper, the wire schema and the enrichment next to wire validation, its tests, and the agent contract. Scene progression and Unity rendering have separate owners.

- Author reviewed groups for the three demo turns. Publish them only when both the Japanese and whole-line romaji rejoin correctly and the full kana reading agrees.
- Older or generated lines without verified groups keep their available whole-line reading. No model request or word-boundary guess is made at turn time.
- Progression and scoring retain the original unsplit target.
