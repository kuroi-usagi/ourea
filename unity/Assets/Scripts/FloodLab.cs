using System.Collections.Generic;
using UnityEngine;

// "What if" controls on the right of the map.
//  1. Raise the water (MODELED): a flat water surface over the Copernicus DEM ("bathtub" fill).
//     Drag the handle; the surface springs to it and wobbles. Readout = DEM area below that level.
//  2. Radar view (REAL): sweeps NISAR HV backscatter for the current pass over the map.
//  3. Model vs radar: the bathtub level that would cover as much ground as NISAR saw as water.
// Every number comes from data baked in by Editor/OureaLook.cs (DEM curve, nisar.json).
public class FloodLab : MonoBehaviour
{
    public OureaViewer viewer;
    public Transform waterPlane;            // quad over the tile, moved up and down
    public Material waterPlaneMaterial;
    public Texture2D[] radar;               // NISAR HV backscatter per pass (handoff/layers/backscatter)
    public float[] observedWaterPct;        // NISAR water % per pass (handoff/layers/nisar.json)
    public float[] demCurve;                // fraction of tile with elevation <= level, levels minM..maxM
    public float minM, maxM;                // DEM range (m, EGM2008)
    public float heightScale;               // world units for maxM - minM
    public float tileAreaKm2;

    // spring state for the water level (metres)
    float target, level, velocity;
    float radarOn, radarVel, sweep;
    bool radarWanted, dragging;
    Material deltaMat;

    static readonly List<Rect> uiRects = new List<Rect>();
    public static bool PointerOver(Vector2 guiPoint)
    {
        for (int i = 0; i < uiRects.Count; i++) if (uiRects[i].Contains(guiPoint)) return true;
        return false;
    }

    void Start()
    {
        target = level = minM - 0.5f;   // start below the lowest ground: nothing modelled yet
    }

    void Update()
    {
        if (deltaMat == null && viewer != null && viewer.deltaRenderer != null) deltaMat = viewer.deltaRenderer.material;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);

        // under-damped spring: it overshoots a little and settles, like water sloshing up a slope
        float k = dragging ? 140f : 70f, c = dragging ? 16f : 7.5f;
        velocity += (k * (target - level) - c * velocity) * dt;
        level += velocity * dt;

        if (waterPlane != null)
        {
            float y = (level - minM) / Mathf.Max(0.01f, maxM - minM) * heightScale;
            waterPlane.gameObject.SetActive(level > minM + 0.02f);
            waterPlane.localPosition = new Vector3(waterPlane.localPosition.x, y, waterPlane.localPosition.z);
            if (waterPlaneMaterial != null)
            {
                waterPlaneMaterial.SetFloat("_Wobble", Mathf.Clamp01(Mathf.Abs(velocity) * 0.35f));
                waterPlaneMaterial.SetFloat("_Alpha", 0.78f * (1f - 0.85f * radarOn));   // let the radar show through
            }
        }

