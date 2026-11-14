"""Optical layers for the Compare wipe: Sentinel-2 true color nearest each NISAR pass. Owner: Tina.

Run (inside the same .venv as make_heightmap.py):
    pip install pystac-client rasterio numpy pillow
    python pipeline/make_optical.py

For each NISAR pass date it finds the Sentinel-2 L2A image closest in time (within +/-6 days) over the tile,
cuts it to the tile, puts it on the UTM 48N grid at 2048x2048, and writes handoff/layers/optical/DATE.png.
It deliberately does NOT pick the clearest image: the point of the wipe is to show what a camera satellite
really saw near the pass date. Cloud % per image goes into handoff/layers/optical/optical.json.
Data: Element 84 Earth Search (AWS open data, no login needed).
"""
import json, pathlib, datetime as dt
import numpy as np
import rasterio
from rasterio.warp import transform_bounds, reproject, Resampling
from rasterio.transform import from_bounds
from pystac_client import Client

BOX = (105.30, 10.40, 105.90, 10.95)
DST_CRS, SIZE = "EPSG:32648", 2048
PASSES = ["2026-06-17", "2026-06-29", "2026-07-11", "2026-07-23", "2026-08-16",
          "2026-08-28", "2026-09-09", "2026-09-21", "2026-10-03", "2026-10-15"]
OUT = pathlib.Path(__file__).resolve().parents[1] / "handoff" / "layers" / "optical"

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    left, bottom, right, top = transform_bounds("EPSG:4326", DST_CRS, *BOX, densify_pts=21)
    tf = from_bounds(left, bottom, right, top, SIZE, SIZE)
    cat = Client.open("https://earth-search.aws.element84.com/v1")
    log = []
    for p in PASSES:
        d = dt.date.fromisoformat(p)
        items = list(cat.search(collections=["sentinel-2-l2a"], bbox=BOX,
                                datetime=f"{d - dt.timedelta(days=6)}/{d + dt.timedelta(days=6)}").items())
        if not items:
            print(f"{p}: no Sentinel-2 image within 6 days"); log.append({"pass": p, "image": None}); continue
        # closest in time; the tile can span two Sentinel-2 granules, so mosaic every granule from that day
        best_day = min({i.datetime.date() for i in items}, key=lambda x: abs((x - d).days))
        day_items = [i for i in items if i.datetime.date() == best_day]
        rgb = np.zeros((3, SIZE, SIZE), dtype="float32")
        for it in day_items:
            with rasterio.open(it.assets["visual"].href) as src:
                for b in range(3):
                    tmp = np.zeros((SIZE, SIZE), dtype="float32")
                    reproject(rasterio.band(src, b + 1), tmp, src_transform=src.transform, src_crs=src.crs,
                              dst_transform=tf, dst_crs=DST_CRS, resampling=Resampling.bilinear, src_nodata=0, dst_nodata=0)
                    rgb[b] = np.where(tmp > 0, tmp, rgb[b])
        cloud = round(float(np.mean([i.properties.get("eo:cloud_cover", 0) for i in day_items])), 1)
        from PIL import Image
        Image.fromarray(np.clip(rgb.transpose(1, 2, 0), 0, 255).astype("uint8")).save(OUT / f"{p}.png")
        log.append({"pass": p, "image_date": str(best_day), "days_off": (best_day - d).days, "cloud_pct": cloud,
                    "granules": [i.id for i in day_items]})
        print(f"{p}: Sentinel-2 {best_day} ({(best_day - d).days:+d} days), {cloud}% cloud")
    (OUT / "optical.json").write_text(json.dumps(log, indent=2))

if __name__ == "__main__":
    main()
