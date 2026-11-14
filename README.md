# Ourea · Every pass, a plain answer

**NASA Space Apps Challenge 2026 · Dancing with the SARs · Team Ourea (NYC)**

Ourea turns every NISAR radar pass over Vietnam's Mekong Delta into a plain answer for the people who keep kids safe in flood season: **where is flood water now close to homes, schools and children's routes, and where should we check first?**

- **Problem:** the Mekong Delta has Vietnam's highest child drowning rate, and the flood season comes under monsoon clouds that blind normal satellites.
- **Solution:** NISAR L-band radar (free, through clouds, every 4–6 days over the delta) → water map → a ranked top-10 "check first" list of hamlets, plus school routes near water.
- **Users:** provincial education and child-protection offices, commune officials, drowning-prevention programs.

> Status: hackathon build (Nov 14–15, 2026). NISAR data is PROVISIONAL. We never call a route "safe"; we say "least water on the [date] pass".

## Repo layout
| Folder | Owner | What lives here |
|---|---|---|
| `pipeline/` | Eric | Download NISAR GCOV, align layers, export the handoff package (`run_pipeline.py`) |
| `science/` | Mitch | Water classifier, rice-vs-flood rule, check-first score, sensitivity test |
| `unity/` | Kason (+ Tina) | Unity project (WebGL build) |
| `web/` | Asher | Web app, Cloudflare Worker, MCP server, Google Maps / Routes, Twilio |
| `docs/` | Christopher (+ all) | Slides, story, one-pager, AI-use log, data sources |
| `handoff/` | Data ↔ Unity | File contract (see `handoff/CONTRACT.md`) |

## Quick start
```bash
git clone <this repo>
cd pipeline && python -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt
# Earthdata login goes in your own ~/.netrc. Never commit credentials.
python run_pipeline.py --track 047 --frame 083 --start 2026-06-17 --end 2026-10-15
```

## Data
- NISAR L2 GCOV (HH, HV), track 047 descending, frame 083 (plus 083/155 ascending, frame 007), via ASF
- Copernicus GLO-30 elevation · Sentinel-2 optical (compare view) · Sentinel-1 (2025 comparison)
- WorldPop under-5 population · OpenStreetMap homes, schools, roads

## Team
Tina (lead) · Kason (Unity) · Eric (data) · Mitch (science) · Asher (web) · Christopher (slides & story)

## License
MIT