        // radar: ease the overlay in, and sweep north → south like a descending pass
        radarVel += (90f * ((radarWanted ? 1f : 0f) - radarOn) - 14f * radarVel) * dt;
        radarOn = Mathf.Clamp01(radarOn + radarVel * dt);
        sweep = radarWanted ? Mathf.MoveTowards(sweep, 1f, dt / 1.8f) : Mathf.MoveTowards(sweep, 0f, dt / 0.6f);
        if (deltaMat != null && viewer != null && radar != null && radar.Length > viewer.currentPass)
        {
            deltaMat.SetTexture("_RadarTex", radar[viewer.currentPass]);
            deltaMat.SetFloat("_Radar", radarOn);
            deltaMat.SetFloat("_Sweep", Mathf.SmoothStep(0f, 1f, sweep));
        }
    }

    // ---------- data ----------
    public float FractionBelow(float m)
    {
        if (demCurve == null || demCurve.Length < 2) return 0f;
        float t = Mathf.Clamp01((m - minM) / Mathf.Max(0.01f, maxM - minM)) * (demCurve.Length - 1);
        int i = Mathf.Min((int)t, demCurve.Length - 2);
        return Mathf.Lerp(demCurve[i], demCurve[i + 1], t - i);
    }

    public float LevelForFraction(float f)
    {
        if (demCurve == null || demCurve.Length < 2) return minM;
        for (int i = 1; i < demCurve.Length; i++)
            if (demCurve[i] >= f)
            {
                float a = demCurve[i - 1], b = demCurve[i];
                float t = (i - 1 + (b > a ? (f - a) / (b - a) : 0f)) / (demCurve.Length - 1);
                return Mathf.Lerp(minM, maxM, t);
            }
        return maxM;
    }

    // ---------- UI ----------
    static Texture2D Rounded(Color col, int radius)
    {
        int size = radius * 2 + 4;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(radius - (x + 0.5f), (x + 0.5f) - (size - radius)));
                float dy = Mathf.Max(0f, Mathf.Max(radius - (y + 0.5f), (y + 0.5f) - (size - radius)));
                t.SetPixel(x, y, new Color(col.r, col.g, col.b, col.a * Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy))));
            }
        t.Apply();
        return t;
    }

    // Linear-space colours (the project renders in linear; these display like the viewer's UI).
    static readonly Color Ink = new Color(0.010f, 0.013f, 0.017f);
    static readonly Color Muted = new Color(0.10f, 0.12f, 0.14f);
    static readonly Color Accent = new Color(0.03f, 0.13f, 0.16f);
    static readonly Color Water = new Color(0.05f, 0.25f, 0.55f);
    static readonly Color Modeled = new Color(0.45f, 0.20f, 0.01f);

    GUIStyle sPanel, sEyebrow, sTag, sTitle, sBig, sBody, sSmall, sBtn, sBtnOn;
    Texture2D texTrack, texFill, texKnob;

    static GUIStyle Text(int size, Color c, FontStyle fs = FontStyle.Normal, bool wrap = false)
    {
        var st = new GUIStyle { fontSize = size, fontStyle = fs, wordWrap = wrap, richText = true };
        st.normal.textColor = c;
        return st;
    }

    static GUIStyle Box(Texture2D bg, int r, Color c, int size, FontStyle fs)
    {
        var st = Text(size, c, fs);
        st.alignment = TextAnchor.MiddleCenter;
        st.normal.background = bg;
        st.border = new RectOffset(r + 1, r + 1, r + 1, r + 1);
        return st;
    }

    void Styles()
    {
        if (sPanel != null) return;
        sPanel = Box(Rounded(new Color(0.97f, 0.97f, 0.955f, 0.90f), 12), 12, Ink, 12, FontStyle.Normal);
        sEyebrow = Text(11, Accent, FontStyle.Bold);
        sTag = Box(Rounded(new Color(0.95f, 0.78f, 0.35f, 0.35f), 7), 7, Modeled, 10, FontStyle.Bold);
        sTitle = Text(17, Ink, FontStyle.Bold);
        sBig = Text(24, Ink, FontStyle.Bold);
        sBody = Text(12, Ink, FontStyle.Normal, true);
        sSmall = Text(10, Muted, FontStyle.Normal, true);
        sBtn = Box(Rounded(new Color(0f, 0f, 0f, 0.06f), 8), 8, Ink, 12, FontStyle.Bold);
        sBtn.hover.background = Rounded(new Color(0f, 0f, 0f, 0.12f), 8); sBtn.hover.textColor = Ink;
        sBtnOn = Box(Rounded(Accent, 8), 8, Color.white, 12, FontStyle.Bold);
        texTrack = Rounded(new Color(0f, 0f, 0f, 0.16f), 10);
        texFill = Rounded(Water, 10);
        texKnob = Rounded(Color.white, 13);
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.Layout) uiRects.Clear();
        Styles();
        float s = Mathf.Clamp(Screen.height / 820f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float W = Screen.width / s, H = Screen.height / s;
        const float M = 16f, PW = 240f;

        // Right column, between the legend/credit (top ~236) and the timeline (bottom 120 from edge)
        float top = 244f, bottom = H - M - 104f - 12f;
        float ph = Mathf.Min(404f, bottom - top);
        if (ph < 300f) { GUI.matrix = Matrix4x4.identity; return; }   // window too short: leave the map clear
        var p = new Rect(W - M - PW, bottom - ph, PW, ph);
        GUI.Box(p, GUIContent.none, sPanel);
        uiRects.Add(new Rect(p.x * s, p.y * s, p.width * s, p.height * s));

        float x = p.x + 16, y = p.y + 14, w = PW - 32;
        GUI.Label(new Rect(x, y, 120, 14), "WHAT IF", sEyebrow);
        GUI.Label(new Rect(p.xMax - 16 - 64, y - 2, 64, 16), "MODELED", sTag);
        y += 18;
        GUI.Label(new Rect(x, y, w, 22), "Raise the water", sTitle);
        y += 28;

        // --- vertical water handle: drag up to flood the DEM
        var track = new Rect(x, y, 26, Mathf.Clamp(ph - 252f, 110f, 160f));
        float lo = minM, hi = maxM;
        float tLevel = Mathf.InverseLerp(lo, hi, Mathf.Clamp(level, lo, hi));
        GUI.DrawTexture(track, texTrack);
        var fill = new Rect(track.x, track.yMax - track.height * tLevel, track.width, track.height * tLevel);
        if (fill.height > 1f) GUI.DrawTexture(fill, texFill);
        float squash = 1f + Mathf.Clamp(velocity * 0.08f, -0.25f, 0.25f);   // the knob squishes with speed
        var knob = new Rect(track.center.x - 15f * squash, track.yMax - track.height * tLevel - 15f / squash, 30f * squash, 30f / squash);
        GUI.DrawTexture(new Rect(knob.x - 1, knob.y - 1, knob.width + 2, knob.height + 2), texTrack);   // soft outline
        GUI.DrawTexture(knob, texKnob);

        var e = Event.current;
        var hit = new Rect(track.x - 12, track.y - 16, track.width + 24, track.height + 32);
        if (e.type == EventType.MouseDown && hit.Contains(e.mousePosition)) { dragging = true; e.Use(); }
        if (dragging && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
        {
            target = Mathf.Lerp(lo, hi, Mathf.Clamp01(Mathf.InverseLerp(track.yMax, track.y, e.mousePosition.y)));
            e.Use();
        }
        if (e.type == EventType.MouseUp && dragging)
        {
            target = Mathf.Lerp(lo, hi, Mathf.Clamp01(Mathf.InverseLerp(track.yMax, track.y, e.mousePosition.y)));
            dragging = false;
            e.Use();
        }

        // --- readouts (all from the DEM curve and nisar.json)
        float rx = track.xMax + 14, rw = p.xMax - 16 - rx;
        bool any = level > minM + 0.02f;
        float frac = any ? FractionBelow(level) : 0f;
        GUI.Label(new Rect(rx, y - 2, rw, 30), (any ? level : 0f).ToString("0.0") + " m", sBig);
        GUI.Label(new Rect(rx, y + 28, rw, 28), "water level above sea level", sSmall);
        GUI.Label(new Rect(rx, y + 58, rw, 30), any ? (frac * tileAreaKm2).ToString("#,0") + " km²" : "0 km²", sBig);
        GUI.Label(new Rect(rx, y + 88, rw, 28), any ? (frac * 100f).ToString("0") + "% of the tile is below it" : "Drag the handle up", sSmall);

        y = track.yMax + 14;
        // --- model vs radar for the current pass
        if (viewer != null && observedWaterPct != null && observedWaterPct.Length > viewer.currentPass && viewer.passes.Count > viewer.currentPass)
        {
            float pct = observedWaterPct[viewer.currentPass];
            float match = LevelForFraction(pct / 100f);
            string date = viewer.passes[viewer.currentPass].date;
            GUI.Label(new Rect(x, y, w, 46),
                "NISAR saw <b>" + pct.ToString("0") + "%</b> of the tile as water on " + date +
                ". Flat water would need <b>" + match.ToString("0.0") + " m</b> to cover that much.", sBody);
            if (GUI.Button(new Rect(x, y + 48, w, 26), "Set water to " + match.ToString("0.0") + " m", sBtn)) target = match;
            y += 82;
        }

        // --- radar view (real NISAR backscatter)
        if (GUI.Button(new Rect(x, y, w, 30), radarWanted ? "Radar view: on" : "Show what the radar saw", radarWanted ? sBtnOn : sBtn))
            radarWanted = !radarWanted;
        y += 34;
        GUI.Label(new Rect(x, y, w, 40), radarWanted
            ? "Real NISAR HV backscatter. Smooth open water bounces the signal away (dark); plants and rough ground send it back (bright)."
            : "Bathtub model over the Copernicus DEM, a surface model with trees and buildings. Ignores dikes and flow. Not a forecast.", sSmall);

        GUI.matrix = Matrix4x4.identity;
    }
}
