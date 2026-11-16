"""Baseline-sensitivity check for the hamlet "check first" ranking (pipeline/validate_baseline.py).

Question: does the 2026-10-03 top 10 depend on which pass is used as the dry-season baseline?
"Flood water" in science/score.py = water on this pass that was LAND on the baseline pass
(2026-07-11, the driest). This script re-scores every ~1 km square with the same formula and
weights, changing only the baseline:

  original      baseline 2026-07-11 (as published)
  a_aug         baseline 2026-08-28 (water new since August)
  b_none        no baseline: every water pixel counts (canals and rivers included)
  c_both        stricter: water now that was land on BOTH 2026-07-11 and 2026-08-28
  d_jrc         JRC Global Surface Water as baseline -- NOT RUN (needs a download the
                user has not approved in this session; see validation_baseline.json)

Formula (copied from science/score.py, USE_GAP = False as published):
  score = 100 x (0.35 water_near_homes + 0.25 young_children + 0.20 new_water + 0.20 schools_routes)
  squares = 32 x 32 px (~1 km), "near" = 3 px dilation (~100 m), only squares with >= 20 under-5s.

Inputs that differ from science/score.py, and why:
  * The WorldPop under-5 raster and the OSM schools/places GeoJSON are not in the repo (*.tif is
    git-ignored) and are not on this machine. Children are therefore a PROXY:
      - pixel weights = GHS-BUILT-S building share (handoff/buildings/built.png, 100 m), sampled
        onto the 2048 grid; tile total scaled to 81,100 under-5s (headline.json);
      - for every square that appears in assets.json, the square total is forced to its published
        under5_estimate (these are WorldPop block sums), spread over buildings inside the square.
  * Schools = the 94 school + childcare assets in assets.json (u, v -> pixel), which equals
    "schools_in_tile": 94 in headline.json.
  The script first re-scores the ORIGINAL baseline and compares to handoff/findings/scores.json,
  so the size of the proxy error is reported next to the baseline effect.

Two ranking universes are reported for every variant:
  published_squares   only the 105 hamlet squares in assets.json, whose under-5 totals are the real
                      WorldPop sums. Reproduces the published 2026-10-03 top 10 at 9/10 (5/5 top 5).
                      Squares outside this set cannot enter, so "kept" counts here are an UPPER bound
                      on stability. Compared with the published top 10.
  all_squares_proxy   every square with >= 20 proxy under-5s. Squares outside assets.json rely on the
                      building proxy (r = 0.95 vs WorldPop on the 105 known squares) and several rank
                      above the published top 10 even on the original baseline, so this universe is
                      compared with its own original-baseline top 10 (like-for-like), not the published one.

Usage: python pipeline/validate_baseline.py [HANDOFF_DIR]   (default: ./handoff)
Writes HANDOFF_DIR/findings/validation_baseline.json. Needs numpy + pillow only.
"""
import json, sys, pathlib
import numpy as np
from PIL import Image

WEIGHTS = {"water_near_homes": 0.35, "young_children": 0.25, "new_water": 0.20, "schools_routes": 0.20}
CELL, NEAR_PX, MIN_CHILDREN, KIDS_FULL, NEW_FULL = 32, 3, 20, 200.0, 0.6
WATER = 0
N = 2048
PASS = "2026-10-03"
TILE_UNDER5 = 81100.0


def dilate(mask, r):
    out = mask.copy(); p = np.pad(mask, r); H, W = mask.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= p[r + dy:r + dy + H, r + dx:r + dx + W]
    return out


