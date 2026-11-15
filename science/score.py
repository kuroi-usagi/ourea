"""Ourea check-first score (science/score.py). Owner: Mitch.

Ranks ~1 km squares where young children live, per NISAR pass. Squares are named after the nearest
OpenStreetMap place, because OSM maps very few hamlets here (2 of 146 places in the tile).

Score 0-100 = 100 x (0.35 water_near_homes + 0.25 young_children + 0.20 new_water + 0.20 schools_routes)
              x 1.3 if no preschool/kindergarten within 2 km  (capped at 100)
  "Flood water" = water on this pass that was land on the baseline (driest) pass. Permanent rivers and
  canals are excluded on purpose: in the delta nearly every home is beside a canal, so "any water near
  homes" is ~100% everywhere and cannot rank anything.
  water_near_homes  share of the square's populated cells with flood water within ~100 m
  young_children    under-5 children living within ~100 m of flood water, sqrt-scaled (50 = 0.5, 200+ = 1.0)
  new_water         share of the square that is flood water, scaled so 60% = 1.0
  schools_routes    schools with flood water within ~100 m, 1 school = 0.5, 2+ = 1.0
                    (OSM school coverage is patchy: see the sensitivity test with this factor off)

Usage: python score.py HANDOFF_DIR WORLDPOP_UNDER5_TIF SCHOOLS_GEOJSON PLACES_GEOJSON OUT_DIR [VN_MASK_TIF]
"""
import json, sys, math, pathlib, datetime as dt
import numpy as np
import rasterio
from rasterio.warp import reproject, Resampling, transform as warp_transform
from rasterio.transform import from_bounds
from PIL import Image

WEIGHTS = {"water_near_homes": 0.35, "young_children": 0.25, "new_water": 0.20, "schools_routes": 0.20}
GAP_FACTOR, GAP_KM = 1.3, 2.0
USE_GAP = False   # OSM has too few preschools here (9 for 65 communes); switch on when the official childcare list arrives
CELL = 32                  # 32 px of ~31 m = ~1 km squares
NEAR_PX = 3                # ~100 m on the ~31 m grid
MIN_CHILDREN = 20          # only rank squares with at least this many under-5s
KIDS_FULL = 200.0
NEW_FULL = 0.6
WATER, LAND, NODATA, RICE = 0, 255, 128, 200


def dilate(mask, r):
    """Square dilation by r pixels (numpy only)."""
    out = mask.copy()
    p = np.pad(mask, r)
    H, W = mask.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= p[r + dy:r + dy + H, r + dx:r + dx + W]
    return out


