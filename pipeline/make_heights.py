"""Real heights for the Unity scene: bare-earth ground and building heights. Owner: Kason.

Run:
    pip install numpy rasterio pillow
    python pipeline/make_heights.py --fabdem N10E105_FABDEM_V1-2.tif \
        --ghsl-height GHS_BUILT_H_ANBH_E2018_GLOBE_R2023A_54009_100_V1_0_R8_C29.tif \
        --ghsl-built GHS_BUILT_S_E2020_GLOBE_R2023A_54009_100_V1_0_R8_C29.tif

Inputs (download once, not committed):
  FABDEM V1-2 (Univ. of Bristol): Copernicus GLO-30 with trees and buildings removed, so the
    ground is bare earth. https://data.bris.ac.uk/data/dataset/s5hqmjcdj8yo2ibzi9b4ew3sn
    (tile N10E105 inside N10E100-N20E110_FABDEM_V1-2.zip). CC BY-NC-SA 4.0.
  GHSL (EU JRC): GHS-BUILT-H ANBH 2018 (average net building height, m) and GHS-BUILT-S 2020
    (built-up surface, m² per cell), 100 m, Mollweide tile R8_C29.
    https://human-settlement.emergency.copernicus.eu/download.php  CC BY 4.0.

Outputs, on the handoff grid (EPSG:32648, bounds from handoff/terrain/meta.json, row 0 = north):
  handoff/terrain/ground.r16     FABDEM bare earth, 16-bit LE, same size as heightmap.r16;
                                 value = (elev - min_m)/(max_m - min_m) * 65535 (ground.json)
  handoff/terrain/ground.json    {min_m, max_m, size, source}
  handoff/buildings/height.png   8-bit, 100 m cells: average building height, 0.25 m per step
  handoff/buildings/built.png    8-bit, 100 m cells: share of the cell covered by buildings (0..255)
  handoff/buildings/buildings.json  grid, scales, sources
  handoff/trees/height.png       8-bit, 100 m cells: mean height of the tree canopy, 0.25 m per step
                                 (only 10 m pixels >= 10 m: the model reads 3-8 m over rice paddies)
  handoff/trees/cover.png        8-bit, 100 m cells: share of the cell with canopy >= 10 m (0..255)
  handoff/trees/trees.json       grid, scales, source

  --canopy: ETH Global Canopy Height 2020, 10 m (Lang et al. 2023, CC BY 4.0), tile N09E105
    https://langnico.github.io/globalcanopyheight/
"""
import argparse, json, pathlib
import numpy as np
import rasterio
from rasterio.warp import reproject, Resampling
from rasterio.transform import from_bounds
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
DST_CRS = "EPSG:32648"
TREE_M = 10.0   # canopy height that counts as a tree (ETH reads 3-8 m over rice paddies)


