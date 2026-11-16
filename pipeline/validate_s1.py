"""Check Ourea's NISAR water maps against an independent satellite: Sentinel-1 (C-band radar).

Question it answers: "How do you know the water map is right?"
We cannot visit the delta, so we compare our NISAR land/water map for each pass with a water map made
from a different radar satellite (ESA Sentinel-1) that flew over the same tile 1-2 days later.
Both are radar, so both see through cloud (Sentinel-2 optical was 76-95% cloud on these dates).

Run (any venv with numpy, rasterio, pillow):
    pip install pystac-client planetary-computer
    python pipeline/validate_s1.py --cache /some/scratch/dir/s1val
Writes handoff/findings/validation_accuracy.json. Downloads (about 30 MB per scene, already averaged to
the 2048x2048 tile grid) are cached in --cache, NOT in the repo. No login needed: Microsoft Planetary
Computer hands out anonymous read tokens for its public data.

Reference water map, per NISAR pass:
- Product: Planetary Computer "sentinel-1-rtc" (Sentinel-1 IW GRD, radiometrically terrain corrected
  gamma0, 10 m). Main reference = the descending Sentinel-1C pass the day after each NISAR pass (same
  satellite and orbit for all three dates, so the three reference maps are consistent). A second set
  (ascending pass, 2 days after, about the same evening time as NISAR) is scored too, as a robustness check.
- 10 m gamma0 (linear power) is averaged onto the NISAR grid (~30 m), converted to dB.
- Water = VV < -18 dB (a common fixed Sentinel-1 open-water threshold). The scene's own Otsu split is
  also reported. The same 3x3 majority clean-up the NISAR rule uses is applied.
- ESA WorldCover 2021 (10 m land cover) is used only to say WHERE the two maps disagree (trees, towns,
  cropland, open water).

NISAR codes: 0 water, 255 land, 128 no data (excluded), 200 rice flooded on purpose (scored twice:
as land and as water). Metrics treat water as the positive class and NISAR as the "prediction":
precision = share of NISAR water that Sentinel-1 also calls water; recall = share of Sentinel-1 water
that NISAR also finds. This is agreement between two sensors, not accuracy against ground truth.
"""
import argparse, datetime as dt, json, pathlib, warnings

import numpy as np
import rasterio
from PIL import Image
from rasterio.transform import from_bounds
from rasterio.warp import reproject, Resampling

warnings.filterwarnings("ignore")
ROOT = pathlib.Path(__file__).resolve().parents[1]
HANDOFF = ROOT / "handoff"
OUT_JSON = HANDOFF / "findings" / "validation_accuracy.json"

N = 2048
CRS = "EPSG:32648"
BOX = (105.30, 10.40, 105.90, 10.95)
VV_WATER_DB = -18.0
BASELINE = "2026-07-11"

# NISAR pass -> Sentinel-1 RTC items (chosen from a STAC search of 2026-06-25..10-20; each covers the whole tile)
REFERENCES = {
    "main (S1C descending, +1 day)": {
        "2026-07-11": "S1C_IW_GRDH_1SDV_20260712T224436_20260712T224501_008516_010DC0_rtc",
        "2026-08-28": "S1C_IW_GRDH_1SDV_20260829T224438_20260829T224503_009216_0124FB_rtc",
        "2026-10-03": "S1C_IW_GRDH_1SDV_20261004T224440_20261004T224505_009741_013669_rtc",
    },
    "check (S1C ascending, +2 days)": {
        "2026-07-11": "S1C_IW_GRDH_1SDV_20260713T111043_20260713T111112_008524_010E04_rtc",
        "2026-08-28": "S1C_IW_GRDH_1SDV_20260830T111046_20260830T111115_009224_01253E_rtc",
        "2026-10-03": "S1C_IW_GRDH_1SDV_20261005T111047_20261005T111116_009749_0136AE_rtc",
    },
}
WORLDCOVER = {10: "trees", 20: "shrub", 30: "grass", 40: "cropland", 50: "built-up", 60: "bare",
              80: "permanent water", 90: "herbaceous wetland", 95: "mangrove"}


