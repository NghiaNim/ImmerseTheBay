# Record an automated Japanese rehearsal

This test automation accompanies #37 and #39. It owns `portal/agent/e2e-synthetic-demo.cjs` and this runbook. It does not change app code, open the device microphone, alter the installed S25 app or start a Reactor generation.

## Run

Install Playwright, Chromium, FFmpeg and FFprobe locally. Start a Portal with valid Cartesia TTS and server STT, and with `ja-konbini-demo-001` available. Select the pinned Portal with `PORTAL_URL`. On a protected Portal, provide `PORTAL_TOKEN` through the environment. Never put the token in the video, fixtures or source control.

```sh
PLAYWRIGHT_MODULE=/absolute/path/to/playwright \
CHROME_PATH=/absolute/path/to/chrome \
PORTAL_URL=http://127.0.0.1:8793 \
E2E_ARTIFACTS=/absolute/path/to/demo-material/synthetic \
node portal/agent/e2e-synthetic-demo.cjs
```

The harness puts the Cartesia Japanese samples into a synthetic browser MediaStream and uses the lesson's normal Speak and Stop buttons. The saved transcript comes from the real server STT response. The score, retry focus and independent or assisted outcome come from the actual lesson decision. Expected order is independent pass at the sandwich shelf, deliberate particle substitution at the drinks cooler, pass there after Hint, and independent pass at checkout. The run fails if the provider normalizes the intended bad sample into a passing answer.

The script captures app screenshots with a clearly marked test evidence panel and the three fixture photographs. It assembles those screens and their corresponding learner WAV clips into a playable H.264/AAC walkthrough. `report.json` holds the spoken fixture, recognized transcript, match outcome and any localized correction. Keep the output folder outside Git.

The walkthrough documents real recognition and transcript matching. It does not measure acoustic pronunciation, provide native XREAL footage, or show live Dynamic transitions. The deliberate bad sample says the wrong Japanese particle. Simulator and wearer validation remain separate.
