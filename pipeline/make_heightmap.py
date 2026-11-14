"""Terrain for Unity: Copernicus GLO-30 DEM -> handoff/terrain/heightmap.r16 + meta.json. Owner: Tina.

Run:
    pip install rasterio numpy pillow
    python pipeline/make_heightmap.py            # downloads the one tile it needs (~40 MB)
    python pipeline/make_heightmap.py --dem path/to/local.tif   # if you downloaded it yourself

The tile (105.30-105.90E, 10.40-10.95N) sits inside one Copernicus 1x1 degree tile: N10 E105.
Output follows handoff/CONTRACT.md: UTM 48N, north up, 2049x2049, 16-bit little-endian.
"""
import argparse, json, pathlib
import numpy as np
import rasterio
from rasterio.warp import transform_bounds, reproject, Resampling
from rasterio.transform import from_bounds

BOX = (105.30, 10.40, 105.90, 10.95)  # west, south, east, north (degrees)
DST_CRS = "EPSG:32648"
SIZE = 2049
TILE_URL = ("https://copernicus-dem-30m.s3.amazonaws.com/"
            "Copernicus_DSM_COG_10_N10_00_E105_00_DEM/Copernicus_DSM_COG_10_N10_00_E105_00_DEM.tif")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dem", default=None, help="local DEM GeoTIFF (default: read the tile straight from AWS)")
    ap.add_argument("--out", default=str(pathlib.Path(__file__).resolve().parents[1] / "handoff" / "terrain"))
    a = ap.parse_args()
    out = pathlib.Path(a.out); out.mkdir(parents=True, exist_ok=True)
    src_path = a.dem or "/vsicurl/" + TILE_URL

    left, bottom, right, top = transform_bounds("EPSG:4326", DST_CRS, *BOX, densify_pts=21)
    dst_tf = from_bounds(left, bottom, right, top, SIZE, SIZE)
    dem = np.full((SIZE, SIZE), np.nan, dtype="float32")
    with rasterio.open(src_path) as src:
        reproject(source=rasterio.band(src, 1), destination=dem, src_transform=src.transform, src_crs=src.crs,
                  dst_transform=dst_tf, dst_crs=DST_CRS, resampling=Resampling.bilinear,
                  src_nodata=src.nodata, dst_nodata=np.nan)

    valid = np.isfinite(dem)
    if not valid.any():
        raise SystemExit("No elevation values in the box. Check the DEM file covers 105.3-105.9E, 10.4-10.95N.")
    dem[~valid] = np.nanmin(dem)
    lo, hi = float(np.percentile(dem, 0.5)), float(np.percentile(dem, 99.5))  # trim outliers (towers, trees)
    lo = min(lo, 0.0); hi = max(hi, lo + 1.0)
    norm = np.clip((dem - lo) / (hi - lo), 0, 1)
    (norm * 65535).round().astype("<u2").tofile(out / "heightmap.r16")

    meta = {"crs": DST_CRS, "bounds_m": [left, bottom, right, top], "bounds_deg": list(BOX),
            "size": SIZE, "pixel_m": round((right - left) / (SIZE - 1), 2), "pixel_m_y": round((top - bottom) / (SIZE - 1), 2), "min_m": lo, "max_m": hi,
            "source": "Copernicus GLO-30 DEM (surface model: includes buildings and trees)",
            "notes": "value = (elev - min_m)/(max_m - min_m) * 65535. Delta is very flat: exaggerate height 30-50x in Unity."}
    (out / "meta.json").write_text(json.dumps(meta, indent=2))
    try:
        from PIL import Image
        Image.fromarray((norm * 255).astype("uint8")).save(out / "heightmap_preview.png")
    except ImportError:
        pass
    print(f"Wrote {out/'heightmap.r16'} ({SIZE}x{SIZE}), {lo:.1f} to {hi:.1f} m, {meta['pixel_m']} m/px")
    print("Unity: Terrain > Import Raw, Depth 16 bit, Byte order Windows, resolution 2049. "
          f"Terrain size {right-left:.0f} x {top-bottom:.0f} m.")

if __name__ == "__main__":
    main()
