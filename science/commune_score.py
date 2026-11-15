"""Ourea commune ranking (science/commune_score.py). Owner: Mitch.

Ranks the communes (xa/phuong, post-2025 units, from Asher's handoff/communes_layers.geojson) on every
NISAR pass with the same four factors and weights as score.py, and lists each commune's worst ~1 km
squares as "check here first" hotspots. Run score.py first (it writes findings/_scores_grid.npy).

Usage: python commune_score.py HANDOFF_DIR UNDER5_TIF COMMUNES_GEOJSON SCHOOLS_GEOJSON FINDINGS_DIR
"""
import json, sys, pathlib
import numpy as np
from rasterio.features import rasterize
from rasterio.warp import transform_geom
from PIL import Image
import score as S

KIDS_FULL_C = 1500.0       # commune-scale: 1500+ under-5s near flood water = 1.0 (sqrt scale, 375 = 0.5)


def main(handoff, kids_tif, communes_gj, schools_gj, out_dir):
    handoff, out_dir = pathlib.Path(handoff), pathlib.Path(out_dir)
    meta, tf, N = S.load_grid(handoff)
    crs = meta["crs"]
    px_km2 = abs(tf.a * tf.e) / 1e6
    dates = [r["date"] for r in json.loads((handoff / "dates.json").read_text())]
    codes = {d: np.array(Image.open(handoff / "layers" / "landwater" / f"{d}.png")) for d in dates}
    water = {d: c == S.WATER for d, c in codes.items()}
    base = min(dates, key=lambda d: water[d][codes[d] != S.NODATA].mean())
    kids = S.kids_on_grid(kids_tif, crs, tf, N)
    homes = kids > 0.02

    C = json.load(open(communes_gj))["features"]
    shapes = [(transform_geom("EPSG:4326", crs, f["geometry"]), i + 1) for i, f in enumerate(C)]
    cid = rasterize(shapes, out_shape=(N, N), transform=tf, fill=0, dtype="int32")

    Sch = json.load(open(schools_gj))["features"]
    sr, sc = S.lonlat_to_px([f["geometry"]["coordinates"][0] for f in Sch],
                            [f["geometry"]["coordinates"][1] for f in Sch], crs, tf)
    ok = (sr >= 0) & (sr < N) & (sc >= 0) & (sc < N)
    sr, sc = sr[ok].astype(int), sc[ok].astype(int)

    grid = np.load(out_dir / "_scores_grid.npy")               # [pass, GH, GW] 1 km square scores
    GH, GW = grid.shape[1:]
    cell_commune = cid[S.CELL // 2::S.CELL, S.CELL // 2::S.CELL][:GH, :GW]

    nC = len(C) + 1
    def per_commune(a):
        return np.bincount(cid.ravel(), weights=a.ravel().astype("float64"), minlength=nC)

    homes_c = per_commune(homes)
    kids_c = per_commune(kids)
    area_c = np.bincount(cid.ravel(), minlength=nC) * px_km2

    out, prev = [], {}
    for k, d in enumerate(dates):
        new = water[d] & ~water[base]
        near = S.dilate(new, S.NEAR_PX)
        f_homes = np.divide(per_commune(homes & near), homes_c, out=np.zeros(nC), where=homes_c > 0)
        kids_near = per_commune(kids * near)
        f_kids = np.clip(np.sqrt(kids_near / KIDS_FULL_C), 0, 1)
        flood_km2 = per_commune(new) * px_km2
        f_new = np.clip(np.divide(flood_km2, area_c, out=np.zeros(nC), where=area_c > 0) / S.NEW_FULL, 0, 1)
        sch = np.bincount(cid[sr, sc][near[sr, sc]], minlength=nC)
        f_sch = np.clip(sch / 2, 0, 1)
        W = S.WEIGHTS
        score = 100 * (W["water_near_homes"] * f_homes + W["young_children"] * f_kids
                       + W["new_water"] * f_new + W["schools_routes"] * f_sch)
        idx = [i for i in range(1, nC) if kids_c[i] >= 50]
        idx.sort(key=lambda i: -score[i])
        for rank, i in enumerate(idx, 1):
            p = C[i - 1]["properties"]
            cells = np.argwhere((cell_commune == i) & np.isfinite(grid[k]))
            cells = sorted(cells.tolist(), key=lambda rc: -grid[k][rc[0], rc[1]])[:3]
            hot = [{"place_id": f"cell_{r:02d}_{c:02d}", "score": round(float(grid[k][r, c]), 1),
                    "u": round((c * S.CELL + S.CELL / 2) / N, 4), "v": round((r * S.CELL + S.CELL / 2) / N, 4)}
                   for r, c in cells]
            reasons = []
            if kids_near[i] >= 1:
                reasons.append(f"about {kids_near[i]:,.0f} of {kids_c[i]:,.0f} children under 5 live within ~100 m of flood water")
            if f_homes[i] >= 0.2:
                reasons.append(f"{100 * f_homes[i]:.0f}% of homes have flood water within ~100 m")
            if flood_km2[i] >= 0.5:
                reasons.append(f"{flood_km2[i]:.1f} km² of flood water ({100 * flood_km2[i] / max(area_c[i], 1e-9):.0f}% of the commune)")
            if sch[i]:
                reasons.append(f"{int(sch[i])} school(s) with flood water within ~100 m")
            frac = p.get("frac_in_tile", 1.0)
            conf = "low" if frac < 0.5 else "medium"
            out.append({"commune_code": p["code"], "name": p.get("fullName", p["name"]), "name_en": p.get("fullNameEn"),
                        "province": p.get("province"), "pass": d, "score": round(float(score[i]), 1), "rank": rank,
                        "rank_change": (prev[p["code"]] - rank) if p["code"] in prev else None,
                        "factors": {"water_near_homes": round(float(f_homes[i]), 3), "young_children": round(float(f_kids[i]), 3),
                                    "new_water": round(float(f_new[i]), 3), "schools_routes": round(float(f_sch[i]), 3)},
                        "under5_in_tile": round(float(kids_c[i])), "under5_near_flood_water": round(float(kids_near[i])),
                        "flood_km2": round(float(flood_km2[i]), 2), "schools_near_flood_water": int(sch[i]),
                        "share_in_tile": frac, "hotspots": hot, "reasons": reasons, "confidence": conf})
        prev = {C[i - 1]["properties"]["code"]: r for r, i in enumerate(idx, 1)}

    (out_dir / "commune_scores.json").write_text(json.dumps(out, indent=1, ensure_ascii=False))
    last = dates[-1]
    for r in [x for x in out if x["pass"] == last][:10]:
        print(r["rank"], r["name"], f'({r["province"]})', r["score"], "Δ", r["rank_change"], "|", "; ".join(r["reasons"]))


if __name__ == "__main__":
    main(*sys.argv[1:6])
