# Show clerk captions in romaji and English

Demo master plan #16, language presentation #23. Branch `codex/demo-clerk-captions`, base `36aa775`. Own the Portal clerk script, service, wire schema, translation route, their tests, the agent contract, and this brief. Unity's subtitle view and client transport are owned by their respective integration tasks.

- Send parallel Japanese speech text, Hepburn romaji and English for authored or generated scripted partner lines. Keep the speech text and audio route unchanged. Carry the extra fields only on the server-owned wire response.
- Translate and romanize managed conversation transcripts in one request when the client opts in. A provider failure leaves the original available to the client. Preserve the original English-only route contract.
- Verify known scripted captions without using a provider, the generated partner path, negotiated response shape, and result validation.

## Verification

- Portal syntax check and 220 tests passed. Route coverage includes opt-in, English-only compatibility, invalid input and provider errors. Turn coverage checks immediate scripted and generated partner captions.
- The existing configured OpenAI model returned “How many pairs of chopsticks would you like?” and “Ohashi wa nanzen otsuke shimasu ka?” for a synthetic clerk line in 1.33 s. [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses)
- S25 and Air 2 Ultra display and audio timing remain a wearer check after Unity integration.
