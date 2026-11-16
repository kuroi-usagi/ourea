"""Data for the routes page (web/public/unity/routes/). Owner: Kason.

Run:
    pip install numpy rasterio pillow
    python pipeline/make_routes_data.py

Writes, next to the page:
  flood_<DATE>.png   new flood water for each pass (water on that pass that was land on the
                     baseline pass), reprojected to lat/lon so it lines up on Google Maps;
                     transparent elsewhere. Same rule as the app (Shaders/OureaWater.shader).
  data.json          bounds of the overlays, the passes, and for each pass its top-10 hamlets
                     with their 2 nearest schools / day-care points (straight-line), from
                     handoff/findings. The routes themselves are asked of Google in the browser
                     and never stored (Google's terms).
"""
import json, math, pathlib
import numpy as np
import rasterio
from rasterio.transform import from_bounds
from rasterio.warp import reproject, Resampling
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "web" / "public" / "unity" / "routes"
WATER = 64   # landwater PNG: 0 = water, 255 = land, 128 = no data, 200 = rice flooded on purpose


def km(a, b):
    dy = (a["lat"] - b["lat"]) * 111.32
    dx = (a["lon"] - b["lon"]) * 111.32 * math.cos(math.radians((a["lat"] + b["lat"]) / 2))
    return math.hypot(dx, dy)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    meta = json.loads((ROOT / "handoff/terrain/meta.json").read_text())
    lon0, lat0, lon1, lat1 = meta["bounds_deg"]
    left, bottom, right, top = meta["bounds_m"]
    dates = sorted(p.stem for p in (ROOT / "handoff/layers/landwater").glob("*.png"))

    def warp(path):
        src = np.asarray(Image.open(path).convert("L"))
        h, w = src.shape
        W, H = 2048, round(2048 * (lat1 - lat0) / (lon1 - lon0) / math.cos(math.radians((lat0 + lat1) / 2)))
        dst = np.full((H, W), 128, np.uint8)
        reproject(src, dst, src_transform=from_bounds(left, bottom, right, top, w, h), src_crs="EPSG:32648",
                  dst_transform=from_bounds(lon0, lat0, lon1, lat1, W, H), dst_crs="EPSG:4326",
                  resampling=Resampling.nearest, src_nodata=None, dst_nodata=128)
        return dst

    base = warp(ROOT / "handoff/layers/landwater" / f"{dates[0]}.png") < WATER
    for d in dates:
        now = warp(ROOT / "handoff/layers/landwater" / f"{d}.png") < WATER
        flood = now & ~base
        rgba = np.zeros(flood.shape + (4,), np.uint8)
        rgba[flood] = (64, 170, 255, 175)   # new flood water: a clear blue over the satellite photo
        Image.fromarray(rgba, "RGBA").save(OUT / f"flood_{d}.png", optimize=True)
        print(d, f"{flood.mean() * 100:.1f}% of the tile is new flood water")

    assets = json.loads((ROOT / "handoff/findings/assets.json").read_text())
    byid = {a["id"]: a for a in assets}
    dests = [a for a in assets if a["type"] in ("school", "childcare")]
    scores = json.loads((ROOT / "handoff/findings/scores.json").read_text())
    passes = []
    for d in dates:
        top = sorted((s for s in scores if s["pass"] == d and s["rank"] <= 10 and s["score"] > 0), key=lambda s: s["rank"])
        hamlets = []
        for s in top:
            h = byid.get(s["place_id"])
            if not h: continue
            near = sorted(dests, key=lambda x: km(h, x))[:2]
            hamlets.append({"id": h["id"], "name": h["name"], "rank": s["rank"], "lat": h["lat"], "lon": h["lon"],
                            "under5": h.get("under5_estimate"), "reasons": s.get("reasons", []),
                            "to": [{"id": x["id"], "name": x["name"], "type": x["type"], "lat": x["lat"], "lon": x["lon"],
                                    "km": round(km(h, x), 2)} for x in near]})
        passes.append({"date": d, "overlay": f"flood_{d}.png", "hamlets": hamlets})
    (OUT / "data.json").write_text(json.dumps({
        "bounds": {"west": lon0, "south": lat0, "east": lon1, "north": lat1},
        "passes": passes,
        "note": "Overlays: new flood water (water on the pass that was land on " + dates[0] + "), NISAR L2 GCOV, provisional. "
                "Hamlets: top 10 per pass from handoff/findings/scores.json; destinations: nearest 2 schools/day-care points (straight line)."},
        ensure_ascii=False, indent=1) + "\n")
    print(sum(len(p["hamlets"]) for p in passes), "hamlet entries written")


if __name__ == "__main__":
    main()
