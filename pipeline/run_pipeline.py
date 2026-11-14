"""Ourea pipeline: NISAR GCOV -> handoff package. Owner: Eric.

    python pipeline/run_pipeline.py                         # all passes, track 047 frame 083, Jun 17 - Oct 15
    python pipeline/run_pipeline.py --dates 2026-07-11 2026-08-28 2026-10-03   # just these passes
    python pipeline/run_pipeline.py --skip-download         # reprocess what is already in data/raw/

Steps: search ASF -> download (Earthdata login from ~/.netrc) -> cut HH/HV to the locked tile and put it on
the terrain grid (handoff/terrain/meta.json, 2048x2048) -> write layers per handoff/CONTRACT.md + dates.json.

Water rule: if science/classify.py defines classify(hh_db, hv_db) -> uint8 contract codes, it is used.
Otherwise a simple stand-in is used (Otsu threshold on HH dB, clamped to -20..-13 dB) and flagged in nisar.json.
"""
import argparse, json, netrc, pathlib, re, sys
import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[1]
RAW = ROOT / "data" / "raw"
TILE = (105.30, 10.40, 105.90, 10.95)
N = 2048
LAND, WATER, NODATA, RICE = 255, 0, 128, 200


# ---------- 1. search ----------
def search(track, frame, start, end, only_dates=None):
    import asf_search as asf
    from shapely.geometry import box
    res = asf.search(dataset="NISAR", processingLevel="GCOV", intersectsWith=box(*TILE).wkt,
                     start=start, end=f"{end}T23:59:59Z")
    pat = re.compile(rf"_GCOV_\d{{3}}_{int(track):03d}_[AD]_{int(frame):03d}_")
    by_date = {}
    for p in res:
        fid = p.properties["fileID"]
        if not pat.search(fid):
            continue
        d = p.properties["startTime"][:10]
        if only_dates and d not in only_dates:
            continue
        if d not in by_date or fid > by_date[d].properties["fileID"]:  # keep the latest product version
            by_date[d] = p
    return [by_date[d] for d in sorted(by_date)]


# ---------- 2. download ----------
def download(products):
    """Plain requests + ~/.netrc (Earthdata redirect login). asf_search's own session login fails for some accounts."""
    import requests, time
    try:
        netrc.netrc().authenticators("urs.earthdata.nasa.gov")[0]
    except (FileNotFoundError, TypeError):
        sys.exit("No Earthdata login. Add 'machine urs.earthdata.nasa.gov login USER password PW' to ~/.netrc")
    RAW.mkdir(parents=True, exist_ok=True)
    s = requests.Session()
    for p in products:
        f = RAW / (p.properties["fileID"] + ".h5")
        if f.exists() and f.stat().st_size > 0:
            print(f"  have {f.name}")
            continue
        part, t = f.with_suffix(".part"), time.time()
        with s.get(p.properties["url"], stream=True, timeout=120) as r:
            if r.status_code in (401, 403):
                sys.exit(f"Earthdata refused the download ({r.status_code}). Check ~/.netrc and that you can log in at urs.earthdata.nasa.gov")
            r.raise_for_status()
            size = int(r.headers.get("Content-Length", 0))
            print(f"  downloading {f.name} ({size / 1e9:.1f} GB)", flush=True)
            with open(part, "wb") as out:
                for chunk in r.iter_content(8 << 20):
                    out.write(chunk)
        part.rename(f)
        print(f"    done in {time.time() - t:.0f} s", flush=True)


# ---------- 3. read + regrid ----------
def find(h5, name, prefer="frequencyA"):
    hits = []
    h5.visititems(lambda k, v: hits.append(k) if k.split("/")[-1] == name else None)
    hits.sort(key=lambda k: (prefer not in k, len(k)))
    return h5[hits[0]] if hits else None


def read_tile(path, dst_crs, dst_tf):
    """Return HH, HV (linear gamma0) on the destination grid, NaN where no data."""
    import h5py
    from rasterio.warp import reproject, Resampling, transform_bounds
    from rasterio.transform import from_origin
    with h5py.File(path, "r") as h5:
        xs, ys = find(h5, "xCoordinates")[()], find(h5, "yCoordinates")[()]
        proj = find(h5, "projection")
        epsg = int(proj.attrs.get("epsg_code", proj[()]))
        src_crs = f"EPSG:{epsg}"
        dx, dy = float(xs[1] - xs[0]), float(ys[1] - ys[0])  # dy < 0: row 0 = north
        # only read the window that covers the tile (plus a margin)
        l, b, r, t = transform_bounds("EPSG:4326", src_crs, *TILE, densify_pts=21)
        m = 2000.0
        c0, c1 = np.searchsorted(xs, [l - m, r + m])
        r0, r1 = np.searchsorted(-ys, [-(t + m), -(b - m)])
        win_tf = from_origin(xs[c0] - dx / 2, ys[r0] - dy / 2, dx, -dy)
        out = {}
        for pol in ("HHHH", "HVHV"):
            ds = find(h5, pol)
            if ds is None:
                raise SystemExit(f"{path.name}: no {pol} layer")
            a = ds[r0:r1, c0:c1].astype("float32")
            a[~np.isfinite(a) | (a <= 0)] = np.nan
            dst = np.full((N, N), np.nan, "float32")
            reproject(a, dst, src_transform=win_tf, src_crs=src_crs, dst_transform=dst_tf, dst_crs=dst_crs,
                      resampling=Resampling.average, src_nodata=np.nan, dst_nodata=np.nan)
            out[pol] = dst
    return out["HHHH"], out["HVHV"], {"epsg": epsg, "pixel_m": [dx, -dy]}


