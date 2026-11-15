# Sources (every number on screen must trace here)
- NISAR GCOV via ASF Search: https://search.asf.alaska.edu/
- Child drowning, Mekong Delta: https://vietnamnews.vn/society/520845/mekong-delta-takes-action-to-prevent-drowning-incidents.html
- Flood-season childcare points: https://vov.vn/xa-hoi/dbscl-to-chuc-giu-tre-ban-tru-trong-mua-lu
- 2026 flood-season drowning warning: https://baovanhoa.vn/doi-song/canh-bao-nguy-co-duoi-nuoc-tre-em-trong-mua-nuoc-noi-263637.html
- 2026 low flood year: https://fishbio.com/news/the-mekong-delta-is-starving-for-floods/
- Google Routes API route modifiers: https://developers.google.com/maps/documentation/routes/route-modifiers

## Unity app: what is real, derived or modeled
| On screen | Status | Source |
|---|---|---|
| Water on each pass (timeline, map colour, "+1,150 km²" new water) | REAL, derived | NISAR L2 GCOV, track 047 frame 083, 11 Jul / 28 Aug / 3 Oct 2026 (provisional), via ASF: https://search.asf.alaska.edu/ — water rule is a stand-in (HH < −13 dB, Otsu), see `handoff/layers/nisar.json` |
| "NISAR saw 55% of the tile as water" | REAL, derived | `water_pct` in `handoff/layers/nisar.json` (same masks) |
| Radar view (dark water, bright fields) | REAL | NISAR HV γ⁰ backscatter, −25…−5 dB mapped to 0…255 (`handoff/layers/backscatter/`) |
| Terrain shape in the 3D view | REAL elevation | Copernicus GLO-30 DEM (surface model: includes trees and buildings), `handoff/terrain/heightmap.r16`: https://spacedata.copernicus.eu/collections/copernicus-digital-elevation-model |
| Flood depth, "km² / % of the map below it", ground under the building blocks | REAL elevation, derived area | FABDEM V1-2 bare earth (Copernicus GLO-30 with trees and buildings removed; Hawker et al. 2022, Univ. of Bristol, CC BY-NC-SA 4.0): https://data.bris.ac.uk/data/dataset/s5hqmjcdj8yo2ibzi9b4ew3sn — tile N10E105, reprojected by `pipeline/make_heights.py` into `handoff/terrain/ground.r16` |
| Raise the water / "Match NISAR: 2.6 m" | MODELED | Bathtub fill: a flat water level over the FABDEM bare-earth ground. Ignores dikes, canals, flow and tides. Not a forecast. Labelled MODELED in the app. |
| Ground colour | REAL imagery | Sentinel-2 cloudless 2024 by EOX IT Services, https://s2maps.eu (CC BY-NC-SA 4.0), contains modified Copernicus Sentinel data 2024 |
| Building blocks (height and footprint per 100 m cell), "% of built-up land below it" | REAL, derived | GHS-BUILT-H ANBH 2018 + GHS-BUILT-S 2020 R2023A, EU JRC, 100 m (CC BY 4.0): https://human-settlement.emergency.copernicus.eu/download.php — reprojected by `pipeline/make_heights.py` into `handoff/buildings/`. Heights exaggerated in the 3D view, same factor as the ground. |
| Live commune ranking (Top 10 → Communes · live) | REAL when a pass is imported | Ourea API (Asher, Cloudflare Worker): https://ourea-api.team-ourea.workers.dev — shows "no pass imported yet" until then |
| Hamlets, check-first ranks, beams | SIMULATED placeholder | Built by `OureaSceneBuilder` until `handoff/findings/` (Mitch, Asher) exists. Labelled PLACEHOLDER RANKING in the app. |