def grid():
    meta = json.loads((HANDOFF / "terrain" / "meta.json").read_text())
    return from_bounds(*meta["bounds_m"], N, N)


def catalog():
    import planetary_computer as pc
    from pystac_client import Client
    return Client.open("https://planetarycomputer.microsoft.com/api/stac/v1", modifier=pc.sign_inplace)


def warp_to_grid(href, resampling, dtype="float32", nodata=np.nan):
    dst = np.full((N, N), nodata, dtype=dtype)
    with rasterio.open(href) as src:
        reproject(rasterio.band(src, 1), dst, src_nodata=src.nodata if src.nodata is not None else 0,
                  dst_transform=grid(), dst_crs=CRS, dst_nodata=nodata, resampling=resampling)
    return dst


def s1_db(cat, item_id, band, cache):
    """Sentinel-1 RTC gamma0 for one band, averaged (in linear power) onto the tile grid, in dB."""
    f = cache / f"{item_id}_{band}.npy"
    if not f.exists():
        item = cat.get_collection("sentinel-1-rtc").get_item(item_id)
        lin = warp_to_grid(item.assets[band].href, Resampling.average)
        lin[lin <= 0] = np.nan
        np.save(f, (10 * np.log10(lin)).astype("float32"))
    return np.load(f)


def worldcover(cat, cache):
    f = cache / "worldcover_2021.npy"
    if not f.exists():
        items = list(cat.search(collections=["esa-worldcover"], bbox=BOX).items())
        items = [i for i in items if "2021" in i.id] or items
        out = np.zeros((N, N), "uint8")
        for it in items:
            out = np.where(out == 0, warp_to_grid(it.assets["map"].href, Resampling.mode, "uint8", 0), out)
        np.save(f, out)
    return np.load(f)


def otsu(v, bins=256):
    v = v[np.isfinite(v)]
    hist, edges = np.histogram(v, bins=bins)
    c = (edges[:-1] + edges[1:]) / 2
    w0 = np.cumsum(hist); w1 = w0[-1] - w0
    m0 = np.cumsum(hist * c) / np.maximum(w0, 1)
    m1 = (np.sum(hist * c) - np.cumsum(hist * c)) / np.maximum(w1, 1)
    return float(c[np.argmax(w0 * w1 * (m0 - m1) ** 2)])


def majority3(mask):
    m = np.pad(mask.astype("uint8"), 1, mode="edge")
    s = sum(m[1 + dy:m.shape[0] - 1 + dy, 1 + dx:m.shape[1] - 1 + dx] for dy in (-1, 0, 1) for dx in (-1, 0, 1))
    return s >= 5


def scores(pred, ref, valid):
    """Binary agreement: pred = NISAR water, ref = Sentinel-1 water, over valid pixels."""
    p, r = pred[valid], ref[valid]
    tp = int(np.sum(p & r)); fp = int(np.sum(p & ~r)); fn = int(np.sum(~p & r)); tn = int(np.sum(~p & ~r))
    n = tp + fp + fn + tn
    po = (tp + tn) / n
    pe = ((tp + fp) * (tp + fn) + (fn + tn) * (fp + tn)) / n ** 2
    prec = tp / (tp + fp) if tp + fp else None
    rec = tp / (tp + fn) if tp + fn else None
    f1 = 2 * prec * rec / (prec + rec) if prec and rec else None
    r4 = lambda x: None if x is None else round(x, 4)
    return {"overall_agreement_pct": round(100 * po, 2), "water_precision": r4(prec), "water_recall": r4(rec),
            "f1": r4(f1), "cohen_kappa": r4((po - pe) / (1 - pe)),
            "nisar_water_pct": round(100 * (tp + fp) / n, 2), "s1_water_pct": round(100 * (tp + fn) / n, 2),
            "pixels": {"valid": n, "both_water": tp, "nisar_water_s1_land": fp, "nisar_land_s1_water": fn,
                       "both_land": tn}}