# ---------- 4. classify + export ----------
def otsu(v):
    hist, edges = np.histogram(v, bins=256)
    c = (edges[:-1] + edges[1:]) / 2
    w0 = np.cumsum(hist); w1 = w0[-1] - w0
    m0 = np.cumsum(hist * c) / np.maximum(w0, 1)
    m1 = (np.sum(hist * c) - np.cumsum(hist * c)) / np.maximum(w1, 1)
    return float(c[np.argmax(w0 * w1 * (m0 - m1) ** 2)])


def classify(hh_db, hv_db):
    try:
        sys.path.insert(0, str(ROOT / "science"))
        from classify import classify as science_rule  # Mitch's rule, once it exists
        return science_rule(hh_db, hv_db), "science/classify.py"
    except ImportError:
        pass
    valid = np.isfinite(hh_db)
    thr = float(np.clip(otsu(hh_db[valid]), -20, -13)) if valid.any() else -16.0
    codes = np.where(hh_db < thr, WATER, LAND).astype("uint8")
    codes[~valid] = NODATA
    return codes, f"stand-in: HH < {thr:.1f} dB (Otsu), rice not flagged yet"


def to_db(a):
    with np.errstate(divide="ignore", invalid="ignore"):
        return 10 * np.log10(a)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--track", default="047"); ap.add_argument("--frame", default="083")
    ap.add_argument("--start", default="2026-06-17"); ap.add_argument("--end", default="2026-10-15")
    ap.add_argument("--dates", nargs="*", help="only these pass dates (YYYY-MM-DD)")
    ap.add_argument("--skip-download", action="store_true")
    ap.add_argument("--out", default=str(ROOT / "handoff"))
    a = ap.parse_args()
    out = pathlib.Path(a.out)

    from PIL import Image
    from rasterio.transform import from_bounds
    meta = json.loads((ROOT / "handoff" / "terrain" / "meta.json").read_text())
    dst_crs = meta["crs"]
    dst_tf = from_bounds(*meta["bounds_m"], N, N)  # same box as the terrain, 2048 px

    print(f"Searching NISAR GCOV track {a.track} frame {a.frame}, {a.start}..{a.end}")
    prods = search(a.track, a.frame, a.start, a.end, set(a.dates) if a.dates else None)
    print(f"  {len(prods)} passes: {', '.join(p.properties['startTime'][:10] for p in prods)}")
    if not a.skip_download:
        download(prods)

    optical = {}
    opt_log = out / "layers" / "optical" / "optical.json"
    if opt_log.exists():
        optical = {r["pass"]: r for r in json.loads(opt_log.read_text())}

    receipts = []
    dates_path = out / "dates.json"
    dates = {r["date"]: r for r in json.loads(dates_path.read_text())} if dates_path.exists() else {}
    for p in prods:
        fid, d = p.properties["fileID"], p.properties["startTime"][:10]
        f = RAW / (fid + ".h5")
        if not f.exists():
            print(f"  {d}: not downloaded, skipping"); continue
        hh, hv, info = read_tile(f, dst_crs, dst_tf)
        hh_db, hv_db = to_db(hh), to_db(hv)
        codes, rule = classify(hh_db, hv_db)
        bs = (np.clip((np.nan_to_num(hv_db, nan=-25) + 25) / 20, 0, 1) * 255).astype("uint8")
        for name, arr in (("landwater", codes), ("backscatter", bs)):
            pth = out / "layers" / name / f"{d}.png"
            pth.parent.mkdir(parents=True, exist_ok=True)
            Image.fromarray(arr).save(pth, optimize=True)
        valid = codes != NODATA
        wet = float((codes[valid] == WATER).mean()) if valid.any() else 0.0
        o = optical.get(d, {})
        dates[d] = {"date": d, "landwater": f"layers/landwater/{d}.png", "backscatter": f"layers/backscatter/{d}.png",
                    "optical": f"layers/optical/{d}.png" if (out / "layers" / "optical" / f"{d}.png").exists() else None,
                    "cloud_pct": o.get("cloud_pct")}
        receipts.append({"date": d, "granule": fid, "product": "NISAR L2 GCOV", "maturity": "provisional",
                         "water_rule": rule, "water_pct": round(100 * wet, 2),
                         "valid_pct": round(100 * float(valid.mean()), 2), **info})
        print(f"  {d}: water {wet:.1%} of valid, valid {valid.mean():.1%}  [{rule}]")

    (out / "layers").mkdir(parents=True, exist_ok=True)
    dates_path.write_text(json.dumps([dates[k] for k in sorted(dates)], indent=2))
    log = out / "layers" / "nisar.json"
    old = {r["date"]: r for r in json.loads(log.read_text())} if log.exists() else {}
    old.update({r["date"]: r for r in receipts})
    log.write_text(json.dumps([old[k] for k in sorted(old)], indent=2))
    print(f"Wrote {dates_path} and {log}")


if __name__ == "__main__":
    main()