def block_sum(a, k=CELL):
    H, W = a.shape
    return a[:H // k * k, :W // k * k].reshape(H // k, k, W // k, k).sum(axis=(1, 3))


def pid_rc(pid):
    return int(pid[5:7]), int(pid[8:10])


def main(handoff="handoff"):
    handoff = pathlib.Path(handoff)
    F = handoff / "findings"
    dates = [r["date"] for r in json.loads((handoff / "dates.json").read_text())]
    water = {d: np.array(Image.open(handoff / "layers" / "landwater" / f"{d}.png")) == WATER for d in dates}
    assets = json.loads((F / "assets.json").read_text())
    scores = json.loads((F / "scores.json").read_text())
    hamlets = {a["id"]: a for a in assets if a["type"] == "hamlet"}
    schools = [a for a in assets if a["type"] in ("school", "childcare")]

    # ---- children proxy -------------------------------------------------------------------
    built = np.array(Image.open(handoff / "buildings" / "built.png")).astype("float32") / 255.0
    bh, bw = built.shape
    rows = np.minimum((np.arange(N) + 0.5) / N * bh, bh - 1).astype(int)
    cols = np.minimum((np.arange(N) + 0.5) / N * bw, bw - 1).astype(int)
    w = built[rows][:, cols]                                   # building share per 31 m pixel
    kids = w * (TILE_UNDER5 / w.sum())
    GH = GW = N // CELL
    known = np.zeros((GH, GW), bool)
    for pid, a in hamlets.items():                             # force published WorldPop totals
        r, c = pid_rc(pid)
        blk = (slice(r * CELL, (r + 1) * CELL), slice(c * CELL, (c + 1) * CELL))
        s = w[blk].sum()
        kids[blk] = w[blk] / s * a["under5_estimate"] if s > 0 else a["under5_estimate"] / CELL ** 2
        known[r, c] = True
    homes = kids > 0.02
    kids_cell = block_sum(kids)
    homes_cell = block_sum(homes.astype("float32"))

    s_r = np.clip(np.round(np.array([a["v"] for a in schools]) * N).astype(int), 0, N - 1)
    s_c = np.clip(np.round(np.array([a["u"] for a in schools]) * N).astype(int), 0, N - 1)

    def score_grid(new):
        near = dilate(new, NEAR_PX)
        f_homes = np.divide(block_sum((homes & near).astype("float32")), homes_cell,
                            out=np.zeros_like(homes_cell), where=homes_cell > 0)
        f_kids = np.clip(np.sqrt(block_sum(kids * near) / KIDS_FULL), 0, 1)
        f_new = np.clip(block_sum(new.astype("float32")) / CELL ** 2 / NEW_FULL, 0, 1)
        sch = np.zeros((GH, GW))
        for r, c in zip(s_r, s_c):
            if near[r, c]:
                sch[r // CELL, c // CELL] += 1
        f_sch = np.clip(sch / 2, 0, 1)
        fac = dict(water_near_homes=f_homes, young_children=f_kids, new_water=f_new, schools_routes=f_sch)
        sc = 100 * sum(WEIGHTS[k] * fac[k] for k in WEIGHTS)
        sc = np.clip(sc, 0, 100); sc[kids_cell < MIN_CHILDREN] = np.nan
        sc[known] = np.round(sc[known], 1)   # published scores are rounded to 0.1; ties break by position
        return sc, fac

    def ranking(sc, only_known=False):
        sc = np.where(known, sc, np.nan) if only_known else sc
        idx = np.argwhere(np.isfinite(sc))
        order = idx[np.argsort(-sc[np.isfinite(sc)], kind="stable")]
        return [f"cell_{r:02d}_{c:02d}" for r, c in order]

    def name(pid):
        return hamlets[pid]["name"] if pid in hamlets else "(no published name; proxy-only square)"

    # ---- 1. reproduce the published ranking (original baseline) on every pass --------------
    base0 = dates[0]
    repro, proxy_rank = {}, {}
    for d in dates:
        sc, fac = score_grid(water[d] & ~water[base0])
        rk = ranking(sc); rkk = ranking(sc, True); proxy_rank[d] = (sc, fac, rk, rkk)
        posk = {p: i + 1 for i, p in enumerate(rkk)}
        pub = sorted([s for s in scores if s["pass"] == d], key=lambda s: s["rank"])
        pos = {p: i + 1 for i, p in enumerate(rk)}
        err = {k: [] for k in WEIGHTS}; serr = []
        for s in pub:
            r, c = pid_rc(s["place_id"])
            serr.append(abs(sc[r, c] - s["score"]) if np.isfinite(sc[r, c]) else np.nan)
            for k in WEIGHTS:
                err[k].append(abs(fac[k][r, c] - s["factors"][k]))
        p10 = [s["place_id"] for s in pub[:10]]
        repro[d] = {
            "published_top10": p10,
            "proxy_top10": rk[:10],
            "top10_overlap": len(set(p10) & set(rk[:10])),
            "top5_overlap": len(set(p10[:5]) & set(rk[:5])),
            "published_top10_proxy_ranks": [pos.get(p) for p in p10],
            "published_squares_only": {"top10": rkk[:10], "top10_overlap": len(set(p10) & set(rkk[:10])),
                                       "top5_overlap": len(set(p10[:5]) & set(rkk[:5])),
                                       "published_top10_ranks": [posk.get(p) for p in p10]},
            "score_abs_err_top50_mean": round(float(np.nanmean(serr)), 2),
            "score_abs_err_top50_max": round(float(np.nanmax(serr)), 2),
            "factor_abs_err_top50_mean": {k: round(float(np.mean(v)), 4) for k, v in err.items()},
        }

    # ---- 2. alternative baselines on the 2026-10-03 pass -----------------------------------
    pub10 = repro[PASS]["published_top10"]
    proxy10 = proxy_rank[PASS][2][:10]
    variants = {
        "original": ("baseline 2026-07-11 (published definition), proxy children", water[PASS] & ~water["2026-07-11"]),
        "a_aug": ("baseline 2026-08-28: water now that was land in late August", water[PASS] & ~water["2026-08-28"]),
        "b_none": ("no baseline: all water on 2026-10-03 counts (rivers and canals included)", water[PASS].copy()),
        "c_both": ("water now that was land on BOTH 2026-07-11 and 2026-08-28",
                   water[PASS] & ~water["2026-07-11"] & ~water["2026-08-28"]),
    }
    out_var = {}
    proxy10k = proxy_rank[PASS][3][:10]
    pub_rank = {s["place_id"]: s["rank"] for s in scores if s["pass"] == PASS}

    def compare(rk, sc, ref10):
        pos = {p: i + 1 for i, p in enumerate(rk)}
        return {"top10": [{"rank": i + 1, "place_id": p, "name": name(p), "score": round(float(sc[pid_rc(p)]), 1),
                           "published_rank": pub_rank.get(p)} for i, p in enumerate(rk[:10])],
                "top10_kept": len(set(ref10) & set(rk[:10])), "top5_kept": len(set(ref10[:5]) & set(rk[:5])),
                "rank_of_original_no1": pos.get(ref10[0]),
                "ranks_of_original_top10": {p: pos.get(p) for p in ref10}}

    for key, (desc, new) in variants.items():
        sc, fac = score_grid(new)
        out_var[key] = {
            "description": desc,
            "new_water_km2": round(float(new.sum()) * 32.1 * 29.76 / 1e6, 1),
            "published_squares_vs_published_top10": compare(ranking(sc, True), sc, pub10),
            "all_squares_proxy_vs_proxy_original_top10": compare(ranking(sc), sc, proxy10),
        }
    out_var["d_jrc"] = {"description": "JRC Global Surface Water occurrence/seasonality as baseline",
                        "status": "not run: requires downloading GSW tiles (no account needed, but the download "
                                  "was not approved in this session). Re-run with a GSW-derived mask in place of "
                                  "water['2026-07-11']."}

    result = {
        "pass": PASS,
        "question": "Does the hamlet check-first top 10 depend on the dry-season baseline date?",
        "method": {
            "formula": "100 x (0.35 water_near_homes + 0.25 young_children + 0.20 new_water + 0.20 schools_routes); "
                       "gap factor off; 32 px squares; near = 3 px; >= 20 under-5s (science/score.py)",
            "only_change": "the baseline mask that defines 'new flood water'",
            "children": "PROXY. WorldPop under-5 raster not available locally; GHS-BUILT-S building share "
                        "(handoff/buildings/built.png) scaled to 81,100 under-5s, with published under5_estimate "
                        "forced for the 105 squares in assets.json",
            "schools": "94 school + childcare assets from assets.json",
            "universes": {"published_squares": "105 squares with real WorldPop totals; kept counts are an upper bound on stability; compared to published top 10",
                          "all_squares_proxy": "all squares, proxy children outside the 105; compared to the proxy's own original-baseline top 10"},
            "reading_note": "The 2026-08-28 pass already had 39% water (vs 27% on 2026-07-11), so baselines a and c remove most of the season's flood and rank only late-season water; b ranks permanent canals/rivers too, which science/score.py excludes on purpose.",
            "script": "pipeline/validate_baseline.py",
        },
        "reproduction_of_published": repro,
        "variants": out_var,
    }
    (F / "validation_baseline.json").write_text(json.dumps(result, indent=1, ensure_ascii=False))
    print(json.dumps({d: {k: v for k, v in r.items() if k not in ("published_top10", "proxy_top10")} for d, r in repro.items()}, indent=1))
    for k, v in out_var.items():
        for u in ("published_squares_vs_published_top10", "all_squares_proxy_vs_proxy_original_top10"):
            if u in v:
                x = v[u]
                print(k, u[:17], "kept10", x["top10_kept"], "kept5", x["top5_kept"], "no1->", x["rank_of_original_no1"],
                      [(t["place_id"], t["published_rank"]) for t in x["top10"]])

if __name__ == "__main__":
    main(*sys.argv[1:2])
