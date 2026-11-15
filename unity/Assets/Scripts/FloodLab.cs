using System.Collections.Generic;
using UnityEngine;

// "What if" controls on the right of the map.
//  1. Raise the water (MODELED): a flat water surface over the Copernicus DEM ("bathtub" fill).
//     Drag the handle; the surface springs to it and wobbles. Readout = DEM area below that level.
//  2. Radar view (REAL): sweeps NISAR HV backscatter for the current pass over the map.
//  3. Model vs radar: the bathtub level that would cover as much ground as NISAR saw as water.
//  4. Compare wipe (REAL): drag a line across the map; left is the Sentinel-2 photo nearest the
//     pass (clouds and all, from handoff/layers/optical), right is the NISAR radar water.
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
    public string groundSource;             // which elevation the bathtub uses (bare earth when available)
    public BuildingField buildings;         // for "built-up land below the water"
    public DataLoader live;                 // live commune ranking from the Ourea API (Asher)
    public double[] boundsM;                // tile bounds on the UTM grid, for flying to a commune
    public float sizeX, sizeZ;
    bool showCommunes;
    Vector2 communeScroll;
    public Texture2D[] optical;             // Sentinel-2 true colour nearest each pass
    public string[] opticalDate;            // its image date (optical.json)
    public float[] opticalCloud;            // its cloud % (optical.json)

    // spring state for the water level (metres)
    float target, level, velocity;
    float radarOn, radarVel, sweep;
    bool radarWanted, dragging;
    bool compareWanted, draggingWipe;
    float compareOn, compareVel, wipe = 0.5f, wipeTarget = 0.5f, wipeVel;
    Material deltaMat;

    // "Watch a village flood": fly low over the village cluster the NISAR-matching level would
    // flood most, then raise the modeled water there from almost nothing to that level.
    bool story;
    float storyT, storyFrom, storyTo;
    const float StoryDelay = 1.6f, StoryRise = 9f;

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

        if (story)
        {
            if (dragging) story = false;
            else
            {
                storyT += dt;
                float t = Mathf.Clamp01((storyT - StoryDelay) / StoryRise);
                target = Mathf.Lerp(storyFrom, storyTo, t * t * (3f - 2f * t));
                if (t >= 1f) story = false;
            }
        }

        // under-damped spring: it overshoots a little and settles, like water sloshing up a slope
        float k = dragging ? 140f : 70f, c = dragging ? 16f : 7.5f;
        velocity += (k * (target - level) - c * velocity) * dt;
        level += velocity * dt;

        if (waterPlane != null)
        {
            float y = (level - minM) / Mathf.Max(0.01f, maxM - minM) * heightScale;
            waterPlane.gameObject.SetActive(level > minM + 0.02f);
            waterPlane.localPosition = new Vector3(waterPlane.localPosition.x, y, waterPlane.localPosition.z);
            // buildings (Shaders/OureaBuilding.shader) turn red where their ground is under this
            Shader.SetGlobalFloat("_OureaFloodY", level > minM + 0.02f ? waterPlane.position.y : -1e6f);
            if (waterPlaneMaterial != null)
            {
                waterPlaneMaterial.SetFloat("_Wobble", Mathf.Clamp01(Mathf.Abs(velocity) * 0.35f));
                waterPlaneMaterial.SetFloat("_Alpha", 0.94f * (1f - 0.85f * Mathf.Max(radarOn, compareOn)));   // let radar/photo show through
            }
        }

        // radar: ease the overlay in, and sweep north → south like a descending pass
        radarVel += (90f * ((radarWanted ? 1f : 0f) - radarOn) - 14f * radarVel) * dt;
        radarOn = Mathf.Clamp01(radarOn + radarVel * dt);
        sweep = radarWanted ? Mathf.MoveTowards(sweep, 1f, dt / 1.8f) : Mathf.MoveTowards(sweep, 0f, dt / 0.6f);
        // compare wipe: ease in, and let the line spring after the pointer
        compareVel += (90f * ((compareWanted ? 1f : 0f) - compareOn) - 14f * compareVel) * dt;
        compareOn = Mathf.Clamp01(compareOn + compareVel * dt);
        float kw = draggingWipe ? 260f : 120f, cw = draggingWipe ? 24f : 11f;
        wipeVel += (kw * (wipeTarget - wipe) - cw * wipeVel) * dt;
        wipe = Mathf.Clamp(wipe + wipeVel * dt, 0.02f, 0.98f);
        if (deltaMat != null && viewer != null && optical != null && optical.Length > viewer.currentPass && optical[viewer.currentPass] != null)
        {
            deltaMat.SetTexture("_OpticalTex", optical[viewer.currentPass]);
            deltaMat.SetFloat("_Compare", compareOn);
            deltaMat.SetFloat("_WipeX", wipe);
        }

        if (deltaMat != null && viewer != null && radar != null && radar.Length > viewer.currentPass)
        {
            deltaMat.SetTexture("_RadarTex", radar[viewer.currentPass]);
            deltaMat.SetFloat("_Radar", radarOn);
            deltaMat.SetFloat("_Sweep", Mathf.SmoothStep(0f, 1f, sweep));
        }
    }

    // The wipe line across the whole map, with a label on each side. Drawn before the panel so the
    // panel stays on top of it.
    void DrawWipe(float W, float H, float s)
    {
        if (compareOn < 0.01f || viewer == null) return;
        const float M = 16f;
        float top = M, bottom = H - M - 72f - 10f;   // stops above the timeline pill
        float lx = wipe * W;
        var old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, compareOn);
        GUI.DrawTexture(new Rect(lx - 1f, top, 2f, bottom - top), Texture2D.whiteTexture);
        float squash = 1f + Mathf.Clamp(Mathf.Abs(wipeVel) * 0.15f, 0f, 0.3f);
        float ky = top + (bottom - top) * 0.52f;
        var knob = new Rect(lx - 17f * squash, ky - 17f / squash, 34f * squash, 34f / squash);
        GUI.DrawTexture(new Rect(knob.x - 1, knob.y - 1, knob.width + 2, knob.height + 2), texTrack);
        GUI.DrawTexture(knob, texKnob);
        GUI.Label(knob, "< >", sKnob);

        int pi = viewer.currentPass;
        string photo = "CAMERA  ·  Sentinel-2 " + (opticalDate != null && opticalDate.Length > pi ? opticalDate[pi] : "") +
                       (opticalCloud != null && opticalCloud.Length > pi ? "  ·  " + opticalCloud[pi].ToString("0") + "% cloud" : "");
        string radarLbl = "RADAR  ·  NISAR " + viewer.passes[pi].date + "  ·  water";
        // labels hug the line; each hides when it would run under the side panels
        float ly = bottom - 34f;
        float pw = sChip.CalcSize(new GUIContent(photo)).x, rw = sChip.CalcSize(new GUIContent(radarLbl)).x;
        if (lx - pw - 10f > M) GUI.Label(new Rect(lx - pw - 10f, ly, pw, 24f), photo, sChip);
        if (lx + 10f + rw < W - 120f) GUI.Label(new Rect(lx + 10f, ly, rw, 24f), radarLbl, sChip);
        GUI.color = old;

        // drag anywhere on the line
        var hit = new Rect(lx - 20f, top, 40f, bottom - top);
        uiRects.Add(new Rect(hit.x * s, hit.y * s, hit.width * s, hit.height * s));
        var e = Event.current;
        if (e.type == EventType.MouseDown && hit.Contains(e.mousePosition)) { draggingWipe = true; e.Use(); }
        if (draggingWipe && (e.type == EventType.MouseDrag || e.type == EventType.MouseUp || e.type == EventType.MouseDown))
        {
            wipeTarget = Mathf.Clamp(e.mousePosition.x / W, 0.02f, 0.98f);
            if (e.type == EventType.MouseUp) draggingWipe = false;
            e.Use();
        }
    }

    float MatchLevel()
    {
        if (viewer == null || observedWaterPct == null || observedWaterPct.Length == 0) return LevelForFraction(0.5f);
        // the baseline pass has the least water: show the latest pass's level instead
        int pi = viewer.currentPass > 0 ? viewer.currentPass : observedWaterPct.Length - 1;
        return LevelForFraction(Mathf.Clamp(pi < observedWaterPct.Length ? observedWaterPct[pi] : 50f, 1f, 99f) / 100f);
    }

    // ---- for the guided story (Scripts/StoryMode.cs) ----
    [System.NonSerialized] public bool guided;   // the story is running: hide the fly-in button
    public void StartVillageFlood() { StartStory(); }
    public void Drain() { story = false; target = minM - 0.5f; }
    public void CloseCard() { if (card != Card.None) Toggle(card); }
    public void OpenPhoto() { if (card != Card.Photo) Toggle(Card.Photo); }
    public void OpenRadar() { if (card != Card.Radar) Toggle(Card.Radar); }
    public void OpenCommunes() { if (card != Card.Ranking) Toggle(Card.Ranking); showCommunes = true; }
    public float CloudPct(int pass) { return opticalCloud != null && pass < opticalCloud.Length ? opticalCloud[pass] : -1f; }
    public float WaterPct(int pass) { return observedWaterPct != null && pass < observedWaterPct.Length ? observedWaterPct[pass] : -1f; }
    public float BuiltFloodedHa() { return buildings != null ? buildings.BuiltAreaBelow(level) : 0f; }

    void StartStory()
    {
        storyTo = MatchLevel();
        storyFrom = LevelForFraction(0.03f);
        Vector3 spot;
        if (buildings != null && buildings.BestFloodSpot(storyTo, out spot) && viewer != null && viewer.orbit != null)
            viewer.orbit.Focus(spot, 30f, 33f);
        target = level = storyFrom; velocity = 0f;
        storyT = 0f; story = true;
        if (card == Card.Photo || card == Card.Radar) Toggle(card);   // plain map while it floods
    }

    // A big readout at the top while modeled water is up: depth and how much village land is under it.
    void DrawFloodCounter(float W)
    {
        bool any = level > minM + 0.02f;
        float x0 = 16f + 268f + 12f;
        if (!any)
        {
            if (guided) return;
            var b = new Rect(16f, 16f + 58f + 10f + 48f, 268f, 40f);
            Block(b, curScale);
            if (GUI.Button(b, "WATCH A VILLAGE FLOOD  >", sBtnOn)) StartStory();
            return;
        }
        float cw = Mathf.Min(460f, W - x0 - 16f - 120f);
        var r = new Rect(Mathf.Max(x0, W * 0.5f - cw * 0.5f), 16f, cw, 96f);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, curScale);
        float x = r.x + 18f, y = r.y + 12f;
        GUI.Label(new Rect(x, y, 140, 34), level.ToString("0.0") + " m", sBig);
        GUI.Label(new Rect(x, y + 34, 140, 16), "water above sea level", sSmall);
        float bx = x + 130f, bw = r.xMax - 18f - bx;
        if (buildings != null && buildings.builtAreaHa > 0f)
        {
            float bha = buildings.BuiltAreaBelow(level);
            GUI.Label(new Rect(bx, y, bw, 34), bha.ToString("#,0") + " ha", sBigRed);
            GUI.Label(new Rect(bx, y + 34, bw, 16), "of built-up land flooded (" + (bha / buildings.builtAreaHa * 100f).ToString("0") + "% of all)", sSmall);
        }
        GUI.Label(new Rect(x, y + 56, r.width - 36 - 64, 16), (FractionBelow(level) * 100f).ToString("0") + "% of the map under water  ·  MODELED, not a forecast", sSmall);
        if (GUI.Button(new Rect(r.xMax - 70f, r.yMax - 32f, 56f, 22f), "Drain", sBtn)) { story = false; target = minM - 0.5f; }
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

    // ---------- UI: quiet map ----------
    // A tool dock on the right; one card open at a time beside it. Nothing else covers the map.
    enum Card { None, Water, Photo, Radar, Ranking, Key }
    Card card = Card.None;

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

    // The project renders in linear space: IMGUI colours are given linear and show up gamma-encoded.
    // Lin() takes the colour you want to SEE (sRGB 0..255) so swatches match the map.
    static Color Lin(int r, int g, int b) { return new Color(Mathf.Pow(r / 255f, 2.2f), Mathf.Pow(g / 255f, 2.2f), Mathf.Pow(b / 255f, 2.2f)); }
    static readonly Color Ink = OureaTheme.Ink;
    static readonly Color Muted = OureaTheme.Muted;
    static readonly Color Accent = OureaTheme.Accent;
    static readonly Color Water = Lin(58, 140, 200);
    static readonly Color Modeled = OureaTheme.Warn;
    // what the map actually shows (Shaders/OureaWater.shader, OureaFloodPlane.shader, BeaconField)
    static readonly Color KeyRiver = Lin(52, 78, 92);
    static readonly Color KeyFlood = Lin(118, 172, 222);
    static readonly Color KeySheet = Lin(64, 112, 130);
    static readonly Color KeyWetBuilding = Lin(219, 66, 46);
    static readonly Color KeyTop1 = Lin(219, 64, 52);
    static readonly Color KeyTop10 = Lin(255, 170, 60);
    static readonly Color KeySchool = Lin(64, 120, 245);

    float curScale = 1f;
    GUIStyle sBigRed;
    GUIStyle sPanel, sEyebrow, sTag, sTitle, sBig, sBody, sSmall, sBtn, sBtnOn, sKnob, sChip, sDock, sDockOn, sClose;
    Texture2D texTrack, texFill, texKnob, texDot;

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
        sPanel = Box(OureaTheme.PanelTex, 14, Ink, 12, FontStyle.Normal);
        sEyebrow = OureaTheme.Text(11, Accent, true);
        sTag = OureaTheme.Box(OureaTheme.Rounded(new Color(0, 0, 0, 0), 5, Modeled), 5, Modeled, 10);
        sTitle = OureaTheme.Text(16, Ink, true);
        sBig = OureaTheme.Text(26, Ink, true);
        sBigRed = OureaTheme.Text(26, OureaTheme.Bad, true);
        sBody = Text(12, Ink, FontStyle.Normal, true);
        sSmall = Text(11, Muted, FontStyle.Normal, true);
        sBtn = OureaTheme.Button(12);
        sBtnOn = OureaTheme.Primary(12);
        sDock = OureaTheme.Box(Rounded(new Color(0f, 0f, 0f, 0f), 10), 10, Ink, 11);
        sDock.hover.background = Rounded(new Color(1f, 1f, 1f, 0.07f), 10); sDock.hover.textColor = Ink;
        sDockOn = OureaTheme.Primary(11);
        sClose = OureaTheme.Box(Rounded(new Color(1f, 1f, 1f, 0.06f), 12), 12, Muted, 13);
        texTrack = Rounded(new Color(1f, 1f, 1f, 0.14f), 10);
        texFill = Rounded(Water, 10);
        texKnob = Rounded(Accent, 13);
        texDot = Rounded(Color.white, 8);
        sKnob = Text(12, OureaTheme.OnAccent, FontStyle.Bold);
        sKnob.alignment = TextAnchor.MiddleCenter;
        sChip = Box(OureaTheme.PanelTex, 14, Ink, 11, FontStyle.Bold);
        sChip.padding = new RectOffset(10, 10, 4, 4);
    }

    void Toggle(Card c)
    {
        bool closing = card == c;
        // leaving a mode card turns its mode off, so what's on the map always matches the open card
        if (card == Card.Photo && (closing || c != Card.Key)) compareWanted = false;
        if (card == Card.Radar && (closing || c != Card.Key)) radarWanted = false;
        card = closing ? Card.None : c;
        if (card == Card.Photo && !compareWanted) { compareWanted = true; wipeTarget = 0.5f; wipe = 0.08f; wipeVel = 0f; }
        if (card == Card.Radar) radarWanted = true;
    }

    void OnGUI()
    {
        GUI.depth = -10;   // dock and cards above the viewer's map labels
        if (viewer != null && viewer.uiFont != null) GUI.skin.font = viewer.uiFont;
        if (Event.current.type == EventType.Layout) uiRects.Clear();
        Styles();
        float s = Mathf.Clamp(Screen.height / 820f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float W = Screen.width / s, H = Screen.height / s;
        const float M = 16f;

        curScale = s;
        DrawWipe(W, H, s);
        DrawFloodCounter(W);

        // --- dock
        string[] labels = { "WATER", "PHOTO", "RADAR", "TOP 10", "KEY" };
        Card[] cards = { Card.Water, Card.Photo, Card.Radar, Card.Ranking, Card.Key };
        const float bw = 64f, bh = 46f, gap = 6f, pad = 8f;
        float dh = pad * 2 + cards.Length * bh + (cards.Length - 1) * gap;
        var dock = new Rect(W - M - bw - pad * 2, Mathf.Max(70f, H * 0.5f - dh * 0.5f - 30f), bw + pad * 2, dh);
        GUI.Box(dock, GUIContent.none, sPanel);
        Block(dock, s);
        for (int i = 0; i < cards.Length; i++)
        {
            var r = new Rect(dock.x + pad, dock.y + pad + i * (bh + gap), bw, bh);
            if (GUI.Button(r, labels[i], card == cards[i] ? sDockOn : sDock)) Toggle(cards[i]);
            // a small dot when a mode stays on behind another card (e.g. water raised while reading the key)
            bool live = (cards[i] == Card.Water && level > minM + 0.02f) || (cards[i] == Card.Photo && compareWanted) || (cards[i] == Card.Radar && radarWanted);
            if (live && card != cards[i])
            {
                var old = GUI.color; GUI.color = KeyFlood;
                GUI.DrawTexture(new Rect(r.xMax - 10, r.y + 6, 7, 7), texDot);
                GUI.color = old;
            }
        }

        // --- the open card, beside the dock
        if (card != Card.None)
        {
            const float cw = 300f;
            float maxH = H - M - 96f - 12f - M;   // above the timeline pill
            float ch = CardHeight(card, maxH);
            float cy = Mathf.Clamp(dock.y, M, H - M - 96f - 12f - ch);
            var c = new Rect(dock.x - 12f - cw, cy, cw, ch);
            GUI.Box(c, GUIContent.none, sPanel);
            Block(c, s);
            if (GUI.Button(new Rect(c.xMax - 36, c.y + 10, 24, 24), "x", sClose)) Toggle(card);
            switch (card)
            {
                case Card.Water: DrawWaterCard(c); break;
                case Card.Photo: DrawPhotoCard(c); break;
                case Card.Radar: DrawRadarCard(c); break;
                case Card.Ranking: DrawRankingCard(c); break;
                case Card.Key: DrawKeyCard(c); break;
            }
        }
        GUI.matrix = Matrix4x4.identity;
    }

    void Block(Rect r, float s) { uiRects.Add(new Rect(r.x * s, r.y * s, r.width * s, r.height * s)); }

    float CardHeight(Card c, float maxH)
    {
        switch (c)
        {
            case Card.Water: return Mathf.Min(maxH, 350f);
            case Card.Photo: return 170f;
            case Card.Radar: return 224f;
            case Card.Ranking: return Mathf.Min(maxH, 640f);
            default: return Mathf.Min(maxH, 368f);
        }
    }

    void DrawWaterCard(Rect p)
    {
        float x = p.x + 18, y = p.y + 16, w = p.width - 36;
        GUI.Label(new Rect(x, y + 2, 160, 20), "Raise the water", sTitle);
        GUI.Label(new Rect(p.xMax - 112, y + 4, 64, 16), "MODELED", sTag);
        y += 38;

        var track = new Rect(x + 6, y + 8, 12, 150);
        float lo = minM, hi = maxM;
        float tLevel = Mathf.InverseLerp(lo, hi, Mathf.Clamp(level, lo, hi));
        GUI.DrawTexture(track, texTrack);
        var fill = new Rect(track.x, track.yMax - track.height * tLevel, track.width, track.height * tLevel);
        if (fill.height > 1f) GUI.DrawTexture(fill, texFill);
        float squash = 1f + Mathf.Clamp(velocity * 0.08f, -0.25f, 0.25f);   // the knob squishes with speed
        var knob = new Rect(track.center.x - 14f * squash, track.yMax - track.height * tLevel - 14f / squash, 28f * squash, 28f / squash);
        GUI.DrawTexture(new Rect(knob.x - 1, knob.y - 1, knob.width + 2, knob.height + 2), texTrack);
        GUI.DrawTexture(knob, texKnob);

        var e = Event.current;
        var hit = new Rect(track.x - 18, track.y - 16, track.width + 36, track.height + 32);
        if (e.type == EventType.MouseDown && hit.Contains(e.mousePosition)) { dragging = true; e.Use(); }
        if (dragging && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown || e.type == EventType.MouseUp))
        {
            target = Mathf.Lerp(lo, hi, Mathf.Clamp01(Mathf.InverseLerp(track.yMax, track.y, e.mousePosition.y)));
            if (e.type == EventType.MouseUp) dragging = false;
            e.Use();
        }

        float rx = track.xMax + 26, rw = p.xMax - 18 - rx;
        bool any = level > minM + 0.02f;
        float frac = any ? FractionBelow(level) : 0f;
        GUI.Label(new Rect(rx, y, rw, 32), (any ? level : 0f).ToString("0.0") + " m", sBig);
        GUI.Label(new Rect(rx, y + 32, rw, 18), "water level above sea level", sSmall);
        GUI.Label(new Rect(rx, y + 66, rw, 32), any ? (frac * tileAreaKm2).ToString("#,0") + " km²" : "0 km²", sBig);
        GUI.Label(new Rect(rx, y + 98, rw, 30), any ? (frac * 100f).ToString("0") + "% of the map is below it" : "Drag the handle up", sSmall);
        if (any && buildings != null && buildings.builtAreaHa > 0f)
        {
            float bha = buildings.BuiltAreaBelow(level);
            GUI.Label(new Rect(rx, y + 120, rw, 30), "<b>" + (bha / buildings.builtAreaHa * 100f).ToString("0") + "%</b> of built-up land is below it", sSmall);
        }

        y = track.yMax + 22;
        if (viewer != null && observedWaterPct != null && observedWaterPct.Length > viewer.currentPass && viewer.passes.Count > viewer.currentPass)
        {
            float pct = observedWaterPct[viewer.currentPass];
            float match = LevelForFraction(pct / 100f);
            if (GUI.Button(new Rect(x, y, w * 0.62f, 30), "Match NISAR: " + match.ToString("0.0") + " m", sBtn)) target = match;
            if (GUI.Button(new Rect(x + w * 0.62f + 8, y, w * 0.38f - 8, 30), "Drain", sBtn)) target = minM - 0.5f;
            y += 38;
            GUI.Label(new Rect(x, y, w, 34), "NISAR saw " + pct.ToString("0") + "% of the map as water on " + viewer.passes[viewer.currentPass].date + ".", sSmall);
            y += 30;
        }
        GUI.Label(new Rect(x, y, w, 40), "A flat water level over " + (groundSource != null && groundSource.StartsWith("FABDEM") ? "bare-earth ground (FABDEM)" : "the elevation map") + ". Ignores dikes and flow. Not a forecast.", sSmall);
    }

    void DrawRankingCard(Rect p)
    {
        // two sources: hamlet scores built into the app (Mitch) and the live commune ranking (Asher's API)
        var tabs = new Rect(p.x + 14, p.y + 12, p.width - 64, 30);
        float tw = tabs.width / 2f;
        if (GUI.Button(new Rect(tabs.x, tabs.y, tw - 3, tabs.height), "Hamlets", showCommunes ? sBtn : sBtnOn)) showCommunes = false;
        if (GUI.Button(new Rect(tabs.x + tw + 3, tabs.y, tw - 3, tabs.height), "Communes", showCommunes ? sBtnOn : sBtn)) showCommunes = true;
        var body = new Rect(p.x, tabs.yMax + 2, p.width, p.yMax - tabs.yMax - 2);
        if (!showCommunes) { if (viewer != null) viewer.DrawRanking(body); return; }

        float x = body.x + 18, y = body.y + 12, w = body.width - 36;
        if (live == null) { GUI.Label(new Rect(x, y, w, 40), "No commune data in this build.", sBody); return; }
        if (live.state == DataLoader.State.Live && live.top.Length > 0)
        {
            GUI.Label(new Rect(x, y, w, 14), "LIVE  ·  OUREA API", sEyebrow); y += 20;
            GUI.Label(new Rect(x, y, w, 30), live.message, sSmall); y += 32;
            var rows = new Row[live.top.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var cm = live.top[i];
                string why = cm.reasons ?? "";
                if (cm.gap_status != null && cm.gap_status != "known" && !why.Contains("childcare gap")) why += " · childcare gap not yet known";
                rows[i] = new Row { rank = cm.rank, name = string.IsNullOrEmpty(cm.name_en) ? cm.name : cm.name_en, score = cm.score, why = why,
                                    at = boundsM != null ? DataLoader.ToWorld(cm.lon, cm.lat, boundsM, sizeX, sizeZ) : Vector3.zero };
            }
            DrawCommuneRows(body, y, rows);
            return;
        }

        // no pass in the API yet: Mitch's commune scores for the pass on the timeline
        string date = viewer != null && viewer.passes.Count > viewer.currentPass ? viewer.passes[viewer.currentPass].date : "";
        GUI.Label(new Rect(x, y, w, 14), date + " PASS  ·  PROVISIONAL", sEyebrow); y += 20;
        GUI.Label(new Rect(x, y, w, 30), live.message, sSmall); y += 32;
        var list = new List<Row>();
        foreach (var b in live.baked)
            if (b.pass == date)
                list.Add(new Row { rank = b.rank, name = string.IsNullOrEmpty(b.name_en) ? b.name : b.name_en, score = b.score,
                                   why = b.reasons + " · childcare gap not yet known", at = new Vector3(b.u * sizeX, 0f, (1f - b.v) * sizeZ) });
        if (list.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 46), "No flood water yet on the " + date + " pass: it is the baseline. Move the timeline to a later pass.", sBody);
            return;
        }
        DrawCommuneRows(body, y, list.ToArray());
    }

    struct Row { public int rank; public string name, why; public float score; public Vector3 at; }

    void DrawCommuneRows(Rect body, float y, Row[] rows)
    {
        var view = new Rect(body.x + 8, y, body.width - 16, body.yMax - y - 8);
        const float rowH = 64f;
        communeScroll = GUI.BeginScrollView(view, communeScroll, new Rect(0, 0, view.width - 4, rows.Length * rowH), false, false, GUIStyle.none, GUIStyle.none);
        for (int i = 0; i < rows.Length; i++)
        {
            var cm = rows[i];
            var row = new Rect(0, i * rowH, view.width - 4, rowH - 4);
            if (GUI.Button(row, GUIContent.none, sDock) && viewer != null && viewer.orbit != null)
                viewer.orbit.Focus(cm.at, 120f);
            GUI.Label(new Rect(row.x + 10, row.y + 6, row.width - 70, 18), "<b>" + cm.rank + ".  " + cm.name + "</b>", sBody);
            GUI.Label(new Rect(row.xMax - 64, row.y + 6, 56, 18), "<b>" + cm.score.ToString("0") + "</b>", sBody);
            GUI.Label(new Rect(row.x + 10, row.y + 24, row.width - 20, 34), cm.why, sSmall);
        }
        GUI.EndScrollView();
    }

    void DrawPhotoCard(Rect p)
    {
        float x = p.x + 18, y = p.y + 16, w = p.width - 36;
        GUI.Label(new Rect(x, y + 2, w - 40, 20), "Camera vs radar", sTitle);
        y += 34;
        int pi = viewer != null ? viewer.currentPass : 0;
        string d = opticalDate != null && opticalDate.Length > pi ? opticalDate[pi] : "";
        string c = opticalCloud != null && opticalCloud.Length > pi ? opticalCloud[pi].ToString("0") + "% cloud" : "";
        GUI.Label(new Rect(x, y, w, 36), "Drag the white line. Left: Sentinel-2 photo from " + d + " (" + c + "). Right: NISAR radar water.", sBody);
        y += 50;
        GUI.Label(new Rect(x, y, w, 50), "Monsoon cloud hides the ground from cameras. Radar sees through it. Step back to 11 Jul to see the cloudiest day.", sSmall);
    }

    void DrawRadarCard(Rect p)
    {
        float x = p.x + 18, y = p.y + 16, w = p.width - 36;
        GUI.Label(new Rect(x, y + 2, w - 40, 20), "What the radar saw", sTitle);
        y += 34;
        GUI.Label(new Rect(x, y, w, 36), "The real NISAR image for this pass (HV backscatter). Smooth water bounces the signal away; plants and rough ground send it back.", sBody);
        y += 60;
        Swatch(x, y, Lin(14, 20, 30), "Dark: smooth open water"); y += 24;
        Swatch(x, y, Lin(205, 230, 222), "Bright: plants, rough ground"); y += 30;
        GUI.Label(new Rect(x, y, w, 30), "NISAR data is provisional.", sSmall);
    }

    void DrawKeyCard(Rect p)
    {
        float x = p.x + 18, y = p.y + 16, w = p.width - 36;
        GUI.Label(new Rect(x, y + 2, w - 40, 20), "Key", sTitle);
        y += 34;
        GUI.Label(new Rect(x, y, w, 14), "ON THE MAP NOW", sEyebrow); y += 20;
        if (radarWanted && radarOn > 0.5f)
        {
            Swatch(x, y, Lin(14, 20, 30), "Radar: dark = open water"); y += 22;
            Swatch(x, y, Lin(205, 230, 222), "Radar: bright = plants, rough ground"); y += 22;
        }
        else
        {
            Swatch(x, y, KeyRiver, "River and canals (water on 11 Jul)"); y += 22;
            Swatch(x, y, KeyFlood, "Flood water, new since 11 Jul"); y += 22;
        }
        if (compareWanted) { Swatch(x, y, Color.white, "Left of the line: camera photo"); y += 22; }
        if (level > minM + 0.02f)
        {
            Swatch(x, y, KeySheet, "Modeled flat water level"); y += 22;
            Swatch(x, y, KeyWetBuilding, "Buildings with ground under it"); y += 22;
        }
        bool sim = viewer != null && viewer.simulated;
        GradientSwatch(x, y, sim ? "Hamlets ranked 1 to 10 (simulated)" : "Check first, ranked 1 to 10"); y += 22;
        Swatch(x, y, KeySchool, sim ? "School (simulated)" : "School (OpenStreetMap)"); y += 30;

        GUI.Label(new Rect(x, y, w, 14), "CONTROLS", sEyebrow); y += 20;
        GUI.Label(new Rect(x, y, w, 52), "Drag to rotate · scroll to zoom · WASD to move\n[ and ] step passes · Space plays them", sSmall); y += 46;
        if (GUI.Button(new Rect(x, y, w, 28), "Reset the view", sBtn) && viewer != null && viewer.orbit != null) viewer.orbit.ResetView();
        y += 40;
        GUI.Label(new Rect(x, y, w, 14), "SOURCES", sEyebrow); y += 20;
        GUI.Label(new Rect(x, y, w, 76), "Water and radar: NISAR (NASA/ISRO), provisional. Children: WorldPop 2026. Schools: OpenStreetMap. Elevation: Copernicus GLO-30. Imagery: Sentinel-2 cloudless 2024 by EOX, s2maps.eu.", sSmall);
    }

    void Swatch(float x, float y, Color c, string text)
    {
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.25f);
        GUI.DrawTexture(new Rect(x - 1, y + 1, 16, 16), texDot);
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y + 2, 14, 14), texDot);
        GUI.color = old;
        GUI.Label(new Rect(x + 24, y + 1, 240, 18), text, sBody);
    }

    void GradientSwatch(float x, float y, string text)
    {
        var old = GUI.color;
        GUI.color = KeyTop1;  GUI.DrawTexture(new Rect(x, y + 2, 9, 14), texDot);
        GUI.color = KeyTop10; GUI.DrawTexture(new Rect(x + 6, y + 2, 9, 14), texDot);
        GUI.color = old;
        GUI.Label(new Rect(x + 24, y + 1, 240, 18), text, sBody);
    }
}
