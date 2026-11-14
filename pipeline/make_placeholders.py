"""SIMULATED placeholder layers so Unity/web can build before real NISAR data lands. Owner: Eric.

Follows handoff/CONTRACT.md exactly (2048² grid, UTM 48N, row 0 = north), but the water is FAKE:
it floods the lowest ground of Tina's heightmap, rising pass by pass. Nothing here is real radar.

Run:  python pipeline/make_placeholders.py      -> handoff/placeholder/
"""
import json, pathlib
import numpy as np
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
TERRAIN = ROOT / "handoff" / "terrain"
OUT = ROOT / "handoff" / "placeholder"
N = 2048
# (date, fraction of tile under water, fake Sentinel-2 cloud %)
PASSES = [("2026-07-11", 0.04, 62), ("2026-08-28", 0.12, 48), ("2026-10-03", 0.22, 35)]

def main():
    meta = json.loads((TERRAIN / "meta.json").read_text())
    h = np.fromfile(TERRAIN / "heightmap.r16", dtype="<u2").reshape(2049, 2049)[:N, :N].astype("float32")
    elev = meta["min_m"] + h / 65535 * (meta["max_m"] - meta["min_m"])
    rng = np.random.default_rng(14)
    # smooth noise so the flood edge looks organic rather than a contour line
    noise = rng.normal(0, 1, (N // 32, N // 32)).astype("float32")
    noise = np.array(Image.fromarray(noise).resize((N, N), Image.BICUBIC))
    wet_score = elev + 0.4 * noise  # lower = floods first
    # fake rice paddies: blocky fields flooded on purpose
    blocks = rng.random((N // 64, N // 64)) < 0.12
    rice = np.kron(blocks, np.ones((64, 64), bool))
    nodata = np.zeros((N, N), bool); nodata[-120:, :300] = True  # pretend swath edge

    entries = []
    for date, frac, cloud in PASSES:
        water = wet_score <= np.quantile(wet_score, frac)
        lw = np.full((N, N), 255, np.uint8)
        lw[rice & ~water] = 200
        lw[water] = 0
        lw[nodata] = 128

        # HV gamma0 in dB: water dark, land brighter, speckle; map -25..-5 dB -> 0..255
        db = np.where(water, -22.0, np.where(rice, -17.0, -11.0)) + rng.normal(0, 1.5, (N, N))
        bs = (np.clip((db + 25) / 20, 0, 1) * 255).astype(np.uint8)
        bs[nodata] = 0

        rgb = np.empty((N, N, 3), np.uint8)
        rgb[:] = (70, 110, 50)                      # vegetation
        rgb[rice & ~water] = (95, 140, 120)        # wet paddies
        rgb[water] = (120, 105, 80)                 # muddy Mekong water
        rgb = np.clip(rgb.astype(int) + rng.integers(-8, 9, (N, N, 1)), 0, 255).astype(np.uint8)

        rec = {"date": date, "cloud_pct": cloud}
        for name, arr in (("landwater", lw), ("backscatter", bs), ("optical", rgb)):
            p = OUT / "layers" / name / f"{date}.png"
            p.parent.mkdir(parents=True, exist_ok=True)
            Image.fromarray(arr).save(p, optimize=True)
            rec[name] = str(p.relative_to(OUT))
        entries.append(rec)
        print(f"{date}: water {water.mean():.1%}")

    (OUT / "dates.json").write_text(json.dumps(entries, indent=2))
    (OUT / "meta.json").write_text(json.dumps({
        "simulated": True,
        "notes": "PLACEHOLDER. Water = lowest ground of the DEM plus noise, not radar. Replace with handoff/layers/ from run_pipeline.py.",
        "crs": meta["crs"], "bounds_m": meta["bounds_m"], "size": N}, indent=2))
    print(f"Wrote {OUT}")

if __name__ == "__main__":
    main()
