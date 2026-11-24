# Contributing to Reco+

Thanks for helping! Issues and pull requests are welcome, in English or in French.

## Reporting a problem

Use the **Bug report** form: Reco+ and Jellyfin versions, other plugins installed, and — for anything on the home screen or the settings page — the red lines of the browser console (F12). Never post API keys, tokens or session cookies. Security issues: report them [privately](https://github.com/crocodile13/jellyfin-plugin-recoplus/security/advisories/new).

## Before writing code

- Read the [wiki](https://github.com/crocodile13/jellyfin-plugin-recoplus/wiki): it describes the engine exactly.
- For anything bigger than a fix, open an issue first so we can agree on the approach.
- **Ranking changes are measured before they ship.** An idea that cannot show a gain (Calibration on a history, or the integration bench) stays off by default. The C# engine must keep reproducing the Python prototype (`prototype/`) exactly: change both.

## Building and testing

```bash
./build.sh                                   # engine tests, build, package (.NET 10 SDK)
cd tests/client && npm install --no-save jsdom@24.1.3 && node client.test.mjs && node configpage.test.mjs
cd prototype && python golden.py ../tests/RecoPlus.Tests/golden.json   # after an engine change
```

Every pull request runs CI and the integration test (a real Jellyfin in Docker, with and without the other plugins, and a real Seerr).

## Conventions

- Code comments are in French; commits, pull requests and changelogs in English.
- Every text shown to users is written in French (the source) and translated in `english.json`, `german.json` and `spanish.json` (`src/RecoPlus.Engine/`); the tests check it.
- No secret, real data or personal information anywhere in the repository.

The maintainer reviews and merges pull requests; versions are grouped (1.minor.patch) and published automatically.
