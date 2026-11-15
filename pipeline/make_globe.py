"""Imagery for the globe and the fly-in. Owner: Kason.

Run:
    pip install numpy pillow
    python pipeline/make_globe.py

Fetches EOX "Sentinel-2 cloudless" web-mercator tiles (the same source as make_basemap.py) and writes:
  handoff/globe/earth.jpg     4096x2048, whole Earth, plate carree (lon -180..180, lat 90..-90)
  handoff/globe/context.jpg   2048x2048, plate carree window of +-WINDOW degrees around the site,
                              the ground you see around the delta tile while flying in
  handoff/globe/near.jpg      2048x2048, +-NEAR degrees at zoom 12 (~110 m pixels): the ground right
                              around the tile, so its edge doesn't jump from sharp to blurry
  handoff/globe/globe.json    site, window bounds, attribution
Licence: CC BY-NC-SA 4.0 (attribution shown in the app's Key card).
"""
import io, json, math, pathlib, urllib.request
from concurrent.futures import ThreadPoolExecutor
import numpy as np
from PIL import Image

YEAR = 2024
URL = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-{y}_3857/default/g/{z}/{ty}/{tx}.jpg"
ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "handoff" / "globe"
ATTRIB = f"Sentinel-2 cloudless {YEAR} by EOX IT Services GmbH (s2maps.eu), contains modified Copernicus Sentinel data {YEAR}"
SITE = (10.675, 105.6)   # centre of the delta tile (handoff/terrain/meta.json bounds_deg)
WINDOW = 4.0             # degrees each side for the context ground
NEAR = 1.0               # degrees each side for the sharper ground next to the tile


def fetch(args):
    z, tx, ty = args
    req = urllib.request.Request(URL.format(y=YEAR, z=z, tx=tx, ty=ty), headers={"User-Agent": "ourea-spaceapps/1.0"})
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return (tx, ty), np.asarray(Image.open(io.BytesIO(r.read())).convert("RGB"))
        except Exception:
            pass
    return (tx, ty), np.zeros((256, 256, 3), np.uint8)


def mosaic(z, tx0, tx1, ty0, ty1):
    tiles = [(z, tx, ty) for tx in range(tx0, tx1 + 1) for ty in range(ty0, ty1 + 1)]
    img = np.zeros(((ty1 - ty0 + 1) * 256, (tx1 - tx0 + 1) * 256, 3), np.uint8)
    with ThreadPoolExecutor(8) as ex:
        for (tx, ty), a in ex.map(fetch, tiles):
            img[(ty - ty0) * 256:(ty - ty0 + 1) * 256, (tx - tx0) * 256:(tx - tx0 + 1) * 256] = a
    return img


def merc(lat, lon, z):
    s = 256 * 2 ** z
    lat = np.clip(lat, -85.05, 85.05)
    return (lon + 180) / 360 * s, (1 - np.arcsinh(np.tan(np.radians(lat))) / math.pi) / 2 * s


def resample(img, z, tx0, ty0, lats, lons):
    x, y = merc(lats, lons, z)
    x = np.clip(x - tx0 * 256, 0, img.shape[1] - 1).astype(int)
    y = np.clip(y - ty0 * 256, 0, img.shape[0] - 1).astype(int)
    return img[y, x]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    # whole Earth at zoom 4 (16 x 16 tiles), to plate carree
    z = 4
    world = mosaic(z, 0, 15, 0, 15)
    W, H = 4096, 2048
    lon = np.linspace(-180, 180, W, endpoint=False) + 180 / W
    lat = np.linspace(90, -90, H, endpoint=False) - 90 / H
    LON, LAT = np.meshgrid(lon, lat)
    earth = resample(world, z, 0, 0, LAT, LON)
    polar = np.abs(LAT) > 85.0   # web mercator stops at 85 degrees: polar ice
    earth[polar] = (230, 236, 240)
    Image.fromarray(earth).save(OUT / "earth.jpg", quality=88)

    # context window at zoom 9 (~300 m pixels)
    z = 9
    la0, la1 = SITE[0] - WINDOW, SITE[0] + WINDOW
    lo0, lo1 = SITE[1] - WINDOW, SITE[1] + WINDOW
    xa, ya = merc(np.array([la1, la0]), np.array([lo0, lo1]), z)
    tx0, tx1 = int(xa[0] // 256), int(xa[1] // 256)
    ty0, ty1 = int(ya[0] // 256), int(ya[1] // 256)
    win = mosaic(z, tx0, tx1, ty0, ty1)
    N = 2048
    lon = np.linspace(lo0, lo1, N)
    lat = np.linspace(la1, la0, N)
    LON, LAT = np.meshgrid(lon, lat)
    Image.fromarray(resample(win, z, tx0, ty0, LAT, LON)).save(OUT / "context.jpg", quality=88)

    # near ring at zoom 12
    z = 12
    la0n, la1n = SITE[0] - NEAR, SITE[0] + NEAR
    lo0n, lo1n = SITE[1] - NEAR, SITE[1] + NEAR
    xa, ya = merc(np.array([la1n, la0n]), np.array([lo0n, lo1n]), z)
    tx0, tx1 = int(xa[0] // 256), int(xa[1] // 256)
    ty0, ty1 = int(ya[0] // 256), int(ya[1] // 256)
    win = mosaic(z, tx0, tx1, ty0, ty1)
    lon = np.linspace(lo0n, lo1n, N)
    lat = np.linspace(la1n, la0n, N)
    LON, LAT = np.meshgrid(lon, lat)
    Image.fromarray(resample(win, z, tx0, ty0, LAT, LON)).save(OUT / "near.jpg", quality=88)

    (OUT / "globe.json").write_text(json.dumps({
        "near_bounds_deg": [lo0n, la0n, lo1n, la1n],
        "site_lat": SITE[0], "site_lon": SITE[1],
        "context_bounds_deg": [lo0, la0, lo1, la1],
        "earth": "plate carree, lon -180..180 left to right, lat 90..-90 top to bottom",
        "attribution": ATTRIB}, indent=2) + "\n")
    print("earth.jpg + context.jpg written")


if __name__ == "__main__":
    main()
