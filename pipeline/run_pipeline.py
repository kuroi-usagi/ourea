"""Ourea pipeline: NISAR GCOV -> handoff package. Owner: Eric.
Steps: search ASF -> download (Earthdata via ~/.netrc) -> subset to delta tile -> reproject to UTM 48N
-> export layers + dates.json. Fill in each TODO; keep the CLI stable so Unity and web can rely on it."""
import argparse, json, pathlib

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--track", default="047"); ap.add_argument("--frame", default="083")
    ap.add_argument("--start", default="2026-06-17"); ap.add_argument("--end", default="2026-10-15")
    ap.add_argument("--out", default="../handoff")
    a = ap.parse_args()
    out = pathlib.Path(a.out)
    # TODO 1: asf_search.search(dataset="NISAR", ...) for GCOV on this track/frame and dates
    # TODO 2: download to data/raw/ (gitignored)
    # TODO 3: read HH/HV from HDF5, subset, reproject to EPSG:32648, write PNGs per the contract
    # TODO 4: write dates.json
    print(f"Pipeline stub: track {a.track} frame {a.frame} {a.start}..{a.end} -> {out}")

if __name__ == "__main__":
    main()
