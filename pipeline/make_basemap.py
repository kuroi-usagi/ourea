"""Cloud-free satellite basemap for the Unity terrain. Owner: Kason.

Run:
    pip install numpy pillow
    python pipeline/make_basemap.py

Fetches EOX "Sentinel-2 cloudless" web-mercator tiles over the tile, reprojects them onto the
handoff grid (UTM 48N, row 0 = north, see handoff/CONTRACT.md) and writes
handoff/basemap/s2cloudless.jpg (4096x4096) + basemap.json. This is the "what the ground looks
like" texture only; the per-pass optical layers for the Compare wipe still come from make_optical.py.
Licence: CC BY-NC-SA 4.0. Attribution must be shown in the app (see basemap.json).
"""
import io, json, math, pathlib, urllib.request
from concurrent.futures import ThreadPoolExecutor
import numpy as np
from PIL import Image

YEAR, ZOOM, SIZE = 2024, 14, 4096
URL = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-{y}_3857/default/g/{z}/{ty}/{tx}.jpg"
ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "handoff" / "basemap"
ATTRIB = f"Sentinel-2 cloudless {YEAR} by EOX IT Services GmbH (s2maps.eu), contains modified Copernicus Sentinel data {YEAR}"


def utm_to_latlon(e, n, zone=48):
    # Snyder's inverse transverse Mercator, WGS84, northern hemisphere
    a, f, k0 = 6378137.0, 1 / 298.257223563, 0.9996
    e2 = f * (2 - f); ep2 = e2 / (1 - e2)
    x = e - 500000.0; m = n / k0
    mu = m / (a * (1 - e2 / 4 - 3 * e2**2 / 64 - 5 * e2**3 / 256))
    e1 = (1 - math.sqrt(1 - e2)) / (1 + math.sqrt(1 - e2))
    p1 = (mu + (3 * e1 / 2 - 27 * e1**3 / 32) * np.sin(2 * mu) + (21 * e1**2 / 16 - 55 * e1**4 / 32) * np.sin(4 * mu)
          + (151 * e1**3 / 96) * np.sin(6 * mu) + (1097 * e1**4 / 512) * np.sin(8 * mu))
    c1 = ep2 * np.cos(p1)**2; t1 = np.tan(p1)**2
    n1 = a / np.sqrt(1 - e2 * np.sin(p1)**2); r1 = a * (1 - e2) / (1 - e2 * np.sin(p1)**2)**1.5
    d = x / (n1 * k0)
    lat = p1 - (n1 * np.tan(p1) / r1) * (d**2 / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1**2 - 9 * ep2) * d**4 / 24
                                          + (61 + 90 * t1 + 298 * c1 + 45 * t1**2 - 252 * ep2 - 3 * c1**2) * d**6 / 720)
    lon = (d - (1 + 2 * t1 + c1) * d**3 / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1**2 + 8 * ep2 + 24 * t1**2) * d**5 / 120) / np.cos(p1)
    return np.degrees(lat), (zone - 1) * 6 - 180 + 3 + np.degrees(lon)


def merc_px(lat, lon):
    # global web-mercator pixel coordinates at ZOOM
    s = 256 * 2**ZOOM
    x = (lon + 180) / 360 * s
    y = (1 - np.arcsinh(np.tan(np.radians(lat))) / math.pi) / 2 * s
    return x, y


def fetch(t):
    tx, ty = t
    req = urllib.request.Request(URL.format(y=YEAR, z=ZOOM, tx=tx, ty=ty), headers={"User-Agent": "ourea-spaceapps/1.0"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return t, np.asarray(Image.open(io.BytesIO(r.read())).convert("RGB"))


def main():
    meta = json.loads((ROOT / "handoff" / "terrain" / "meta.json").read_text())
    left, bottom, right, top = meta["bounds_m"]
    cols = left + (np.arange(SIZE) + 0.5) * (right - left) / SIZE
    rows = top - (np.arange(SIZE) + 0.5) * (top - bottom) / SIZE  # row 0 = north
    E, N = np.meshgrid(cols, rows)
    lat, lon = utm_to_latlon(E, N)
    px, py = merc_px(lat, lon)

    tx0, tx1 = int(px.min() // 256), int(px.max() // 256)
    ty0, ty1 = int(py.min() // 256), int(py.max() // 256)
    tiles = [(x, y) for y in range(ty0, ty1 + 1) for x in range(tx0, tx1 + 1)]
    print(f"fetching {len(tiles)} tiles at z{ZOOM}")
    mosaic = np.zeros(((ty1 - ty0 + 1) * 256, (tx1 - tx0 + 1) * 256, 3), np.uint8)
    with ThreadPoolExecutor(8) as pool:
        for (tx, ty), img in pool.map(fetch, tiles):
            mosaic[(ty - ty0) * 256:(ty - ty0 + 1) * 256, (tx - tx0) * 256:(tx - tx0 + 1) * 256] = img

    # bilinear resample onto the UTM grid
    fx = np.clip(px - tx0 * 256 - 0.5, 0, mosaic.shape[1] - 1.001)
    fy = np.clip(py - ty0 * 256 - 0.5, 0, mosaic.shape[0] - 1.001)
    x0, y0 = fx.astype(int), fy.astype(int)
    wx, wy = (fx - x0)[..., None], (fy - y0)[..., None]
    m = mosaic.astype(np.float32)
    out = (m[y0, x0] * (1 - wx) * (1 - wy) + m[y0, x0 + 1] * wx * (1 - wy)
           + m[y0 + 1, x0] * (1 - wx) * wy + m[y0 + 1, x0 + 1] * wx * wy)

    OUT.mkdir(parents=True, exist_ok=True)
    Image.fromarray(out.clip(0, 255).astype(np.uint8)).save(OUT / "s2cloudless.jpg", quality=90)
    (OUT / "basemap.json").write_text(json.dumps({
        "file": "s2cloudless.jpg", "size": SIZE, "crs": "EPSG:32648", "bounds_m": meta["bounds_m"],
        "source": f"EOX Sentinel-2 cloudless {YEAR}, web-mercator z{ZOOM}, bilinear to the handoff grid",
        "licence": "CC BY-NC-SA 4.0", "attribution": ATTRIB}, indent=2) + "\n")
    print("wrote", OUT / "s2cloudless.jpg")


if __name__ == "__main__":
    main()