def disagreement_by_cover(pred, ref, valid, wc):
    rows = {}
    for code, name in WORLDCOVER.items():
        m = valid & (wc == code)
        n = int(m.sum())
        if n < 2000:
            continue
        rows[name] = {"share_of_tile_pct": round(100 * n / valid.sum(), 1),
                      "agreement_pct": round(100 * np.mean(pred[m] == ref[m]), 1),
                      "nisar_water_pct": round(100 * pred[m].mean(), 1),
                      "s1_water_pct": round(100 * ref[m].mean(), 1)}
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", required=True, help="scratch folder for downloads (not the repo)")
    a = ap.parse_args()
    cache = pathlib.Path(a.cache); cache.mkdir(parents=True, exist_ok=True)
    cat = catalog()
    wc = worldcover(cat, cache)
    codes = {d: np.array(Image.open(HANDOFF / "layers" / "landwater" / f"{d}.png")) for d in REFERENCES["main (S1C descending, +1 day)"]}
    result = {
        "what": "Agreement of Ourea NISAR land/water maps with an independent Sentinel-1 water map",
        "nisar_maps": "handoff/layers/landwater/<date>.png (NISAR L2 GCOV, stand-in rule HH < -13 dB)",
        "nisar_codes_found": {d: sorted(int(v) for v in np.unique(c)) for d, c in codes.items()},
        "method": {
            "reference_product": "Microsoft Planetary Computer sentinel-1-rtc (Sentinel-1 IW GRDH, RTC gamma0, 10 m)",
            "resampling": "10 m linear gamma0 averaged onto the 2048x2048 NISAR grid (EPSG:32648, ~32x30 m), then dB",
            "water_rule": f"VV < {VV_WATER_DB} dB, then 3x3 majority (same clean-up as the NISAR rule); "
                          "Otsu-threshold variant also reported",
            "excluded": "NISAR 128 (no data) and Sentinel-1 no-data pixels",
            "rice_200": "scored twice: as land and as water",
            "positive_class": "water; NISAR treated as prediction, Sentinel-1 as reference",
            "new_flood_water": f"water on the pass date AND land on {BASELINE} (each sensor vs its own baseline)",
            "land_cover_for_notes": "ESA WorldCover 2021 v200 (Planetary Computer esa-worldcover), mode-resampled",
            "script": "pipeline/validate_s1.py",
            "run_on": dt.date.today().isoformat(),
        },
        "caveats": [
            "This is agreement between two radars, not accuracy against ground truth. Neither map is truth.",
            "The NISAR maps contain only codes 0 and 255 (no 128 no-data, no 200 rice yet), so both rice "
            "variants give identical numbers and nothing is excluded.",
            "NISAR maps use the stand-in rule HH < -13 dB, which is lenient; the fixed S1 rule VV < -18 dB is "
            "strict. Most disagreement is NISAR water / S1 land on cropland (paddies): smooth wet or bare soil "
            "and shallow water among young rice. Who is right there is unknown without field data.",
            "C-band (S1, 5.5 cm) sees less water under rice than L-band (NISAR, 24 cm), so S1 under-counts "
            "water under crops; the low precision is partly an S1 limitation, not only a NISAR error.",
            "Sentinel-1 is 1 day (main) or 2 days (check) after NISAR; water can rise or drain in between. "
            "The two S1 passes themselves agree only 92-98%, and their August water share differs (18% vs 11%).",
            "Permanent rivers/canals: S1 calls only ~45-49% of WorldCover 'permanent water' pixels water "
            "(wind-roughened river surface, mixed 30 m pixels on narrow canals); NISAR ~74%.",
            "The Otsu split on each S1 scene is reported too; on the July scene Otsu (-9.9 dB) is not a "
            "water/land split (the scene has almost no open water) and should be ignored.",
            "Sentinel-2 optical was not used: 76-95% cloud on these dates (handoff/layers/optical/optical.json).",
        ],
        "references": {},
    }
    for ref_name, ref_items in REFERENCES.items():
        s1 = {}
        block = {"items": {}, "per_date": {}}
        for d, item_id in ref_items.items():
            vv = s1_db(cat, item_id, "vv", cache)
            s1_date = dt.datetime.strptime(item_id.split("_")[4][:8], "%Y%m%d").date()  # UTC acquisition date
            t_otsu = otsu(vv)
            s1[d] = {"vv": vv, "water": majority3(vv < VV_WATER_DB), "water_otsu": majority3(vv < t_otsu),
                     "ok": np.isfinite(vv)}
            block["items"][d] = {"id": item_id, "s1_date": s1_date.isoformat(),
                                 "days_after_nisar": (s1_date - dt.date.fromisoformat(d)).days,
                                 "otsu_vv_threshold_db": round(t_otsu, 2)}
        for d, c in codes.items():
            valid = (c != 128) & s1[d]["ok"]
            per = {}
            for rice_as in ("land", "water"):
                nis = (c == 0) | ((c == 200) & (rice_as == "water"))
                per[f"rice200_as_{rice_as}"] = scores(nis, s1[d]["water"], valid)
            per["rice200_pixels"] = int(np.sum(c == 200))
            per["otsu_variant"] = scores(c == 0, s1[d]["water_otsu"], valid)
            per["by_land_cover"] = disagreement_by_cover(c == 0, s1[d]["water"], valid, wc)
            if d != BASELINE:
                b = codes[BASELINE]
                v2 = valid & (b != 128) & s1[BASELINE]["ok"]
                nis_new = (c == 0) & (b == 255)
                s1_new = s1[d]["water"] & ~s1[BASELINE]["water"]
                per["new_flood_water"] = scores(nis_new, s1_new, v2)
                per["new_flood_water"]["nisar_new_km2"] = round(float(nis_new[v2].sum()) * 32.1 * 29.76 / 1e6, 1)
                per["new_flood_water"]["s1_new_km2"] = round(float(s1_new[v2].sum()) * 32.1 * 29.76 / 1e6, 1)
                # Is NISAR's new flood water wet at all in Sentinel-1 (regardless of the baseline)?
                per["new_flood_water"]["share_of_nisar_new_water_that_s1_calls_water"] = round(
                    float(np.mean(s1[d]["water"][nis_new & v2])), 4)
            block["per_date"][d] = per
        result["references"][ref_name] = block
    # agreement between the two Sentinel-1 sets themselves (how much do two C-band looks 1 day apart differ?)
    names = list(REFERENCES)
    result["s1_desc_vs_s1_asc_agreement_pct"] = {}
    for d in codes:
        A = majority3(s1_db(cat, REFERENCES[names[0]][d], "vv", cache) < VV_WATER_DB)
        Bv = s1_db(cat, REFERENCES[names[1]][d], "vv", cache)
        B = majority3(Bv < VV_WATER_DB)
        ok = np.isfinite(Bv) & np.isfinite(s1_db(cat, REFERENCES[names[0]][d], "vv", cache))
        result["s1_desc_vs_s1_asc_agreement_pct"][d] = scores(A, B, ok)["overall_agreement_pct"]
    OUT_JSON.write_text(json.dumps(result, indent=2))
    for d, per in result["references"][names[0]]["per_date"].items():
        s = per["rice200_as_land"]
        print(d, {k: s[k] for k in ("overall_agreement_pct", "water_precision", "water_recall", "f1", "cohen_kappa",
                                    "nisar_water_pct", "s1_water_pct")})
    print("wrote", OUT_JSON)


if __name__ == "__main__":
    main()
