## What and why

<!-- What this changes, and the problem it solves. Link the issue: "Fixes #123". -->

## How it was checked

<!-- Tests added or run, what you looked at on a real server, measurements for a ranking change. -->

## Checklist

- [ ] Small and focused: one change per pull request.
- [ ] `./build.sh` passes (engine tests, build, package).
- [ ] New behaviour has a test (engine: xUnit in `tests/RecoPlus.Tests`; settings page or home script: jsdom in `tests/client`).
- [ ] **Ranking change**: measured (Calibration or the integration bench) — a change that is not measured stays off by default — and done in both the C# engine and the Python prototype (`prototype/`, parity checked to 1e-9).
- [ ] **Text shown to users**: written in French (the source language) and translated in `english.json`, `german.json` and `spanish.json`.
- [ ] **Settings**: help text, default value and level (essential / advanced / expert) set; nothing duplicated from an existing setting.
- [ ] Docs updated if behaviour changes: README (English and French) and the wiki page concerned.
- [ ] No secret, real watch history, server address or personal data in code, logs, screenshots or this pull request.

<!-- CI and the integration test run on every pull request; I review and merge. Thank you! -->
