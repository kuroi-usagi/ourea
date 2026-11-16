# How sure are we? Checks on the water map, the list and the routes

Owner: Kason. Every number here comes from a script in `pipeline/` and a JSON file in `handoff/findings/`, so anyone can re-run it.

## 1. Is the water map right? (NISAR against Sentinel-1)

We compared each NISAR water map with an independent radar: **Sentinel-1C**, one day later (descending pass, 12 Jul, 29 Aug and 4 Oct 2026, via Microsoft Planetary Computer). Sentinel-1 water = VV backscatter below −18 dB with a 3×3 clean-up, on the same grid. Script: `pipeline/validate_s1.py`; numbers: `handoff/findings/validation_accuracy.json`.

| NISAR pass | Pixels that agree | NISAR finds Sentinel-1's water (recall) | Sentinel-1 confirms NISAR's water (precision) | F1 | Kappa |
|---|---|---|---|---|---|
| 11 Jul (baseline) | 75.3% | 95% | 8% | 0.14 | 0.11 |
| 28 Aug | 78.2% | 98% | 45% | 0.62 | 0.49 |
| 3 Oct | 73.0% | 99% | 52% | 0.68 | 0.48 |

New flood water only (the thing we rank on): F1 **0.56** (28 Aug) and **0.59** (3 Oct). That's 1,335 km² from NISAR against 1,087 km² from Sentinel-1 on 3 Oct.

**The honest headline:** NISAR misses almost none of the water Sentinel-1 sees. But it calls about twice as much area water as Sentinel-1 does, and only about half of NISAR's water pixels are confirmed.

Where they disagree:
- **Almost all on cropland and rice paddies.** Towns and tree cover agree 98–99.8%.
- **That's partly physics.** NISAR's L-band (24 cm) radar passes through rice and sees the water under it. Sentinel-1's C-band (5.6 cm) bounces off the crop and misses it, so Sentinel-1 undercounts there too.
- **And partly our threshold.** NISAR's −13 dB water rule is lenient. With Otsu thresholds instead, F1 rises to about 0.75–0.78 on the flood passes.
- **In July most rice fields are flooded on purpose**, so NISAR's water there is real but not a flood. This is one reason we rank only *new* water against that baseline.

Caveats: neither map is ground truth (this is two satellites compared, a day apart), and cloud cover of 76–95% ruled out a Sentinel-2 optical check. Next step: a stricter NISAR threshold, checked against field reports from Đồng Tháp.

## 2. Does the list depend on the baseline date?

**Yes, and we say so.** "New flood water" means water on a pass that was land on the dry-season baseline pass of 11 Jul 2026. The list ranks hamlets by water that arrived *since the dry season*. Pick another baseline and you are asking a different question, so you get a different list.

`pipeline/validate_baseline.py` re-ranks the 3 Oct 2026 hamlets with the formula in `science/score.py`, changing only the baseline. Results are in `handoff/findings/validation_baseline.json`.

| Baseline | Top 10 kept | Top 5 kept | Where the original #1 lands |
|---|---|---|---|
| 11 Jul (the one we use) | 9 of 10 reproduced | 5 of 5 | 1 |
| 28 Aug (water new since August) | 3 | 0 | 38 |
| No baseline (all water counts) | 4 | 1 | 2 |
| Water on both earlier passes removed | 3 | 0 | 29 |

How to read this:
- By 28 Aug, 39% of the frame was already water (27% on 11 Jul). A late baseline removes most of the season's flood and ranks only the last month's rise. That's a different question: "where did water arrive in September?"
- With no baseline, permanent canals and rivers count as flood, which the method deliberately excludes. The original #1 still comes 2nd.
- Caveat: the WorldPop under-5 raster wasn't on the machine that ran the check. A building-share stand-in was used outside the 105 published squares, so these counts are a best case.

**What we tell users:** the list answers "where has flood water reached homes with young children since the dry season?" It is re-checked every pass, and it is not a forecast.

## 3. Does the list depend on the weights?

Not much. The score is 35% homes near flood water, 25% young children near it, 20% newly flooded area and 20% schools near flood water (`science/score.py`). In `handoff/findings/sensitivity.json` (Mitch), each weight is halved and raised by half, one at a time, on the 3 Oct pass:

- **Top 10:** 7 to 10 of the 10 stay.
- **Top 5:** 3 to 5 of the 5 stay.
- **Most sensitive weight:** homes near water (×0.5 keeps 7 of 10).

The app's TOP 10 list draws each hamlet's score as a bar split into these four parts. It shows what pushed the hamlet up the list, along with a one-line stability note.

## 4. Near water is not under water: dikes and raised roads

Roads in Đồng Tháp often run on dikes. The old route rule counted any metre within 100 m of new flood water. `pipeline/make_osm_routes.py` now classes every ~20 m of a route using the radar and the bare-earth ground (FABDEM V1-2):

- **Under water:** the radar sees new flood water on that spot.
- **Low, near:** within ~100 m of new flood water and less than 1 m above it. "Above it" compares the ground with the mean ground height of the flood pixels around it.
- **Raised, near:** within ~100 m but at least 1 m above that water, which means a dike or raised road.

**At risk** = under water + low, near.

On the 3 Oct pass, the shortest walking routes from the top-10 hamlets:
- **Old rule:** 56% "near water".
- **New rule:** 42% at risk, with 13% on raised dikes and roads.
- **Driest alternatives:** for 6 routes the pipeline also found one at most 50% longer that cuts risk, for example from 33% to 7% for the #1 hamlet's day care.

Caveat: FABDEM is accurate to about ±1–2 m in flat deltas, so "raised" is a hint and not a survey. The text in the app says so.

## 5. Which school? Walking distance, and day care first

- **Destinations by walking distance:** they are now picked on the OpenStreetMap road network (7,600 km of roads and paths, with dead ends within 30 m joined), not by straight line. In several hamlets the straight-line nearest school is not the walking-nearest. One is 8.3 km away in a straight line but 15.5 km on foot.
- **Day care first:** flood-season day care is what keeps under-fives away from the water, so the routes page lists the nearest day care first and routes to it by default.
- **Choosing the school:** public data says nothing about which school a hamlet's children actually attend. So the page asks: "pick the school your children attend". Any of the 94 schools and day-care points can be chosen.
- **Hamlets with no road:** 2 of the top-10 hamlets per pass have no mapped road within 1 km. That's likely boat access, or gaps in OSM. They get no route, only a note to check on the ground. We don't invent one.
- **Data gaps:** OSM lists only 86 schools and 8 day-care points in this frame, so some real ones are missing.

## 6. What a commune office can take to a meeting

The routes page has a **CSV** button and a **PRINT SHEET** button. Each covers every check-first hamlet on the chosen pass: the children under 5, the nearest day care and school, the walking distance and time, and how much of the route is at risk, under water or raised. Both come from our OSM routes; Google's routes are shown only on the Google map, as its terms require. The commune office message drafts in the app have a **SHARE · ZALO** button. On a phone it opens the share sheet, so the message goes to Zalo. On a laptop it copies the text and opens Zalo Web.
