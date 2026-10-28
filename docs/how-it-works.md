# How Reco+ works

*[Version française](how-it-works.fr.md) · [Back to the README](../README.md)*

## What the engine takes into account

- **Whole history, with decay.** A title watched long ago still counts, but less. The half-life is 90 days, the MovieLens value reported by Larrain, Parra et al., RecSys 2015.
- **Abandoned titles count against similar titles, with a margin.**
  - A movie stopped between 5 % and 70 % and never resumed for 30 days is an abandon.
  - A movie watched in two sittings is not.
  - A long series dropped after one or two episodes is an abandon too.
- **Favourites** weigh more, at the level they were given: a favourite series counts fully, a favourite season partly (0.6), favourite episodes a little (0.3, reached at 3 episodes) — liking an episode is not liking the series.
- **What the account said explicitly** wins over what viewing suggests, in both directions: Jellyfin Enhanced reviews (1–5 stars) and Jellyfin's rating or thumbs. Titles hidden everywhere in Jellyfin Enhanced ("not interested") are never suggested again and pull similar titles down; hides limited to a home row or to episodes do not count.
- **Liked but not watched here** (thumb or favourite on a title never played on this server — seen elsewhere, or a misclick): counts as taste at half weight (setting), so it guides without dominating.
- **Seerr requests** are a *short-term* declared interest, and the **Seerr watchlist** counts like a request, weaker (0.4):
  - half-life 21 days, ignored after 90;
  - once the title is actually watched, what the account did with it counts instead;
  - they feed the taste profile, but never push **their own** title: a requested title is ranked without its own request, held back right after the request or its arrival — shown in no row at all for one half-life (the account already knows it wants it — it does not need to discover it) — then comes back gradually if it stays unwatched (same 21-day half-life): an old forgotten request that fits the account's taste may resurface.
- A rated (review, thumbs up) or favourite title is not recommended: it has been seen (setting: "Also propose what the account has already marked").
- **At the top of the list: what is new in what the account follows**, for 45 days after it arrives (up to 3 titles), never a title it requested:
  - new episodes or a new season of a series watched on this server that the account had nearly caught up with (no like needed, just a positive weight: not abandoned, not hidden);
  - for a series liked but not watched here (we do not know how far the account got), only episodes that aired recently;
  - the next part of a saga whose earlier parts were watched (or liked) — for a saga only liked, only a recent release. A fully watched saga has nothing left to place.
- **Content.** TF-IDF vectors over genres, keywords, studios, main actors, directors, writers, creators, decade and kind — plus, optionally, original language and production countries. The account's taste combines its overall profile with the closest title it liked.
- **Optional, off by default** (no measurable gain on the reference history; Calibration can propose them on yours): rewatches, recent *release* (as opposed to recent addition), original language and countries. The quality bonus can use the community rating, the critics' rating, the better of the two or their mean.
- **Confidence in ratings.** The community rating is pulled towards 6.5 when it rests on few votes (Bayesian average, 150 virtual votes; TMDb vote counts read through Seerr): 9/10 on 12 votes ≈ 6.7, on 20,000 votes = 9. **Household reviews** (Jellyfin Enhanced, other accounts only) are a reserved bonus with the same idea: 2 neutral virtual reviews, so one 5-star review weighs little and five matching reviews a lot.
- **"Watched together" in the household.** Item-to-item co-occurrence with shrinkage (Ferrari Dacrema et al., 2019), so one person who watches everything does not link every title to every other one.
- **Guard rail.** Freshness, household popularity and community rating are *multiplicative bonuses reserved for the titles that already rank among the account's top 60 by taste*.
  - A brand-new title the account has no affinity with gets no bonus at all.
  - Without this rule, new arrivals were pushed to everyone — for example new fan-service anime to a Ghibli-only account.
  - The rule cannot be switched off, and freshness is capped.
