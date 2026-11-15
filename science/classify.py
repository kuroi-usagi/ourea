"""Ourea water rule (science/classify.py). Owner: Mitch.

run_pipeline.py imports classify(hh_db, hv_db) from this file and uses it instead of the stand-in.
Inputs: HH and HV gamma0 in dB on the 2048x2048 tile grid (NaN = no data).
Output: uint8 contract codes: 255 land, 0 water, 128 no data. (200 = rice flooded on purpose is set
later by rice_flag(), which needs several passes; one pass alone cannot tell rice from flood.)

Physics, in plain words:
- Calm open water is a mirror: the L-band signal bounces away, so it is dark in BOTH HH and HV.
- HH is the main test: water is darker in HH than bare soil, crops or towns (sunglasses rule: water
  reflects H-wiggles away very efficiently at NISAR's slant angle).
- HV ("twisted" echo) comes back from leaves. Over water it sits near the noise floor (about -24 dB on
  these passes). It is the second check: a pixel that is dark in HH but has HV above -20 dB has
  something leafy in it, so it is not counted as open water. HV alone is NOT enough: bare harvested
  fields in July are also dark in HV (tested: HV-only called 68% of the Jul 11 tile water).
- Thresholds come from each pass's own histogram (Otsu), clamped to physically sensible ranges,
  so a noisy pass cannot push the split somewhere absurd.
Known limits: water under dense plants (bright HH double bounce) is NOT counted as water by this rule,
so flooded forest / tall rice is undercounted; canals narrower than a ~30 m pixel are missed;
wind-roughened water can be missed. All flagged in the limits paragraph.
"""
import numpy as np

LAND, WATER, NODATA, RICE = 255, 0, 128, 200

HH_CLAMP = (-18.0, -14.0)   # HH water threshold (Otsu per pass) must fall in this range (dB)
HH_FALLBACK = -15.0
HV_MAX_WATER = -20.0        # open water must also have HV below this (dB): nothing leafy in the pixel
RULE_VERSION = "ourea-v1 (HH Otsu clamped -18..-14 dB AND HV < -20 dB, 3x3 majority)"


def otsu(v, bins=256):
    v = v[np.isfinite(v)]
    if v.size < 1000:
        return None
    hist, edges = np.histogram(v, bins=bins)
    c = (edges[:-1] + edges[1:]) / 2
    w0 = np.cumsum(hist); w1 = w0[-1] - w0
    m0 = np.cumsum(hist * c) / np.maximum(w0, 1)
    m1 = (np.sum(hist * c) - np.cumsum(hist * c)) / np.maximum(w1, 1)
    return float(c[np.argmax(w0 * w1 * (m0 - m1) ** 2)])


def hh_threshold(hh_db):
    t = otsu(hh_db)
    return HH_FALLBACK if t is None else float(np.clip(t, *HH_CLAMP))


def majority3(mask):
    """3x3 majority vote: removes single-pixel speckle without blurring field edges much."""
    m = np.pad(mask.astype("uint8"), 1, mode="edge")
    s = sum(m[1 + dy:m.shape[0] - 1 + dy, 1 + dx:m.shape[1] - 1 + dx]
            for dy in (-1, 0, 1) for dx in (-1, 0, 1))
    return s >= 5


def classify(hh_db, hv_db):
    """Return uint8 codes (255 land, 0 water, 128 no data)."""
    hh = np.asarray(hh_db, dtype="float32")
    hv = np.asarray(hv_db, dtype="float32")
    valid = np.isfinite(hh) & np.isfinite(hv)
    thr = hh_threshold(hh[valid])
    water = valid & (hh < thr) & (hv < HV_MAX_WATER)
    water = majority3(water) & valid
    codes = np.full(hv.shape, LAND, "uint8")
    codes[water] = WATER
    codes[~valid] = NODATA
    classify.last_info = {"rule": RULE_VERSION, "hh_threshold_db": round(thr, 2)}
    return codes


def rice_flag(passes):
    """Multi-pass rule, run after every pass is classified.
    passes: list of (date, codes, hv_db) sorted by date. Returns {date: codes with RICE where flagged}.
    A pixel is 'rice flooded on purpose' on a pass if it is water then AND its HV rises by >= 4 dB to
    above -17 dB within the next two passes (plants growing up out of the water: the rice life cycle).
    River flood water stays dark in HV until it drains, so it is not flagged.
    Needs >= 3 passes 12 days apart; with gaps of 6+ weeks it will under-flag (tell the judges)."""
    out = {}
    for i, (d, codes, hv) in enumerate(passes):
        c = codes.copy()
        later = [p[2] for p in passes[i + 1:i + 3]]
        if later:
            grows = np.zeros(c.shape, bool)
            for hv2 in later:
                grows |= (hv2 - hv >= 4.0) & (hv2 > -17.0)
            c[(codes == WATER) & grows] = RICE
        out[d] = c
    return out
