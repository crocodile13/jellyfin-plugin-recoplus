# Reco+ — personal recommendations for Jellyfin

*[Version française](README.fr.md)*

Reco+ replaces Jellyfin's built-in recommendations with lists computed from each account's **whole** watch history.

It has been built and tested against **Jellyfin 12.2** only.

| Where | Jellyfin today | With Reco+ |
|---|---|---|
| `/Items/Suggestions` | random items | the account's ranked list |
| `/Movies/Recommendations` ("Suggestions" tab, Media Bar…) | similar to the 7 last movies and a few random favourites | "Because you liked X" categories built from the whole history |
| "More like this" (movies) | weighted overlap of genres/tags/people | content similarity + a bit of the account's taste, next saga volume first |
| "More like this" (series) | **random** series sharing a genre or a tag | content similarity, same as movies |
| Home Screen Sections | — | a "For you" row and, optionally, "New for you", up to three "Because you liked …" rows, "Movies for you", "Series for you" — a title never appears in two of these rows |
| Outside the library | — | optional: titles from the Seerr/TMDb catalogue **mixed into the "For you" row**, with Jellyfin Enhanced's cards and *Request* button |

Everything is computed in the background and cached in memory and on disk, so no page ever waits for a computation. An account without a ready list simply gets Jellyfin's normal answer.

## What the engine takes into account

- **Whole history, with decay.** A title watched long ago still counts, but less. The half-life is 90 days, the MovieLens value reported by Larrain, Parra et al., RecSys 2015.
- **Abandoned titles count against similar titles, with a margin.**
  - A movie stopped between 5 % and 70 % and never resumed for 30 days is an abandon.
  - A movie watched in two sittings is not.
  - A long series dropped after one or two episodes is an abandon too.
- **Favourites** weigh more, at the level they were given: a favourite series counts fully, a favourite season partly (0.6), favourite episodes a little (0.3, reached at 3 episodes) — liking an episode is not liking the series.
- **What the account said explicitly** wins over what viewing suggests, in both directions: Jellyfin Enhanced reviews (1–5 stars) and Jellyfin's rating or thumbs. Titles hidden everywhere in Jellyfin Enhanced ("not interested") are never suggested again and pull similar titles down; hides limited to a home row or to episodes do not count.
- **Seerr watchlist** counts like a request, weaker (0.4) and just as short-term. A title that is only *wanted* (watchlist, old request, favourite never watched) stays a candidate — usually the best one.
- **New season** of a series the account had caught up with and liked: placed at the top for 45 days after the new episodes arrive (up to 2 series).
- **Seerr requests** are a *short-term* declared interest:
  - half-life 21 days, ignored after 90;
  - once the title is actually watched, what the account did with it counts instead;
  - a requested title that just arrived and is still unwatched is pinned at the top for 30 days.
- **Content.** TF-IDF vectors over genres, keywords, studios, main actors, directors, writers, creators, decade and kind — plus, optionally, original language and production countries. The account's taste combines its overall profile with the closest title it liked.
- **Optional, off by default** (no measurable gain on the reference history; Calibration can propose them on yours): rewatches, recent *release* (as opposed to recent addition), original language and countries. The quality bonus can use the community rating, the critics' rating, the better of the two or their mean.
- **Confidence in ratings.** The community rating is pulled towards 6.5 when it rests on few votes (Bayesian average, 150 virtual votes; TMDb vote counts read through Seerr): 9/10 on 12 votes ≈ 6.7, on 20,000 votes = 9. **Household reviews** (Jellyfin Enhanced, other accounts only) are a reserved bonus with the same idea: 2 neutral virtual reviews, so one 5-star review weighs little and five matching reviews a lot.
- **"Watched together" in the household.** Item-to-item co-occurrence with shrinkage (Ferrari Dacrema et al., 2019), so one person who watches everything does not link every title to every other one.
- **Guard rail.** Freshness, household popularity and community rating are *multiplicative bonuses reserved for the titles that already rank among the account's top 60 by taste*.
  - A brand-new title the account has no affinity with gets no bonus at all.
  - Without this rule, new arrivals were pushed to everyone — for example new fan-service anime to a Ghibli-only account.
  - The rule cannot be switched off, and freshness is capped.