def warp(src_path, bounds, w, h, resampling, nodata_out=np.nan):
    left, bottom, right, top = bounds
    dst = np.full((h, w), nodata_out, np.float32)
    with rasterio.open(src_path) as src:
        reproject(source=rasterio.band(src, 1), destination=dst,
                  src_nodata=src.nodata, dst_transform=from_bounds(left, bottom, right, top, w, h),
                  dst_crs=DST_CRS, dst_nodata=nodata_out, resampling=resampling)
    return dst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fabdem")
    ap.add_argument("--ghsl-height")
    ap.add_argument("--ghsl-built")
    ap.add_argument("--canopy")
    a = ap.parse_args()
    meta = json.loads((ROOT / "handoff/terrain/meta.json").read_text())
    bounds, size = meta["bounds_m"], meta["size"]

    if a.fabdem:
        g = warp(a.fabdem, bounds, size, size, Resampling.bilinear)
        if np.isnan(g).any():   # tile edge: fill the few gaps from neighbours
            g = np.where(np.isnan(g), np.nanmedian(g), g)
        lo, hi = float(np.percentile(g, 0.05)), float(np.percentile(g, 99.95))
        g = np.clip(g, lo, hi)
        q = np.round((g - lo) / (hi - lo) * 65535).astype("<u2")
        (ROOT / "handoff/terrain/ground.r16").write_bytes(q.tobytes())
        (ROOT / "handoff/terrain/ground.json").write_text(json.dumps({
            "min_m": round(lo, 3), "max_m": round(hi, 3), "size": size, "crs": DST_CRS, "bounds_m": bounds,
            "source": "FABDEM V1-2 (Hawker et al. 2022, Univ. of Bristol), bare earth, CC BY-NC-SA 4.0",
            "notes": "value = (elev - min_m)/(max_m - min_m) * 65535; 0.05/99.95 percentile clipped"}, indent=2) + "\n")
        print(f"ground: {lo:.2f}..{hi:.2f} m")

    if a.ghsl_height and a.ghsl_built:
        left, bottom, right, top = bounds
        w, h = round((right - left) / 100), round((top - bottom) / 100)
        hgt = np.nan_to_num(warp(a.ghsl_height, bounds, w, h, Resampling.average), nan=0.0)
        blt = np.nan_to_num(warp(a.ghsl_built, bounds, w, h, Resampling.average), nan=0.0)
        frac = np.clip(blt / 10000.0, 0, 1)          # m² of buildings in a 100 m x 100 m cell
        hgt = np.where(frac > 0.01, hgt, 0.0)
        out = ROOT / "handoff/buildings"
        out.mkdir(parents=True, exist_ok=True)
        Image.fromarray(np.clip(np.round(hgt * 4), 0, 255).astype(np.uint8)).save(out / "height.png")
        Image.fromarray(np.clip(np.round(frac * 255), 0, 255).astype(np.uint8)).save(out / "built.png")
        built = frac > 0.05
        (out / "buildings.json").write_text(json.dumps({
            "crs": DST_CRS, "bounds_m": bounds, "width": w, "height": h, "cell_m": 100,
            "height_png": "value / 4 = average building height in metres",
            "built_png": "value / 255 = share of the 100 m cell covered by buildings",
            "cells_with_buildings": int(built.sum()),
            "mean_height_m_where_built": round(float(hgt[built].mean()), 2) if built.any() else 0,
            "max_height_m": round(float(hgt.max()), 2),
            "sources": ["GHS-BUILT-H ANBH 2018 R2023A, EU JRC (CC BY 4.0)",
                        "GHS-BUILT-S 2020 R2023A, EU JRC (CC BY 4.0)"]}, indent=2) + "\n")
        print(f"buildings: {w}x{h} cells, {int(built.sum())} built, mean {hgt[built].mean():.1f} m, max {hgt.max():.1f} m")


    if a.canopy:
        left, bottom, right, top = bounds
        w, h = round((right - left) / 100), round((top - bottom) / 100)
        # 10 m pixels -> 100 m cells: 10x finer grid first, then block statistics
        fine = warp(a.canopy, bounds, w * 10, h * 10, Resampling.nearest)
        fine = np.where((fine >= 0) & (fine < 100), fine, 0.0)   # 255 = no data
        blocks = fine[: h * 10, : w * 10].reshape(h, 10, w, 10)
        tall = blocks >= TREE_M
        cover = tall.mean(axis=(1, 3))
        hsum = np.where(tall, blocks, 0).sum(axis=(1, 3))
        height = np.where(tall.any(axis=(1, 3)), hsum / np.maximum(tall.sum(axis=(1, 3)), 1), 0.0)
        out = ROOT / "handoff/trees"
        out.mkdir(parents=True, exist_ok=True)
        Image.fromarray(np.clip(np.round(height * 4), 0, 255).astype(np.uint8)).save(out / "height.png")
        Image.fromarray(np.clip(np.round(cover * 255), 0, 255).astype(np.uint8)).save(out / "cover.png")
        treed = cover > 0.15
        (out / "trees.json").write_text(json.dumps({
            "crs": DST_CRS, "bounds_m": bounds, "width": w, "height": h, "cell_m": 100,
            "height_png": "value / 4 = mean canopy height (m) of the 10 m pixels >= 10 m",
            "cover_png": "value / 255 = share of the cell with canopy >= 10 m",
            "threshold_note": "the model reads 3-8 m over rice paddies (known bias over low crops), so only pixels >= 10 m count as trees",
            "cells_with_trees": int(treed.sum()),
            "mean_height_m_where_treed": round(float(height[treed].mean()), 2) if treed.any() else 0,
            "source": "ETH Global Canopy Height 2020, 10 m (Lang et al. 2023, Nature Ecology & Evolution), CC BY 4.0"}, indent=2) + "\n")
        print(f"trees: {int(treed.sum())} cells with >15% canopy, mean {height[treed].mean():.1f} m")


if __name__ == "__main__":
    main()
