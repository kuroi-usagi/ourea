"""Walking routes on OpenStreetMap roads, for the 3D app. Owner: Kason.

Google's routes can only be shown on a Google map (web/public/unity/routes/), so the 3D map draws its own:
the OSM road and path network, routed here with Dijkstra.

Run:
    pip install numpy rasterio pillow
    curl -A ourea -H 'Accept: application/json' -o roads.json --data-urlencode \
      'data=[out:json][timeout:240];way["highway"]["highway"!~"motorway|motorway_link|construction|proposed|bus_guideway|raceway"](10.4,105.3,10.95,105.9);out geom;' \
      https://overpass-api.de/api/interpreter
    python pipeline/make_osm_routes.py roads.json

For each pass after the baseline and each of its top-10 hamlets (handoff/findings/scores.json):
  - the nearest schools and day-care points by walking distance on the network (not straight line);
  - to the nearest school and the nearest day care, the shortest route and, when it differs, the driest one:
    the route that keeps furthest from flood water, at most 50% longer;
  - for each route, every ~20 m of it classed with the radar and the bare-earth ground (FABDEM):
      under water   the radar sees new flood water at that spot,
      low, near     within ~100 m of new flood water and less than 1 m above it,
      raised, near  within ~100 m but at least 1 m above it: a dike or raised road,
    where "above it" compares the ground there with the ground at the edge of the nearby flood water.
    "At risk" = under water + low, near. The old rule (anything within 100 m) is kept as "near".
Writes unity/Assets/Ourea/Routes/Resources/osm_routes.json for the 3D map and web/public/unity/routes/osm.json
for the routes page (u, v as in assets.json: u 0 = west, v 0 = north).
Road data (c) OpenStreetMap contributors, ODbL.
"""
import heapq, json, math, pathlib, sys
import numpy as np
from PIL import Image
from rasterio.transform import from_bounds
from rasterio.warp import reproject, transform, Resampling

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "unity" / "Assets" / "Ourea" / "Routes" / "Resources" / "osm_routes.json"
OUT_WEB = ROOT / "web" / "public" / "unity" / "routes" / "osm.json"
WATER = 64            # landwater PNG: 0 = water, 255 = land, 128 = no data, 200 = rice flooded on purpose
NEAR_M = 100.0        # "near water", as in the app
WALK_M_PER_MIN = 75.0 # 4.5 km/h
DRY_WEIGHT = 6.0      # driest route: a metre near water costs 7 metres
DRY_MAX_LONGER = 1.5
DAYCARE_MAX_M = 8000.0
MAX_SNAP_M = 1000.0   # a place further than this from the main road network gets no route
JOIN_M = 30.0         # dead-end paths joined to a road this close
RAISED_M = 1.0        # at least this far above the nearby flood water's edge counts as raised (dike, road)
N_SCHOOLS, N_DAYCARE = 3, 2