- **Watch time.** A series weighs by the hours actually watched (each episode's runtime; 45 min if unknown): 150 fifteen-minute episodes do not weigh as much as 150 forty-five-minute ones.
- **Unknown dates.** A title marked watched without a date, or bulk-marked (at least 4 titles "started" less than 2 minutes apart), counts as an **old** taste — neither ignored nor mistaken for yesterday's favourite.
- **Replaced files.** When Radarr or Sonarr upgrades a file, Jellyfin creates a new item and Playback Reporting keeps the plays under the old one: they are attached to the current title by name (when unique), restoring its real first-play date.
- **Diversity, three modes.**
  - *Personalised* (default): the list is as varied as what the account likes. Several Maximal Marginal Relevance strengths (Carbonell & Goldstein, 1998) are tried and the list whose internal similarity is closest to that of the account's liked titles is kept: an eclectic viewer gets a varied list, a one-genre fan a focused one.
  - *Calibrated* (Steck, RecSys 2018): the share of each genre in the list follows the account's history (70% thrillers watched → about 70% suggested, not 100%).
  - *Fixed*: the same redundancy penalty for everyone (behaviour before 1.0.5).
- **Shown but never opened.** A title that stayed on an account's screen for several days without being opened slowly sinks (at most 40% of its score), then recovers over time (30-day half-life). Only what the home-screen script saw **actually displayed to the signed-in account** counts: a card of a Reco+ row visible for at least one second, tab in the foreground, once per day. Opening the title resets its counter.
- **Current taste, optional** (off by default): similarity to the last 5 liked titles, on top of long-term taste.
- **Time of day, optional** (off by default): similarity to liked titles watched in the same time slot (night, morning, afternoon, evening); one list per slot is then computed and served according to the time of the request (configurable time zone).
- **Sagas.** At most one entry per saga, and it is the first volume not yet seen, not volume 4. Large thematic collections get at most two entries.

## Measured on one real server (be careful with these numbers)

Protocol: every time an account started a new title, would that title have been in the top 10 computed just before, using only what was known at that time? There are 126 such cases over 11 accounts.

| Method | Adopted title in top 10 |
|---|---|
| Random (Jellyfin's `/Items/Suggestions`) | 2 % |
| Jellyfin 12 recommendations (faithful replica) | 11 % |
| **Reco+ (default settings)** | **18–19 %** |

- Reco+ found the title 13 times where Jellyfin missed it, and missed it 4 times where Jellyfin found it.
- The 95 % confidence interval of the gain, bootstrapped by account, goes from **0 to +13 points**: Reco+ is not worse, but with so little history the size of the gain is uncertain.
- A variant without the guard rail scored 32 %, but only by recommending new arrivals to everyone; it was rejected.
- The abandonment rule for movies could not be validated: only 4 movies were left in progress in that history.

## Safety and robustness

- **Version guard.** At start-up Reco+ checks the Jellyfin version and every hook it uses (service registration, controller actions and parameters, optional plugins).
  - A part that no longer matches is disabled on its own, and Jellyfin's normal behaviour comes back for it.
  - So is a part that throws 5 errors within 10 minutes.
  - You are told through the Jellyfin activity log, a red banner on the plugin page, and optionally a webhook (ntfy, Gotify, Discord or generic JSON).
- **Rights.** Every list goes through Jellyfin's own access rules for the account (libraries, parental rating).
  - Titles outside the library must have a known certification no higher than the account's limit; this is stricter than Jellyfin.
- **Secrets.** The Seerr API key and the webhook URL and token are stored in a separate file with mode 600.
  - They are never returned by the API; the page only shows "set (…1234)".
- **Requests to Seerr** are made *as the user*, with their own permissions, quotas and auto-approval.
  - Nothing is ever requested automatically.
- **Light on the server.**
  - History is read in a single read-only query.
  - One computation runs at a time.
  - A full recompute is a scheduled task with visible progress.
  - Each account is recomputed 2 minutes after playback.
  - The library is re-indexed after additions (debounced).
- **Other plugins.** It cooperates with them rather than racing them.
  - The Suggestions filter runs innermost, so Jellyfin Enhanced's hidden-content filter still applies to its output.
  - The web script is injected at request time, like Jellyfin Enhanced does, without writing to the web folder.

## Installation

### From the plugin repository

In Dashboard → Plugins → Repositories, add:

```
https://raw.githubusercontent.com/crocodile13/jellyfin-plugin-recoplus/main/manifest.json
```

Then install **Reco+** from the catalogue and restart Jellyfin.

### Manually

1. Download `RecoPlus_<version>.zip` from the [releases](https://github.com/crocodile13/jellyfin-plugin-recoplus/releases).
2. Check it against the published `.sha256`.
3. Extract it into `<config>/plugins/RecoPlus_<version>/`, owned by the Jellyfin user.
4. Restart Jellyfin.

## Configuration

Dashboard → Plugins → Reco+. The page has one tab per topic:

| Tab | Content |
|---|---|
| State | compatibility checks, last computation, per-account readiness |
| General | what to replace, Home Screen Sections rows, sources (including titles shown but never opened), ignored accounts |
| Lists | what is shown, applied after ranking: share of daily randomness, minimum movies / series, excluded genres, minimum rating and year, diversity (personalised, calibrated or fixed), sagas, pinned requests |
| Engine | every ranking factor (favourites, requests, abandons, similarities, watched together, freshness, popularity, rating, TMDb agreement, content features) can be switched off or made weaker/stronger (×0.25 to ×3); durations; defaults are the evaluated ones |
| Seerr | URL and key, with a **Test** button that uses the values typed in without saving them |
| Extended mode | titles outside the library: on/off; mixed into the home row one every N cards, or freely by the engine's joint ranking |
| Notifications | webhook, with a test button |
| Calibration | method (automatic, learned choice model, coordinate search) with a warning when the history is too short; see below |
| Preview | what an account currently gets, row by row |

Every tab shows the essential settings; advanced ones are folded away. Hover (or tap) the **?** next to a setting for its help and default value. A single **Save** button stores both the settings and any secret you typed.

### Calibration

The Calibration tab replays your server's history: every time an account started a new title, **at which rank** was that title in the list computed just before, using only what was known then?

- **Measure.** Rank gain 1/log2(1 + rank) over *all* candidates, not just "in the top 10 or not": far more statistical power from the same history.
- **Cases.** Every title started (known date, bulk marks excluded) once the account had adopted at least 2; a title started but not adopted (stopped movie, series at 1–2 episodes) counts half. Each case is weighted by how much history the account already had (n/(n+3)), and accounts are balanced (1/√cases) so a heavy viewer does not decide alone.
- **Methods.**
  - *Learned choice model*: in each case the account picked one title among all those it could have been offered; the 10 score weights that make these choices most likely are learned in one go (conditional logit, projected Newton, weights kept positive and bounded). L2 regularisation **towards the current settings** — without evidence nothing moves — with its strength chosen by cross-validation over accounts. Few parameters, so usable with a short history (about 10 cases per weight); about one minute.
  - *Coordinate search*: one parameter at a time within safe bounds, over 30+ settings (content and signals included): a 5-point grid (log scale for weights, zero tried for optional factors), then golden-section refinement; discrete choices enumerated (diversity mode included); two passes. A change is kept only if it improves the whole *and* does not lose on either half of the accounts. Needs much more history (about 20 cases per setting).
  - *Neural network* (experimental): a small non-linear correction (10 → 8 → 1, tanh, 96 parameters) learned on the score's ingredients and added to the engine; it starts at zero and regularisation pulls it back without evidence. Needs a lot of history (about 10 cases per parameter).
  - *Automatic* (default): runs all of them and keeps the one that generalises best.
  - The page shows, for each method, whether the history is **sufficient, borderline or insufficient**.
- **Validation.** Each method is redone on half of the accounts and measured on the other half (two-fold cross-validation by account). A setting is **recommended** only if its gain holds on accounts that did not help choose it (bootstrap 95% CI above zero).

Nothing is applied until you click **Apply**; the previous settings are kept for rollback. It refuses to propose anything with fewer than 50 usable cases. It runs as a scheduled task, on half the CPU cores.

On the reference household (144 cases over 13 accounts, once the 22 fake cases of a bulk mark are removed and started titles added at half weight):

| Method | In-sample | Cross-validation (95% CI) | History |
|---|---|---|---|
| Coordinate search | +17.8% | −3.1% [−11.5; +8.0] | insufficient |
| Learned choice model | −0.4% | +0.7% [−0.9; +4.5] | sufficient |
| Neural network | 0% | −2.1% [−6.0; +2.5] | insufficient |

Coordinate search is textbook overfitting: +18% on the cases used to tune it, −3% on unseen accounts. No method is recommended, so the defaults stay. That is the safeguard working, not a failure. Automatic mode takes about 23 minutes on one core (choice model: one minute; network: six).

New features were measured the same way before choosing their defaults: weighting series by watch time does as well as counting episodes (letting very long series exceed 1 does worse, hence the cap at 1); time of day does worse (−0.7% to −3.8%: too few views per slot and account) and stays off; personalised diversity gives +0.8% ranking quality over fixed diversity (95% CI [−0.1%; +2.4%]) and becomes the default; calibrated diversity is neutral at moderate strength and loses at high strength; current taste brings nothing on this history (the half-life already follows recent interests) and stays off. Titles shown but never opened cannot be measured this way (no display history before 1.0.5), so the default penalty is deliberately moderate.

## Optional integrations

| Plugin / service | If present | If absent |
|---|---|---|
| Home Screen Sections | Reco+ rows (enable them in HSS settings too); "Because you liked …" titles are per account | no home row; everything else works |
| Jellyfin Enhanced | extended mode uses its cards, detail modal and *Request* button, with the live Seerr status (if it is linked to Seerr) | Reco+ cards, opening the Seerr page, with Reco+'s own *Request* button |
| Media Bar Enhanced | benefits automatically (it calls the replaced endpoints) | — |
| Playback Reporting | precise first-play dates for calibration | calibration is less precise; Reco+ also records first plays itself from install time |
| Seerr (formerly Jellyseerr) | requests as a signal, pinned arrivals, extended mode | all of this is off; the rest works |
| LocalRecs | flagged as a duplicate | — |

## Privacy

Titles shown but never opened stay on the server (a plugin file, one year at most) and only affect that account's ranking; the "Titles shown but never opened" switch turns this off.

Nothing leaves the server, with two exceptions:
- calls to **your** Seerr instance, when you configure it;
- in extended mode, the web client loads posters from `image.tmdb.org`.

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

- Built for Jellyfin 12.2. Other versions are detected and each hook is checked, but they are not tested.
- The defaults were chosen on a single, small household (about 16 active accounts). Use Calibration on yours.
- Titles outside the library are added by a script in the web client (Modern layout tested): mixed into the Home Screen Sections "For you" row, or as a separate row without HSS. Other clients (TV, mobile apps) get the library recommendations only.

## License

[GPL-3.0](LICENSE).