- **Watch time.** A series weighs by the hours actually watched (each episode's runtime; 45 min if unknown): 150 fifteen-minute episodes do not weigh as much as 150 forty-five-minute ones.
- **Unknown dates.** A title marked watched without a date, or bulk-marked (at least 4 titles "started" — or "finished" — less than 2 minutes apart), counts as an **old** taste — neither ignored nor mistaken for yesterday's favourite. A title started long before and bulk-marked as finished keeps its real start date. With Playback Reporting, the date of a title is its last *real* play, which "mark as watched" does not rewrite.
- **Replaced files.** When Radarr or Sonarr upgrades a file, Jellyfin creates a new item and Playback Reporting keeps the plays under the old one: they are attached to the current title by name (when unique), restoring its real first-play date.
- **Diversity, three modes.**
  - *Personalised* (default): the list is as varied as what the account likes. Several Maximal Marginal Relevance strengths (Carbonell & Goldstein, 1998) are tried and the list whose internal similarity is closest to that of the account's liked titles is kept: an eclectic viewer gets a varied list, a one-genre fan a focused one.
  - *Calibrated* (Steck, RecSys 2018): the share of each genre in the list follows the account's history (70% thrillers watched → about 70% suggested, not 100%).
  - *Fixed*: the same redundancy penalty for everyone (behaviour before 1.0.5).
- **Shown but never opened.** A title that stayed on an account's screen for several days without being opened slowly sinks (at most 40% of its score), then recovers over time (30-day half-life). Only what the home-screen script saw **actually displayed to the signed-in account** counts: a card of a Reco+ row visible for at least one second, tab in the foreground, once per day. Opening the title resets its counter.
- **Current taste, optional** (off by default): similarity to the last 5 liked titles, on top of long-term taste.
- **Time of day, optional** (off by default): similarity to liked titles watched in the same time slot (night, morning, afternoon, evening); one list per slot is then computed and served according to the time of the request (configurable time zone).
- **New accounts.** With no known taste every title scores the same, so a prior is added: what the household watches, then the community rating. Its weight fades continuously with the account's taste (half at about two liked titles, negligible after).
- **Duplicates.** A title present twice in the library (same TMDb id, e.g. in two libraries) is proposed once, and not at all if either copy was watched.
- **Current lists** (with Seerr): at each full computation, Reco+ reads TMDb's trending (this week or today), popular and upcoming lists, for movies and for series (40 of each by default, cached 6 hours). Each one gives a home row ("Trending", "Popular right now", "Upcoming") where each account only sees what it has not watched, ordered by an adjustable blend of TMDb's order and its taste. These rows come right after "For you" in the duplicate-free allocation. Their content is set like the others: library only, titles outside the library (not requested, parental control respected), or both. Without Seerr they are not registered with Home Screen Sections.
- **Trends in popularity**: the popularity bonus also counts the TMDb trend, in every row. For a trending title, popularity = 1 − (1 − household) × (1 − share × intensity), the intensity being 1/√(1 + place in the list) (1st trend 1, 4th 0.5); adjustable share (0.5 by default, 0 = household only). The guard rail stays: only for titles that already match the taste. A title that is not trending keeps exactly its score.
- **Movies, series, or both**: every row (except "Movies" and "Series") can show movies and series, movies only, series only, or movies and series apart as two rows ("…: movies" and "…: series"). On Save, Reco+ updates each account's home screen in Home Screen Sections: where the row was shown, its two halves take its place (and the other way round when merged back); an account that kept only one half keeps it.
- **"New for you" row**: only recent arrivals that rank in the top quarter by taste, so a quiet month does not fill it with whatever just arrived.
- **Sagas.**
  - At most one entry per saga, and it is the first volume not yet seen, not volume 4 — in "For you" and in every other row ("New", "Because you liked …", movies, series). A new arrival in the middle of a saga whose start is unseen does not go into "New".
  - A saga is recognised from three clues: the movie's TMDb collection (a new part is recognised even before Jellyfin files it in its collection), Jellyfin collections, and the name ("Overlord" then "Overlord: The Sacred Kingdom": same original language, a studio or author in common, and no TMDb collection saying otherwise). A movie that continues a series comes after the series.
  - A Jellyfin collection that mixes several TMDb collections (a director's filmography, "Marvel"), or is larger than a saga, is a thematic group: at most two entries, never a "first volume".
  - A sequel whose start is not in the library (TMDb keyword "sequel", no known saga) comes after everything else.
- **"Because you liked X"**: X is always a title that was watched (movie seen or half watched, at least one episode of a series), never one that is only a favourite, liked or requested.
- **Watchlist and "Like"**: Seerr and Jellyfin Enhanced mirror the watchlist into Jellyfin's "Like"; on a watchlisted title not watched yet, that Like counts as an intent to watch, not as a review. Once the title is watched (movie seen or half watched, series half watched), a Like left in place is a review.
- **TMDb agreement, in the library too** (needs Seerr, on by default): for each account, the TMDb recommendations of its 30 most liked titles (cached, shared with extended mode) are read; a library title that TMDb recommends from several of them gets a bonus — what TMDb's whole audience watches together, which a household of a few accounts cannot learn alone, and which exists the day a title arrives. Each mention weighs by how much the account likes the source title and fades with the position in TMDb's list (1/√(1 + position)); a title recommended from everything (a worldwide hit) is damped like a too-frequent word (IDF). Like the other bonuses, it only applies to titles that already rank among the account's top by taste.
- **Effect of the recommendations** (State tab): every computation notes, per account, the library titles offered in its rows (row and position the first time). The watch history then tells what happened: share started within 7 days, watched in full within 30 days, by row and by position, and the share of titles started in the last 30 days that had been offered shortly before. Nothing is asked of the accounts; a title offered too recently is not counted yet. These are the numbers to watch before changing a setting.

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
  - A full recompute is a scheduled task with visible progress, run after library additions (at most once an hour), at startup only if no account has a list yet (an account without a list is computed alone), or by hand ("Recompute now"). No nightly run by default.
  - Each account is recomputed once its activity calms down (2 minutes without a new playback end, watched mark, favourite or rating), and at most once every 15 minutes.
  - Saving settings or applying a calibration does not recompute by itself: use "Recompute now", otherwise each account picks it up at its next activity.
- **Other plugins.** It cooperates with them rather than racing them.
  - The Suggestions filter runs innermost, so Jellyfin Enhanced's hidden-content filter still applies to its output.
  - The web script is injected at request time, like Jellyfin Enhanced does, without writing to the web folder.

## Configuration

Dashboard → Plugins → Reco+. The page has one tab per topic:

| Tab | Content |
|---|---|
| State | compatibility checks, last computation, per-account readiness, TMDb agreement for the library, current lists read, effect of the recommendations (started within 7 days, finished within 30, by row and position) |
| General | what to replace, what is read besides the history (including titles shown but never opened and TMDb recommendations for the library), ignored accounts, interface language |
| Home | one line per Home Screen Sections row, in allocation order: title, movies and/or series (or two separate rows), content (library only, outside the library only, or mixed); current lists (trending period, titles read, share of taste in the order); how to mix (one every N cards, or freely by the engine's joint ranking on the signals both kinds of titles have: taste, rating, recent release; optionally a minimum number of outside titles shown and "discovery" slots for the best-rated ones); row size. What depends on Seerr or extended mode is greyed out with the reason when they are off |
| Lists | what is shown, applied after ranking: share of daily randomness, minimum movies / series, excluded genres, minimum rating and year, whether titles already marked (rated, favourite, requested) are proposed, diversity (personalised, calibrated or fixed), sagas, what is new in what the account follows |
| Engine *(advanced)* | every ranking factor (favourites, requests, abandons, similarities, watched together, freshness, popularity, rating, TMDb agreement, content features) can be switched off or made weaker/stronger (×0.25 to ×3); durations; defaults are the evaluated ones |
| Seerr | URL and key, with a **Test** button that uses the values typed in without saving them |
| Extended mode | titles outside the library: on/off and how many (their place on the home screen is set in the Home tab; each outside title goes to one row only, the library titles it replaces move down to the next rows, every row keeps its size); sources (each can be switched off): recommendations and similar titles of liked titles, missing parts of sagas, works of recurring directors/creators/writers, the best-rated titles of TMDb overall, of the favourite genres, themes (keywords), studios/networks and original languages and of their crossings (genre × theme, genre × language), and little-known gems (highly rated, few votes), read at pages drawn at random each day; adjustable daily noise; the preview shows what each source brought; minimum rating, vote count and movie length (short films left out); the Seerr exploration of a full recompute only runs for recently active accounts (a play or just signing in), and at least once a week for the others |
| Notifications | webhook, with a test button |
| Calibration *(advanced)* | method (automatic, learned choice model, coordinate search, neural network) with a warning when the history is too short, result, apply and rollback; see below |
| Preview | what an account currently gets, row by row (and, in developer mode, what each extended-mode source brought) |
| Log | developer mode: Reco+'s log in real time (or all of Jellyfin's), filterable, secrets masked |

Three levels:

- **Basic**: the tabs above, except Engine, Calibration and Log.
- **Advanced**: the Engine and Calibration tabs (always visible, tagged *advanced*), and the folded "Advanced settings" sections of every tab.
- **Developer mode** (checkbox at the right of the tabs, remembered per browser): the Log tab, the calibration data export, the statistical settings of the calibration and the step-by-step path of each method, and what each extended-mode source brought in the Preview.

**Language.** The plugin is in English and French: page, home rows and cards, notifications. *Jellyfin's* (default) follows each person's Jellyfin interface language (otherwise the server's); English or French force it for everyone. Row titles left empty use the default title in the reader's language.

Every tab shows the essential settings; advanced ones are folded away. Hover (or tap) the **?** next to a setting for its help and default value. A single **Save** button stores both the settings and any secret you typed.

### Calibration

The Calibration tab replays your server's history: every time an account started a new title, **at which rank** was that title in the list computed just before, using only what was known then?

- **Data.** Exactly what the recommendations use: watch history, Seerr requests and watchlists, TMDb vote counts, Jellyfin Enhanced reviews and hidden titles; ignored accounts left out. Nothing is asked from Seerr during a calibration (last snapshot and cache only).
- **No hindsight.** Each case only sees what existed at that moment: a title started before but finished after is seen in progress, a request counts from its date. A title requested or watchlisted before being watched is not a case: the account found it alone (in real use it is held back on arrival, not recommended).
- **Measure.** Rank gain 1/log2(1 + rank) over *all* candidates, not just "in the top 10 or not": far more statistical power from the same history.
- **Cases.** Every title started (known date, bulk marks excluded) once the account had adopted at least 2; a title started but not adopted (stopped movie, series at 1–2 episodes) counts half. Each case is weighted by how much history the account already had (n/(n+3)), and accounts are balanced (1/√cases) so a heavy viewer does not decide alone.
- **Methods.**
  - *Learned choice model* (default): in each case the account picked one title among all those it could have been offered; the 10 score weights that make these choices most likely are learned in one go (conditional logit, projected Newton, weights kept positive and bounded). L2 regularisation **towards the current settings** — without evidence nothing moves — with its strength chosen by cross-validation over accounts. Few parameters, so usable with a short history (about 10 cases per weight); about one minute.
  - *Coordinate search*: one parameter at a time within safe bounds, over 30+ settings (content and signals included): a 5-point grid (log scale for weights, zero tried for optional factors), then golden-section refinement; discrete choices enumerated (diversity mode included); two passes. A change is kept only if it improves the whole *and* does not lose on either half of the accounts. Needs much more history (about 20 cases per setting).
  - *Neural network* (experimental): a small non-linear correction (10 → 8 → 1, tanh, 96 parameters) learned on the score's ingredients and added to the engine; it starts at zero and regularisation pulls it back without evidence. Needs a lot of history (about 10 cases per parameter).
  - *Automatic*: runs all of them and keeps the one that generalises best; slow and CPU-hungry (up to an hour).
  - The page shows, for each method, whether the history is **sufficient, borderline or insufficient**.
- **Validation.** Each method is redone without one group of accounts and measured on that group, for every group (cross-validation by account; by default √(number of accounts) groups, rounded, between 2 and 10: as many groups as accounts per group). The regularisation strength of the learned models is chosen the same way.
- **How far to move.** Every method yields a path away from the current settings: each change kept by the coordinate search, or a quarter, half and all of the way to a learned model. Cross-validation measures every point of the path, and the shortest one within one standard error of the best is kept (the "one-standard-error" rule: without clear evidence, move less; the start of the path means "keep the current settings"). This is what stops the coordinate search from overfitting.
- **Recommendation.** A setting is **recommended** only if its gain holds on accounts that did not help choose it (bootstrap 95% CI above zero) and reaches at least 1%: below that nothing would be visible.
- **Settings of the calibration itself** (Calibration tab, "Calibration settings", developer mode, saved at once; 0 = automatic): number of groups of the cross-validation, of the slow methods and of the nested regularisation choice, how far to move (prudent = one standard error, very prudent = two, or the best step), passes and refinements of the coordinate search, minimal gain to recommend. Settings that do not apply to the chosen method are greyed out. A tiny change to a weight (under 0.02 or 3%) is not proposed: it is just noise.

The calibration data can be exported, pseudonymised (accounts renamed u01, u02…, no names or IDs), to evaluate it elsewhere: **Export the calibration dataset** button (developer mode).

Nothing is applied until you click **Apply**; the previous settings are kept for rollback. It refuses to propose anything with fewer than 50 usable cases. It runs as a scheduled task, on half the CPU cores by default ("Threads" setting, which also sets how many accounts are computed at the same time).

On the reference household (144 cases over 13 accounts, once the 22 fake cases of a bulk mark are removed and started titles added at half weight):

Path of each method (1.0.23, 5-fold cross-validation by account, no hindsight; gain relative to the defaults):

| Method | Steps | In-sample along the path | Cross-validation along the path | Kept |
|---|---|---|---|---|
| Coordinate search | 18 | +0.1% → +16.0% | −0.7% → −3.9% (never above 0) | none |
| Learned choice model | ¼, ½, all | 0% → −0.3% | −0.3% → −1.3% | none |
| Neural network | ¼, ½, all | 0% | ±0.1% | none (no gain on the history) |

Coordinate search is textbook overfitting: every change it keeps improves the cases used to tune it (+16% after 18 changes) and makes unseen accounts worse. Each point of the path is now measured, so this is visible and nothing is proposed: the defaults stay. That is the safeguard working, not a failure. Automatic mode takes about 27 minutes on two cores.

New features were measured the same way before choosing their defaults: weighting series by watch time does as well as counting episodes (letting very long series exceed 1 does worse, hence the cap at 1); time of day does worse (−0.7% to −3.8%: too few views per slot and account) and stays off; personalised diversity gives +0.8% ranking quality over fixed diversity (95% CI [−0.1%; +2.4%]) and becomes the default; calibrated diversity is neutral at moderate strength and loses at high strength; current taste brings nothing on this history (the half-life already follows recent interests) and stays off. Titles shown but never opened cannot be measured this way (no display history before 1.0.5), so the default penalty is deliberately moderate.