def main(roads_path):
    meta = json.loads((ROOT / "handoff/terrain/meta.json").read_text())
    left, bottom, right, top = meta["bounds_m"]

    # ---- graph: OSM node ids -> index, edges between consecutive nodes of each way
    ways = [e for e in json.loads(pathlib.Path(roads_path).read_text())["elements"] if e["type"] == "way" and "geometry" in e]
    ids, lon, lat = {}, [], []
    pairs = []
    for w in ways:
        prev = None
        for nid, g in zip(w["nodes"], w["geometry"]):
            if g is None: prev = None; continue
            i = ids.get(nid)
            if i is None:
                i = ids[nid] = len(lon); lon.append(g["lon"]); lat.append(g["lat"])
            if prev is not None and prev != i: pairs.append((prev, i))
            prev = i
    ex, ny = transform("EPSG:4326", "EPSG:32648", lon, lat)
    X, Y = np.array(ex), np.array(ny)
    # OSM in the countryside is full of paths that end a few metres short of the road they meet: join every
    # dead end to the nearest other node within JOIN_M, so the network isn't broken into islands
    deg = np.bincount(np.array(pairs).ravel(), minlength=len(X))
    cell = {}
    for i, (x, y) in enumerate(zip((X // JOIN_M).astype(int), (Y // JOIN_M).astype(int))): cell.setdefault((x, y), []).append(i)
    joins = 0
    for i in np.nonzero(deg == 1)[0]:
        cx, cy = int(X[i] // JOIN_M), int(Y[i] // JOIN_M); best, bd = -1, JOIN_M
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for j in cell.get((cx + dx, cy + dy), ()):
                    if j == i: continue
                    dd = math.hypot(X[i] - X[j], Y[i] - Y[j])
                    if dd < bd: best, bd = j, dd
        if best >= 0 and bd > 0.5: pairs.append((int(i), best)); joins += 1
    print(joins, "dead ends joined to a road within", JOIN_M, "m")
    a = np.array([p[0] for p in pairs]); b = np.array([p[1] for p in pairs])
    length = np.hypot(X[a] - X[b], Y[a] - Y[b])
    print(len(X), "nodes,", len(a), "edges,", f"{length.sum() / 1000:,.0f} km of road and path")

    # adjacency (both directions), CSR
    src = np.concatenate([a, b]); dst = np.concatenate([b, a]); eid = np.concatenate([np.arange(len(a))] * 2)
    order = np.argsort(src, kind="stable")
    src, dst, eid = src[order], dst[order], eid[order]
    start = np.searchsorted(src, np.arange(len(X) + 1))
    dst_l, eid_l, start_l = dst.tolist(), eid.tolist(), start.tolist()

    def dijkstra(s, cost, targets=None):
        dist = {s: 0.0}; prev = {}; heap = [(0.0, s)]; left_t = set(targets) if targets else None
        while heap:
            d, u = heapq.heappop(heap)
            if d > dist.get(u, math.inf): continue
            if left_t is not None:
                left_t.discard(u)
                if not left_t: break
            for k in range(start_l[u], start_l[u + 1]):
                v = dst_l[k]; nd = d + cost[eid_l[k]]
                if nd < dist.get(v, math.inf): dist[v] = nd; prev[v] = (u, eid_l[k]); heapq.heappush(heap, (nd, v))
        return dist, prev

    def path_to(prev, s, t):
        nodes, edges = [t], []
        while nodes[-1] != s:
            u, e = prev[nodes[-1]]; nodes.append(u); edges.append(e)
        return nodes[::-1], edges[::-1]

    # snap places to the network: the nearest node in the largest connected piece
    _, seen = dijkstra(int(np.argmax(np.bincount(src))), length.tolist())   # a well-connected node
    comp = np.zeros(len(X), bool); comp[list(seen.keys())] = True
    comp[int(np.argmax(np.bincount(src)))] = True
    def snap(p):
        e, n = transform("EPSG:4326", "EPSG:32648", [p["lon"]], [p["lat"]])
        d = np.hypot(X - e[0], Y - n[0]); d[~comp] = np.inf
        i = int(np.argmin(d)); return i, float(d[i])
    print(f"{comp.mean() * 100:.0f}% of nodes in the main network")

    # ---- flood: within 100 m of new flood water, on the landwater grid
    dates = sorted(p.stem for p in (ROOT / "handoff/layers/landwater").glob("*.png"))
    def water(d): return np.asarray(Image.open(ROOT / "handoff/layers/landwater" / f"{d}.png").convert("L")) < WATER
    base = water(dates[0]); H, W = base.shape
    px, py = (right - left) / W, (top - bottom) / H
    # bare-earth ground on the same grid
    gmeta = json.loads((ROOT / "handoff/terrain/ground.json").read_text())
    g = np.fromfile(ROOT / "handoff/terrain/ground.r16", "<u2").reshape(gmeta["size"], gmeta["size"]).astype(np.float32)
    g = gmeta["min_m"] + g / 65535.0 * (gmeta["max_m"] - gmeta["min_m"])
    gy = (np.arange(H) * (gmeta["size"] - 1) / max(1, H - 1)).round().astype(int)
    gx = (np.arange(W) * (gmeta["size"] - 1) / max(1, W - 1)).round().astype(int)
    ground = g[np.ix_(gy, gx)]
    r_px = int(round(NEAR_M / min(px, py)))

    def boxsum(m):
        c = np.pad(m, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
        k = 2 * r_px + 1
        c = np.pad(c, ((r_px, r_px), (r_px, r_px)), mode="edge")
        return c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]

    def classes(d):
        """per pixel: 0 dry, 1 raised near, 2 low near, 3 under water; and the old 'near' rule"""
        f = water(d) & ~base
        near = np.zeros_like(f)
        for dy in range(-r_px, r_px + 1):
            for dx in range(-r_px, r_px + 1):
                if dx * dx + dy * dy > r_px * r_px: continue
                near |= np.roll(np.roll(f, dy, 0), dx, 1)
        # the flood water's level nearby: mean ground height of the flood pixels around (the water's edge
        # sits on that ground, so its level is at least that)
        n = boxsum(f.astype(np.float32)); level = boxsum(np.where(f, ground, 0).astype(np.float32)) / np.maximum(n, 1)
        cls = np.zeros(f.shape, np.uint8)
        cls[near] = np.where(ground[near] - level[near] >= RAISED_M, 1, 2)
        cls[f] = 3
        return cls, near
    # each edge sampled every ~20 m
    nseg = np.maximum(1, np.ceil(length / 20.0)).astype(int)
    rep = np.repeat(np.arange(len(a)), nseg)
    t = (np.concatenate([np.arange(n) for n in nseg]) + 0.5) / np.repeat(nseg, nseg)
    sx = X[a][rep] + (X[b][rep] - X[a][rep]) * t; sy = Y[a][rep] + (Y[b][rep] - Y[a][rep]) * t
    ci = np.clip(((sx - left) / px).astype(int), 0, W - 1); ri = np.clip(((top - sy) / py).astype(int), 0, H - 1)

    places = json.loads((ROOT / "handoff/findings/assets.json").read_text())
    byid = {p["id"]: p for p in places}
    dests = [p for p in places if p["type"] in ("school", "childcare")]
    dsnap = {p["id"]: snap(p) for p in dests}
    scores = json.loads((ROOT / "handoff/findings/scores.json").read_text())

    def uv(nodes):
        pts = []
        for i in nodes:
            u, v = (X[i] - left) / (right - left), (top - Y[i]) / (top - bottom)
            if not pts or (u - pts[-2]) ** 2 + (v - pts[-1]) ** 2 > (5 / (right - left)) ** 2: pts += [round(u, 5), round(v, 5)]
        return pts   # flat: u0, v0, u1, v1, ... (Unity's JsonUtility reads no nested arrays)

    # the classes for the routes page, on its lat/lon grid (like the flood overlays): 0 dry, 85 raised near,
    # 170 low near, 255 under water
    lon0, lat0, lon1, lat1 = meta["bounds_deg"]
    def save_risk(cls, d):
        Wd = 2048; Hd = round(Wd * (lat1 - lat0) / (lon1 - lon0) / math.cos(math.radians((lat0 + lat1) / 2)))
        out = np.zeros((Hd, Wd), np.uint8)
        reproject((cls * 85).astype(np.uint8), out, src_transform=from_bounds(left, bottom, right, top, W, H), src_crs="EPSG:32648",
                  dst_transform=from_bounds(lon0, lat0, lon1, lat1, Wd, Hd), dst_crs="EPSG:4326", resampling=Resampling.nearest)
        Image.fromarray(out, "L").save(OUT_WEB.parent / f"risk_{d}.png", optimize=True)

    def share(cls_s, which):
        return np.bincount(rep, weights=np.isin(cls_s, which).astype(float), minlength=len(a)) / nseg

    def summary(e, sh):
        L = max(float(length[e].sum()), 1.0)
        return {k: round(float((length[e] * v[e]).sum() / L), 3) for k, v in sh.items()}

    def lonlat(p): return {"lat": p["lat"], "lon": p["lon"]}

    passes = []
    for d in dates[1:]:
        cls, near = classes(d)
        save_risk(cls, d)
        cs = cls[ri, ci]
        sh = {"under": share(cs, [3]), "low": share(cs, [2]), "raised": share(cs, [1]),
              "near": np.bincount(rep, weights=near[ri, ci].astype(float), minlength=len(a)) / nseg}
        risk = sh["under"] + sh["low"]
        wet_e = risk
        cost_len = length.tolist(); cost_dry = (length * (1 + DRY_WEIGHT * (risk + 0.1 * sh["raised"]))).tolist()
        hamlets = []
        top10 = sorted((s for s in scores if s["pass"] == d and s["rank"] <= 10 and s["score"] > 0), key=lambda s: s["rank"])
        for s in top10:
            h = byid.get(s["place_id"])
            if not h: continue
            hs, hgap = snap(h)
            if hgap > MAX_SNAP_M:   # no mapped road near this hamlet (or it's on an island): say so, don't invent a route
                hamlets.append({"id": h["id"], "name": h["name"], "rank": s["rank"], **lonlat(h), "under5": h.get("under5_estimate"),
                                "snap_m": round(hgap), "no_road": True, "dests": [], "routes": []})
                print(d, "#%d" % s["rank"], h["name"], "no mapped road within", MAX_SNAP_M, "m"); continue
            dist, prev = dijkstra(hs, cost_len)
            picks, near_d = [], []
            for kind, n_ in (("childcare", N_DAYCARE), ("school", N_SCHOOLS)):   # day care first: it's what keeps under-fives safe
                reach = sorted((x for x in dests if x["type"] == kind and dsnap[x["id"]][1] <= MAX_SNAP_M and dsnap[x["id"]][0] in dist), key=lambda x: dist[dsnap[x["id"]][0]])
                if kind == "childcare": reach = [x for x in reach if dist[dsnap[x["id"]][0]] <= DAYCARE_MAX_M]
                for x in reach[:n_]:
                    near_d.append({"id": x["id"], "name": x["name"], "type": kind, **lonlat(x), "walk_m": round(dist[dsnap[x["id"]][0]] + dsnap[x["id"]][1] + hgap)})
                if reach: picks.append(reach[0])
            routes = []
            for x in picks:
                t_ = dsnap[x["id"]][0]
                n1, e1 = path_to(prev, hs, t_)
                L1 = float(length[e1].sum()); w1 = float((length[e1] * wet_e[e1]).sum() / max(L1, 1)); s1 = summary(e1, sh)
                dd, pp = dijkstra(hs, cost_dry, [t_])
                n2, e2 = path_to(pp, hs, t_)
                L2 = float(length[e2].sum()); w2 = float((length[e2] * wet_e[e2]).sum() / max(L2, 1)); s2 = summary(e2, sh)
                base_r = {"to": x["id"], "to_name": x["name"], "to_type": x["type"]}
                routes.append({**base_r, "kind": "shortest", "m": round(L1), "min": round(L1 / WALK_M_PER_MIN), "wet": round(w1, 3), **s1, "pts": uv(n1)})
                if e2 != e1 and L2 <= L1 * DRY_MAX_LONGER and w2 < w1 - 0.05:
                    routes.append({**base_r, "kind": "driest", "m": round(L2), "min": round(L2 / WALK_M_PER_MIN), "wet": round(w2, 3), **s2, "pts": uv(n2)})
            hamlets.append({"id": h["id"], "name": h["name"], "rank": s["rank"], **lonlat(h), "under5": h.get("under5_estimate"),
                            "snap_m": round(hgap), "dests": near_d, "routes": routes})
            print(d, "#%d" % s["rank"], h["name"], " | ".join(f'{r["to_type"]} {r["kind"]} {r["m"]} m risk {r["wet"]*100:.0f}% (under {r["under"]*100:.0f}, low {r["low"]*100:.0f}, raised {r["raised"]*100:.0f}, old-rule near {r["near"]*100:.0f})' for r in routes))
        passes.append({"date": d, "hamlets": hamlets})

    doc = json.dumps({"source": "Roads: OpenStreetMap contributors (ODbL). Ground: FABDEM V1-2. Water: NISAR L2 GCOV (provisional). Routed by pipeline/make_osm_routes.py",
                      "baseline": dates[0],
                      "places": [{"id": x["id"], "name": x["name"], "type": x["type"], **lonlat(x)} for x in dests], "near_m": NEAR_M, "raised_m": RAISED_M, "walk_m_per_min": WALK_M_PER_MIN, "passes": passes},
                     ensure_ascii=False, separators=(",", ":"))
    for o in (OUT, OUT_WEB):
        o.parent.mkdir(parents=True, exist_ok=True); o.write_text(doc)
        print("wrote", o, o.stat().st_size // 1024, "KB")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "roads.json")
