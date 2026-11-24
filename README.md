<p align="center"><img src="assets/logo.svg" width="144" alt="Reco+ logo"></p>

<h1 align="center">Reco+ — recommendation engine plugin for Jellyfin</h1>

<p align="center"><b>Personalized, Netflix-style recommendations for Jellyfin, computed from each user's whole watch history.</b></p>

<p align="center">
  <a href="https://github.com/crocodile13/jellyfin-plugin-recoplus/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/crocodile13/jellyfin-plugin-recoplus?sort=semver"></a>
  <a href="https://github.com/crocodile13/jellyfin-plugin-recoplus/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/crocodile13/jellyfin-plugin-recoplus/ci.yml?branch=main&label=CI"></a>
  <img alt="Jellyfin 12.2 to 12.4" src="https://img.shields.io/badge/Jellyfin-12.2%E2%80%9312.4-00a4dc">
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue"></a>
</p>

<p align="center"><i><a href="README.fr.md">Version française</a></i></p>

**Jellyfin's own recommendations are mostly random.** Reco+ replaces them with lists ranked for each person from everything they watched, liked, abandoned or asked for — and adds a Netflix-like home screen with "For you", "Because you liked …", trending and new-for-you rows. With Seerr, it can even suggest titles you don't have yet, with a one-click *Request* button.

<p align="center">
  <img src="assets/screenshots/accueil.jpg" alt="Home screen with four Reco+ rows: For you (with titles outside the library), New for you, Movies for you, Series for you" width="100%">
  <br><sub>A home screen with Reco+ rows: a title never appears twice, and the first row mixes in titles you don't have yet (Seerr cards from Jellyfin Enhanced).</sub>
</p>

## Why Reco+

| | Jellyfin alone | With Reco+ |
|---|---|---|
| **Suggestions** | random titles | your titles, ranked by your taste |
| **"Because you watched X"** | the last 7 movies only | your whole history, movies and series |
| **"More like this"** | genre overlap for movies, random series | real content similarity, the next part of a saga first |
| **Home screen** | recently added, continue watching | up to 10 personal rows, never the same title twice |
| **Titles you don't have** | — | suggested from your tastes, one-click request (optional, with Seerr) |
| **Measured on a real server** — the title someone started next was in the top 10 just before | 11 % | **18–19 %** |

## How it works, in 30 seconds

