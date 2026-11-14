# Handoff contract (locked; announce any change in the group chat)

**Tile (agreed Nov 14):** 105.30–105.90°E, 10.40–10.95°N · upper Mekong floodplain, Đồng Tháp side · inside NISAR frame 083 (track 047 desc.) · grid UTM 48N (EPSG:32648), north up · heightmap 2049² · layers 2048² (~30 m/px). Top edge may clip a sliver of Cambodia; kept on purpose.

```
handoff/
├── terrain/heightmap.r16      16-bit RAW, little-endian, 2049×2049; value = (elev−min)/(max−min)×65535
├── terrain/meta.json          {crs:"EPSG:32648", bounds, pixel_m, size, min_m, max_m, notes}
├── layers/landwater/DATE.png  8-bit grey 2048²: 255 land · 0 water · 128 no data · 200 rice flooded on purpose
├── layers/backscatter/DATE.png 8-bit grey 2048²: HV γ⁰ −25..−5 dB → 0..255
├── layers/optical/DATE.png    8-bit RGB 2048²: Sentinel-2 nearest each pass (cloud % in dates.json)
├── dates.json                 [{date, landwater, backscatter, optical, cloud_pct}] oldest first
└── findings/
    ├── assets.json            [{id, name, type: hamlet|school|childcare, lon, lat, u, v}]
    ├── scores.json            [{place_id, pass, score, rank, rank_change, factors:{water_near_homes, young_children, new_water, schools_routes}, gap_factor, reasons[], confidence}]
    ├── routes.json            [{hamlet_id, school_id, pass, usual:{coords, wet_pct}, alt:{coords, wet_pct, source:"google"|"ors"}, label:"least water on the DATE pass"}]
    ├── receipts.json          {number_id: {source, product, date, maturity, confidence}}
    ├── notes.json             [{from, to, text}]
    └── headline.json          {new_water_km2, pass, baseline, homes_near_water, children_under5_near_water, sources[]}
```
Rules: row 0 = north, col 0 = west · one grid (UTM 48N) for every layer · ISO dates (YYYY-MM-DD) · anything simulated is flagged in meta.json and labeled in the app.