def block_sum(a, k=CELL):
    H, W = a.shape
    return a[:H // k * k, :W // k * k].reshape(H // k, k, W // k, k).sum(axis=(1, 3))


def load_grid(handoff):
    meta = json.loads((handoff / "terrain" / "meta.json").read_text())
    N = 2048
    tf = from_bounds(*meta["bounds_m"], N, N)
    return meta, tf, N


def kids_on_grid(tif, crs, tf, N):
    """WorldPop under-5 counts resampled to the tile grid, preserving totals."""
    with rasterio.open(tif) as src:
        a = src.read(1).astype("float32"); a[a < 0] = 0
        dst = np.zeros((N, N), "float32")
        reproject(a, dst, src_transform=src.transform, src_crs=src.crs, dst_transform=tf, dst_crs=crs,
                  resampling=Resampling.average)
        src_px_area = abs(src.res[0] * src.res[1])
    # average density per source pixel -> people per dst pixel: scale by area ratio (both in m^2)
    lat = 10.675
    src_m2 = src_px_area * (111320 * math.cos(math.radians(lat))) * 110574
    dst_m2 = abs(tf.a * tf.e)
    return dst * (dst_m2 / src_m2)


def lonlat_to_px(lon, lat, crs, tf):
    xs, ys = warp_transform("EPSG:4326", crs, lon, lat)
    inv = ~tf
    cols, rows = zip(*[inv * (x, y) for x, y in zip(xs, ys)]) if xs else ((), ())
    return np.array(rows), np.array(cols)


def main(handoff, kids_tif, schools_gj, places_gj, out_dir, vn_mask_tif=None):
    handoff, out_dir = pathlib.Path(handoff), pathlib.Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    meta, tf, N = load_grid(handoff)
    crs = meta["crs"]
    px_m2 = abs(tf.a * tf.e)
    dates = [r["date"] for r in json.loads((handoff / "dates.json").read_text())]
    nisar = {r["date"]: r for r in json.loads((handoff / "layers" / "nisar.json").read_text())}
    codes = {d: np.array(Image.open(handoff / "layers" / "landwater" / f"{d}.png")) for d in dates}
    water = {d: (c == WATER) for d, c in codes.items()}
    wet_pct = {d: float(water[d][codes[d] != NODATA].mean()) for d in dates}
    base = min(dates, key=lambda d: wet_pct[d])          # driest pass = baseline

    kids = kids_on_grid(kids_tif, crs, tf, N)
    homes = kids > 0.02                                   # populated cells (WorldPop places people only where buildings are)
    kids_cell = block_sum(kids)
    homes_cell = block_sum(homes.astype("float32"))
    GH, GW = kids_cell.shape

    # places (for naming) and schools
    P = json.load(open(places_gj))["features"]
    if vn_mask_tif:   # any national WorldPop file: nodata outside Vietnam
        with rasterio.open(vn_mask_tif) as src:
            inside = [next(src.sample([tuple(f["geometry"]["coordinates"])], masked=True))[0] is not np.ma.masked for f in P]
    else:
        inside = [True] * len(P)
    P = [f for f, k in zip(P, inside) if k]
    pl_r, pl_c = lonlat_to_px([f["geometry"]["coordinates"][0] for f in P],
                              [f["geometry"]["coordinates"][1] for f in P], crs, tf)
    S = json.load(open(schools_gj))["features"]
    s_r, s_c = lonlat_to_px([f["geometry"]["coordinates"][0] for f in S],
                            [f["geometry"]["coordinates"][1] for f in S], crs, tf)
    s_in = (s_r >= 0) & (s_r < N) & (s_c >= 0) & (s_c < N)
    S = [f for f, k in zip(S, s_in) if k]; s_r, s_c = s_r[s_in].astype(int), s_c[s_in].astype(int)
    pre = np.array([f["properties"]["level"] == "preschool" for f in S])
    pre_r, pre_c = s_r[pre], s_c[pre]

    cy, cx = np.mgrid[0:GH, 0:GW]
    cy_px, cx_px = cy * CELL + CELL / 2, cx * CELL + CELL / 2
    # gap factor: no preschool within 2 km of the square centre
    gap_km = GAP_KM * 1000 / abs(tf.a)
    if len(pre_r):
        d2 = (cy_px[..., None] - pre_r) ** 2 + (cx_px[..., None] - pre_c) ** 2
        no_pre = d2.min(-1) > gap_km ** 2
    else:
        no_pre = np.ones((GH, GW), bool)

    def place_name(r, c):
        if not len(pl_r):
            return ""
        i = int(np.argmin((pl_r - r) ** 2 + (pl_c - c) ** 2))
        return P[i]["properties"].get("name", "")

    def lonlat(r, c):
        x, y = tf * (c, r)
        lo, la = warp_transform(crs, "EPSG:4326", [x], [y])
        return round(lo[0], 5), round(la[0], 5)

    def scores_for(d, weights=WEIGHTS, gap=True):
        new = water[d] & ~water[base]
        near = dilate(new, NEAR_PX)
        f_homes = np.divide(block_sum((homes & near).astype("float32")), homes_cell,
                            out=np.zeros_like(homes_cell), where=homes_cell > 0)
        kids_near = block_sum(kids * near)
        f_kids = np.clip(np.sqrt(kids_near / KIDS_FULL), 0, 1)   # sqrt: 50 kids = 0.5, 200+ = 1.0
        f_new = np.clip(block_sum(new.astype("float32")) / CELL ** 2 / NEW_FULL, 0, 1)
        sch_near = np.zeros((GH, GW))
        for r, c in zip(s_r, s_c):
            if near[r, c]:
                sch_near[r // CELL, c // CELL] += 1
        f_sch = np.clip(sch_near / 2, 0, 1)
        raw = (weights["water_near_homes"] * f_homes + weights["young_children"] * f_kids
               + weights["new_water"] * f_new + weights["schools_routes"] * f_sch)
        mult = np.where(no_pre & gap & USE_GAP, GAP_FACTOR, 1.0)
        sc = np.clip(100 * raw * mult, 0, 100)
        sc[kids_cell < MIN_CHILDREN] = np.nan
        return sc, dict(water_near_homes=f_homes, young_children=f_kids, new_water=f_new,
                        schools_routes=f_sch, kids_near=kids_near, sch_near=sch_near, new=new, near=near)

    all_scores, assets, prev_rank, per_pass = [], {}, {}, {}
    for d in dates:
        sc, F = scores_for(d)
        per_pass[d] = sc
        order = [tuple(ix) for ix in np.argwhere(np.isfinite(sc))]
        order.sort(key=lambda ix: -sc[ix])
        ranks = {ix: i + 1 for i, ix in enumerate(order)}
        for ix in order[:50]:
            r, c = ix
            pid = f"cell_{r:02d}_{c:02d}"
            reasons = []
            if F["kids_near"][ix] >= 1:
                reasons.append(f"about {F['kids_near'][ix]:.0f} children under 5 live within ~100 m of flood water")
            if F["water_near_homes"][ix] >= 0.3:
                reasons.append(f"{100 * F['water_near_homes'][ix]:.0f}% of homes here have flood water within ~100 m")
            if F["new_water"][ix] >= 0.2:
                reasons.append(f"{100 * min(F['new_water'][ix] * NEW_FULL, 1):.0f}% of the square is flood water (land on {base})")
            if F["sch_near"][ix] >= 1:
                reasons.append(f"{int(F['sch_near'][ix])} school(s) with flood water within ~100 m")
            if no_pre[ix] and USE_GAP:
                reasons.append("no preschool or kindergarten on OpenStreetMap within 2 km")
            conf = "medium" if kids_cell[ix] >= 50 else "low"
            all_scores.append({"place_id": pid, "pass": d, "score": round(float(sc[ix]), 1), "rank": ranks[ix],
                               "rank_change": (prev_rank.get(pid, None) - ranks[ix]) if pid in prev_rank else None,
                               "factors": {k: round(float(F[k][ix]), 3) for k in WEIGHTS},
                               "gap_factor": GAP_FACTOR if (no_pre[ix] and USE_GAP) else 1.0, "reasons": reasons,
                               "confidence": conf})
            if pid not in assets:
                lo, la = lonlat(r * CELL + CELL / 2, c * CELL + CELL / 2)
                assets[pid] = {"id": pid, "name": f"near {place_name(r * CELL + CELL / 2, c * CELL + CELL / 2)}",
                               "type": "hamlet", "lon": lo, "lat": la,
                               "u": round((c * CELL + CELL / 2) / N, 4), "v": round((r * CELL + CELL / 2) / N, 4),
                               "under5_estimate": round(float(kids_cell[ix]), 0)}
        prev_rank = {f"cell_{r:02d}_{c:02d}": ranks[(r, c)] for (r, c) in order}

    # status per pass for the ranked squares
    for pid, a in assets.items():
        r, c = int(pid[5:7]), int(pid[8:10])
        a["status"] = {}
        for d in dates:
            blk = water[d][r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL]
            frac = blk.mean()
            a["status"][d] = "under_water" if frac > 0.6 else ("water_near" if frac > 0.1 else "clear")
    # schools as assets too
    for f, r, c in zip(S, s_r, s_c):
        lo, la = f["geometry"]["coordinates"]
        p = f["properties"]
        assets[p["id"]] = {"id": p["id"], "name": p["name"], "type": p["type"], "lon": round(lo, 5),
                           "lat": round(la, 5), "u": round(c / N, 4), "v": round(r / N, 4),
                           "status": {d: ("water_near" if dilate(water[d][max(r-4,0):r+5, max(c-4,0):c+5], 0).any() else "clear") for d in dates}}

    # headline (latest pass vs baseline)
    last = dates[-1]
    new = water[last] & ~water[base]
    near = dilate(new, NEAR_PX)
    headline = {
        "pass": last, "baseline": base,
        "water_km2": round(float(water[last].sum()) * px_m2 / 1e6, 1),
        "new_water_km2": round(float(new.sum()) * px_m2 / 1e6, 1),
        "tile_km2": round(N * N * px_m2 / 1e6, 1),
        "water_pct_by_pass": {d: round(100 * wet_pct[d], 1) for d in dates},
        "flood_water_definition": "water on this pass that was land on the baseline pass",
        "populated_cells_near_flood_water_pct": round(100 * float((homes & near).sum() / homes.sum()), 1),
        "children_under5_in_tile": round(float(kids.sum()), -2),
        "children_under5_near_flood_water": round(float((kids * near).sum()), -2),
        "children_under5_near_flood_water_by_pass": {d: round(float((kids * dilate(water[d] & ~water[base], NEAR_PX)).sum()), -2) for d in dates},
        "children_under5_near_any_water_incl_canals": round(float((kids * dilate(water[last], NEAR_PX)).sum()), -2),
        "schools_near_flood_water": int(sum(near[r, c] for r, c in zip(s_r, s_c))), "schools_in_tile": len(S),
        "water_rule": nisar.get(last, {}).get("water_rule", ""),
        "sources": ["NISAR L2 GCOV (provisional), ASF", "WorldPop 2026 age/sex structures, constrained, 100 m (R2025A)",
                    "OpenStreetMap schools and places (ODbL), exported 2026-11-14"],
        "caveats": ["near = within ~100 m (3 pixels of ~31 m)", "canals narrower than ~30 m are not resolved",
                    "OSM school coverage is incomplete in rural areas", "WorldPop counts are modelled estimates",
                    "water rule is provisional; rice fields flooded on purpose not yet separated"]}

    # sensitivity test on the latest pass: each weight x0.5 and x1.5 (renormalised), plus schools off and no gap
    base_sc = per_pass[last]
    def top(sc, k):
        idx = np.argwhere(np.isfinite(sc)); v = sc[np.isfinite(sc)]
        return {tuple(idx[i]) for i in np.argsort(-v)[:k]}
    t5, t10 = top(base_sc, 5), top(base_sc, 10)
    sens = []
    for k in WEIGHTS:
        for m in (0.5, 1.5):
            w = dict(WEIGHTS); w[k] *= m; s = sum(w.values()); w = {a: b / s for a, b in w.items()}
            sc, _ = scores_for(last, w)
            sens.append({"change": f"{k} x{m}", "top5_kept": len(t5 & top(sc, 5)), "top10_kept": len(t10 & top(sc, 10))})
    w = dict(WEIGHTS); w["schools_routes"] = 0; s = sum(w.values()); w = {a: b / s for a, b in w.items()}
    sc, _ = scores_for(last, w); sens.append({"change": "schools factor off (OSM gaps)", "top5_kept": len(t5 & top(sc, 5)), "top10_kept": len(t10 & top(sc, 10))})
    sc, _ = scores_for(last, WEIGHTS, gap=False); sens.append({"change": "no 1.3x gap factor", "top5_kept": len(t5 & top(sc, 5)), "top10_kept": len(t10 & top(sc, 10))})

    (out_dir / "scores.json").write_text(json.dumps(sorted(all_scores, key=lambda r: (r["pass"], r["rank"])), indent=1, ensure_ascii=False))
    (out_dir / "assets.json").write_text(json.dumps(list(assets.values()), indent=1, ensure_ascii=False))
    (out_dir / "headline.json").write_text(json.dumps(headline, indent=1, ensure_ascii=False))
    (out_dir / "sensitivity.json").write_text(json.dumps({"pass": last, "tests": sens}, indent=1))
    np.save(out_dir / "_scores_grid.npy", np.stack([per_pass[d] for d in dates]))
    print(json.dumps(headline, indent=1, ensure_ascii=False))
    print(json.dumps(sens, indent=0))
    for r in [x for x in all_scores if x["pass"] == last][:10]:
        print(r["rank"], assets[r["place_id"]]["name"], r["score"], r["rank_change"], "|", "; ".join(r["reasons"]))


if __name__ == "__main__":
    main(*sys.argv[1:7])