1. **Your history becomes a taste.** Every title you watched, abandoned, rated, liked, hid or requested gets a weight; old ones count less, abandons count against similar titles.
2. **Every title you haven't seen gets a score** by how much it resembles your tastes — its genres, keywords, director, actors… — and what people with the same tastes in your household watched.
3. **A guard rail**: freshness, popularity and ratings only help titles that *already* fit you. A new or popular title that isn't for you is never pushed.
4. **Your rows are built**: diversity, one entry per saga (the first part you haven't seen), new episodes of what you follow first, no duplicates between rows.

Everything is computed in the background: no page ever waits. The full explanation, with every formula, is in the **[wiki](https://github.com/crocodile13/jellyfin-plugin-recoplus/wiki)**.

## Install

### From the plugin repository (recommended)

1. In **Dashboard → Plugins → Repositories**, add:
   ```
   https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-recoplus/main/manifest.json
   ```
2. Install **Reco+** from the catalogue.
3. Restart Jellyfin.

New versions are then installed by Jellyfin's **Update Plugins** scheduled task (Dashboard → Scheduled tasks) and applied at the next restart.

### Manually

1. Download `RecoPlus_<version>.zip` from the [releases](https://github.com/crocodile13/jellyfin-plugin-recoplus/releases) and check it against the published `.sha256`.
2. Extract it into `<config>/plugins/RecoPlus_<version>/`, owned by the Jellyfin user.
3. Restart Jellyfin.

## First steps

1. Open **Reco+** in the dashboard menu (Plugins section). The defaults are the evaluated ones: it works as soon as it is installed.
2. On the **Status** tab, click **Recompute now**.
3. With Home Screen Sections, click **Add them for me** on the Status tab; titles and contents of the rows are set on the **Home** tab.
4. With Seerr, enter its URL and API key on the **Seerr** tab, click **Test**, then turn on **Extended mode** for titles you don't have yet.
5. See exactly what an account gets on the **Preview** tab: its rows as posters, with the reason for each title.

Settings come in three levels — **essential** (default), **advanced**, **expert** — chosen at the right of the tabs. Hover over **?** next to any setting for its help.

## Features

- **Everywhere Jellyfin recommends**: Suggestions, "Because you watched", "More like this", the Media Bar.
- **Home rows** with [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections): "For you", "Trending", "Popular", "Upcoming", "New for you", up to three "Because you liked …", "Movies for you", "Series for you". Each row: movies, series, both, or split in two.
- **Listens to everything you say**: favourites (series, season or episode), ratings, Jellyfin Enhanced reviews and hidden titles, Seerr requests and watchlist, titles shown but never opened.
- **Titles you don't have** (extended mode, optional): found from your tastes in the TMDb catalogue, ranked by the same engine, with a **discovery boldness** slider from cautious to bold.
- **Says why**: on the page of a title opened from a Reco+ row, a discreet line explains it ("Because you liked X", "Sequel to X", "New episodes"…).
- **Safe**: Jellyfin's access rules and parental control apply to every list; nothing is ever requested automatically.
- **Measured, not guessed**: the Status tab shows what accounts do with what is recommended (started within 7 days, finished within 30), and **Calibration** tunes the engine on your own history — only proposing what really generalizes.
- **Self-protecting**: every Jellyfin hook is checked at start-up; a part that no longer fits switches itself off, with an alert.
- **English, French, German and Spanish**, following each person's Jellyfin language.

## Screenshots

<table>
  <tr>
    <td width="50%" valign="top"><img src="assets/screenshots/general.png" alt="General tab: what Reco+ replaces, home row titles, explicit feedback sources, ignored accounts"><br><sub><b>General</b> — what Reco+ replaces, row titles, explicit feedback, ignored accounts.</sub></td>
    <td width="50%" valign="top"><img src="assets/screenshots/listes.png" alt="Lists tab: top of list composition, filters, excluded genres, diversity and sagas, pinned titles"><br><sub><b>Lists</b> — what is shown: randomness, minimums, filters, diversity, sagas, pinned titles.</sub></td>
  </tr>
  <tr>
    <td width="50%" valign="top"><img src="assets/screenshots/seerr.png" alt="Seerr tab: connection, internal and public URLs, API key stored apart"><br><sub><b>Seerr</b> — connection test; the key is stored apart and never sent back.</sub></td>
    <td width="50%" valign="top"><img src="assets/screenshots/notifications.png" alt="Notifications tab: webhook type, URL and token stored as secrets"><br><sub><b>Notifications</b> — webhook when a part switches itself off.</sub></td>
  </tr>
</table>

<sub>Interface available in English, French, German and Spanish (screenshots in French). Screenshots from version 1.0.20 on a real server: account names replaced, artwork blurred, titles made up.</sub>

<sub>PS: the rain is not a Reco+ feature. It is a personal whim of the author's "production" instance, where these screenshots were taken. The plugin recommends films; it does not do the weather.</sub>

## Requirements

- **Jellyfin 12.2 to 12.4** (other versions are detected and each hook is checked, but they are not tested).
- Optional, each adds something and nothing breaks without it:

| Plugin / service | What it adds |
|---|---|
| [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) | the home rows (it decides which rows each account sees and in which order) |
| [Seerr](https://github.com/seerr-team/seerr) (Jellyseerr / Overseerr) | requests and watchlists as tastes, trending/popular/upcoming rows, titles you don't have (extended mode) |
| [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) | star reviews and hidden titles; its cards and *Request* button for titles you don't have |
| [Playback Reporting](https://github.com/jellyfin/jellyfin-plugin-playbackreporting) | precise first-play dates, for calibration |
| [Media Bar Enhanced](https://github.com/IAmParadox27/jellyfin-plugin-media-bar) | shows Reco+ recommendations with its "Personalized Recommendations" option |

LocalRecs, if installed, duplicates recommendations: uninstalling it is advised.

## FAQ

**Why are Jellyfin's recommendations so random?** `/Items/Suggestions` returns random items, "Because you watched" only looks at the last few movies, and "More like this" picks random series sharing a genre. Reco+ replaces all three.

**Can I get a Netflix-like home screen?** Yes, with Home Screen Sections: Reco+ provides the rows, personalized per user.

**Can Jellyfin recommend movies I don't have yet?** Yes, with extended mode and Seerr. Nothing is requested automatically.

**Does it work for a family server?** Yes: each user gets their own lists, library access and parental controls are respected, and guest or child accounts can be ignored.

**Why is this title recommended to me?** Open it from a Reco+ row: a line under its name says why. Administrators can see every account's rows, with reasons, on the Preview tab.

**How do I stop seeing a title?** Hide it with Jellyfin Enhanced ("not interested"), or rate it badly: it disappears and similar titles go down.

## Privacy

Nothing leaves the server, except calls to **your** Seerr instance when you configure it and, in extended mode, posters loaded by the web client from `image.tmdb.org`. Titles shown but never opened stay on the server (one year at most) and only affect that account's ranking; the list of titles offered to each account (for the Status tab's measurement) stays on the server too and is forgotten 120 days after a title was last offered.

## Documentation

- **[Wiki: how Reco+ works](https://github.com/crocodile13/jellyfin-plugin-recoplus/wiki)** — what the engine reads, the exact score, home rows, extended mode, calibration, measurements, every setting.
- [Changelog](CHANGELOG.md).

## Development

```
src/RecoPlus.Engine/         the engine (pure .NET, no Jellyfin dependency)
src/Jellyfin.Plugin.RecoPlus/ Jellyfin integration, Seerr, API, web page and script
tests/RecoPlus.Tests/        unit tests + parity tests against the Python prototype
prototype/                   Python reference engine and the offline evaluation used to choose the defaults
tools/reco-export/           read-only, pseudonymised export of a server's history for offline evaluation
```

To build and test:

```bash
./build.sh
# or, without a local .NET SDK:
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 ./build.sh
```

The C# engine is checked to reproduce the Python prototype exactly:
- scores to 1e-9 and identical lists, on a synthetic dataset regenerated by CI on every run (`prototype/golden.py`);
- 0 differences over 126 evaluation cases on real data, during development.

CI builds and tests on every push. When `manifest.json` gets a new version, a release is published and the manifest is filled in automatically.

## Limitations

- Built and tested for Jellyfin 12.2 to 12.4 only.
- The defaults were chosen on a single, small household: use Calibration on yours.
- Titles you don't have are added by a script in the web client; TV and mobile apps get library recommendations only.

## License

[GPL-3.0](LICENSE).
