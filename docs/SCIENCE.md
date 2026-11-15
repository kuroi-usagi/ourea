# Science notes (Mitch Cahill)

_Pasted from Mitch's write-up, Nov 15 2026. This is the team's fact-check reference: use these numbers and wording everywhere._

Ourea: Science write-ups (Mitch)
Nov 14, 2026 · @Someone
Fact-check: child drowning numbers
Use "nearly 2,000 children die from drowning in Vietnam every year" (Ministry of Health, 2025). Do not use "7,000": it comes from one 2019 news article and does not match any official figure.
Claim
Verdict
What to say instead
Source
About 7,000 children drown each year in Vietnam (UNICEF)
Don't use
Nearly 2,000 children die from drowning each year
7,000 appears only in a 2019 VietNamNet article; the Ministry of Health, Jul 2025 says nearly 2,000
Drowning is a leading cause of child death
Safe
Drowning is the leading cause of death for children aged 5 to 14 in Vietnam
Ministry of Health, Jul 2025
The Mekong Delta has Vietnam's highest child drowning rate
Use with a date
"Reported in 2019 as having the country's highest drowning rate"
VOV, Jun 2019; no newer source found tonight
Young children are most at risk in our area
Safe, strong for us
In Đồng Tháp, 29 children drowned in 2018 and 79% were under six
VOV, Jun 2019
Supervision works
Safe, older evidence
Supervised village crèches cut child drowning deaths by more than 80% in a Vietnam study
Alliance for Safe Children and UNICEF research, reported by VietNamNet
Why this matters for Ourea: the last two rows are the pitch. The children who drown here are mostly very young, and supervision near water prevents most deaths, so knowing where flood water is near young children is the useful thing.
Tell Christopher and the README owner: the README's line "Vietnam's highest child drowning rate" should say "reported in 2019 as".
Polarization note: how NISAR sees flood water through clouds
NISAR sends its own radar signal, so it sees the ground through monsoon cloud, day or night. On our Jul 11 and Aug 28 passes the Sentinel-2 optical photos are almost entirely cloud; the radar maps every pixel.
What the brightness means. Calm water acts like a mirror: the signal bounces away from the satellite, so open water looks dark. Rough land scatters some signal back and looks medium. Where water lies under plants, the signal bounces off the water, then off the stems, and straight back, which is the bright double bounce.
HH and HV. NISAR sends a horizontally wiggling wave (H) and listens two ways. HH (heard horizontal) is strongest for mirror-like and double-bounce returns. HV (heard vertical, the wiggle twisted) only comes back from messy scattering in leaves, so it measures vegetation.
Surface
HH
HV
Calm open water
Dark
Dark (near the noise floor, about −24 dB on our passes)
Bare or harvested field
Medium
Dark
Leafy crops or forest
Medium
Bright
Water under rice or trees
Very bright
Medium
Why HH is the main water test. NISAR looks at the ground at a slant. At a slant, water reflects horizontal wiggles much better than vertical ones (the same reason polarized sunglasses cut glare off a lake), so HH gives the cleanest water/land contrast and the strongest double bounce. HV alone fails in July: bare, freshly harvested fields are as dark as water in HV. Our test called 68% of the Jul 11 tile "water" with HV alone.
Our rule (science/classify.py). Water = HH darker than a per-pass threshold (kept between −18 and −14 dB) and HV below −20 dB, so nothing leafy is in the pixel, then a 3×3 clean-up of single-pixel speckle.
Why L-band. NISAR's L-band wave is about 24 cm long. It passes through cloud and reaches further into crops and canopy than Sentinel-1's 5.5 cm C-band, which is why it can catch water under rice. Sentinel-1 records VV and VH rather than HH and HV, so any 2025 comparison uses open-water extent only.
Drivers hypothesis: why the flood looks like this
The flood is driven by the Mekong rising upstream, then stored on the flat floodplain long after the river falls. Dikes and the rice calendar decide which fields fill. Water on the land peaked after the river did.
NISAR pass
Water in study tile
River at Tân Châu, ~18:00 (pass time)
Oct 3
55%
2.75 m (falling)
Aug 28
39%
2.88 m (rising)
Jul 11
27%
1.04 m (tide-driven)
1.  The river rises from upstream. Tân Châu climbed from about 1 m in July to a peak of about 3.2 m around Sep 21, then fell to about 2.3 m by Oct 15. Before August the daily tide controls the gauge (swings of about 1.5 m a day); from August the river flow overrides it.
2.  Water lingers on the floodplain. On Oct 3 the river was lower than on Aug 28, yet the tile held 55% water against 39%. The Plain of Reeds is flat and drains slowly, so fields stay full for weeks after the peak. For children, the danger outlasts the river peak, and a gauge alone would say the flood is easing.
3.  2026 was a low-flood year. Tân Châu stayed below its alarm level 1 of 3.5 m all season (alarm level reported by VietnamPlus). A normal year would flood more.
4.  Dikes decide which fields fill. The flooded areas are sharp rectangles: low-dike compartments that let the river in on purpose. High-dike areas keep the flood out to grow a third rice crop. So a rectangle is often the real flood here, not a rice paddy.
5.  The rice calendar changes the radar picture. The summer–autumn harvest (Jul–Aug) leaves bare fields, and the autumn–winter crop is planted inside high dikes from August. In 2026 officials told 3-crop areas to cut back that crop because of El Niño risk (VAN, Apr 2026), so there may be less of it than usual.
Not tested tonight: upstream rainfall, dam operations and tides at the coast. Treat them as possible causes of the low peak, not findings.
Headline numbers (provisional)
On the Oct 3 2026 NISAR pass, about 28,000 children under 5 lived within 100 m of flood water: 35% of the roughly 81,000 young children in our 4,006 km² study tile.
• Flood water = water on a pass that was dry land on the driest pass (Jul 11). Permanent rivers and canals are left out on purpose: nearly every home in the delta is beside a canal, so "any water nearby" is close to 100% everywhere.
• 1,335 km² of land flooded between Jul 11 and Oct 3; water went from 27% to 55% of the tile.
• Children under 5 near flood water: about 21,700 on Aug 28 and about 28,100 on Oct 3.
• 18 of 94 mapped schools had flood water within 100 m on Oct 3.
• Two independent WorldPop runs (Mitch: 81,100; Asher: 80,760 under-5s in the tile) agree within 0.5%.
Check-first communes, Oct 3 (full list per pass in handoff/findings/commune_scores.json):
Rank
Commune
Under-5s near flood water
Flood water
1
Xã Vĩnh Châu (Tây Ninh)
488 of 538
74.5 km² (54%)
2
Phường Thường Lạc (Đồng Tháp)
477 of 696
12.9 km² (52%)
3
Xã Tuyên Bình (Tây Ninh)
716 of 815
93.7 km² (66%)
4
Phường Hồng Ngự (Đồng Tháp)
686 of 2,379
26.1 km² (41%)
5
Xã Vĩnh Hưng (Tây Ninh)
540 of 1,068
60.3 km² (65%)
The worst-hit communes sit along the Cambodian border in the Plain of Reeds; Cao Lãnh city, on higher ground behind dikes, ranks lowest. "Tây Ninh" is correct: the old Long An province merged into Tây Ninh in July 2025.
Score: 0.35 × homes near flood water + 0.25 × young children near flood water + 0.20 × share of area newly flooded + 0.20 × schools near flood water. The 1.3× "no childcare within 2 km" factor is off: OpenStreetMap lists too few preschools here (9 for 65 communes).
Sensitivity test (Oct 3, ~1 km squares): raising or lowering any weight by 50% keeps at least 3 of the top 5 and 7 of the top 10. Switching the schools factor off keeps 9 of the top 10, so patchy school data does not drive the ranking.
Limits: what we claim and what we don't
We claim that NISAR shows where flood water sat near young children's homes on each pass, through cloud, and that this ranks communes consistently. We do not claim to predict drownings, to say any place or route is safe, or to give final numbers.
• Provisional data. NISAR GCOV products are provisional, and only 3 passes (Jul 11, Aug 28, Oct 3) are processed so far.
• Stand-in water rule. Tonight's maps use a simple rule (HH below −13 dB). Our improved HH + HV rule is written but not yet run on the raw files, so numbers may shift.
• Narrow canals are missed. Pixels are about 30 m across. The canals and ditches right beside homes, where many children drown, are often narrower and do not show up.
• Water under dense plants is undercounted. The rule finds open water; flooded forest and tall rice (bright HH double bounce) are not yet counted.
• Rice vs flood is not separated. Telling a planted paddy from river flood needs passes 12 days apart; ours are about 7 weeks apart.
• Children are estimates. WorldPop under-5 counts are modelled from census and building data, not a headcount.
• Map gaps. OpenStreetMap is missing many rural schools and preschools, so the childcare-gap factor is switched off and school counts are low.
• One season, one tile. One 66 × 61 km tile in a low-flood year; no 2025 comparison yet.
• Uncorrected atmosphere. No atmospheric correction is applied; at L-band its effect on water detection is small, but we note it.
Receipts: where every number comes from
The same list is in handoff/findings/receipts.json for the app.
Number on screen
Value
Source
Date
Maturity
Confidence
Children under 5 near flood water
~28,100
NISAR GCOV water map × WorldPop under-5
Oct 3 2026 pass
Provisional
Medium
Children under 5 in tile
~81,100
WorldPop R2025A 2026, constrained 100 m (reproduced by Asher: 80,760)
2026
Modelled estimate
Medium
New flood water
1,335 km²
NISAR L2 GCOV, track 047 desc., frame 083
Jul 11 → Oct 3 2026
Provisional
Medium
Water share of tile
27% / 39% / 55%
NISAR L2 GCOV
Jul 11 / Aug 28 / Oct 3
Provisional
Medium
Schools near flood water
18 of 94
OpenStreetMap (ODbL), exported Nov 14 2026
Oct 3 2026 pass
Volunteer map, incomplete
Low
River level, Tân Châu
1.04 / 2.88 / 2.75 m
Mekong River Commission Data Portal, telemetry, read from chart viewer
~18:00 on pass dates
Telemetry
Medium
Tân Châu alarm level 1
3.5 m
VietnamPlus
Undated news report
News
Medium
Child drownings in Vietnam
Nearly 2,000 a year
Ministry of Health via Tuổi Trẻ
Jul 2025
Official
High
Đồng Tháp child drownings
29 in 2018, 79% under six
VOV
Jun 2019
News
Medium
Commune ranking
Top 10 per pass
science/commune_score.py; communes from Asher (post-2025 units)
Per pass
Provisional
Medium
Rice-calendar and El Niño context: VAN, Apr 2026.