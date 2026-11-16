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
    // "as NISAR saw it": the village fly-in plays the real passes, July to October, instead of a
    // modeled level; houses turn red where new flood water is within ~100 m (Shaders/OureaBuilding.shader)
    bool observed; float obsT;
    const float ObsDelay = 2.0f, ObsPlay = 10f;

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

        if (observed && viewer != null && viewer.timeline != null && viewer.passes.Count > 1)
        {
            if (dragging) observed = false;
            else if (obsT < ObsDelay + ObsPlay)
            {
                obsT += dt;
                float play = Mathf.Clamp01((obsT - ObsDelay) / ObsPlay);
                viewer.timeline.SetPosition(play * play * (3f - 2f * play) * (viewer.passes.Count - 1));
            }
        }
        if (deltaMat != null)
        {
            Shader.SetGlobalFloat("_OureaObserved", observed ? 1f : 0f);
            Shader.SetGlobalTexture("_OureaMaskA", deltaMat.GetTexture("_MaskA"));
            Shader.SetGlobalTexture("_OureaMaskB", deltaMat.GetTexture("_MaskB"));
            Shader.SetGlobalTexture("_OureaMaskBase", deltaMat.GetTexture("_MaskBase"));
            Shader.SetGlobalFloat("_OureaBlend", deltaMat.GetFloat("_Blend"));
            Shader.SetGlobalVector("_OureaTile", new Vector4(sizeX, sizeZ, 0, 0));
        }

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
        float top = M, bottom = OureaTheme.Phone ? OureaTheme.PhoneDock(W, H).y - 116f : H - M - 72f - 10f;   // stops above the timeline pill
        float lx = wipe * W;
        var old = GUI.color;
        GUI.color = OureaTheme.A(new Color(1f, 1f, 1f, compareOn));
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
    public void Drain() { story = false; observed = false; target = minM - 0.5f; }
    public void CloseCard() { if (card != Card.None) Toggle(card); }
    public void OpenPhoto() { if (card != Card.Photo) Toggle(Card.Photo); }
    public void OpenRadar() { if (card != Card.Radar) Toggle(Card.Radar); }
    public void OpenCommunes() { if (card != Card.Ranking) Toggle(Card.Ranking); showCommunes = true; }
    public float CloudPct(int pass) { return opticalCloud != null && pass < opticalCloud.Length ? opticalCloud[pass] : -1f; }
    public float WaterPct(int pass) { return observedWaterPct != null && pass < observedWaterPct.Length ? observedWaterPct[pass] : -1f; }
    public float BuiltFloodedHa() { return buildings != null ? buildings.BuiltAreaBelow(level) : 0f; }

    void StartStory()
    {
        Vector3 spot;
        if (buildings != null && buildings.BestFloodSpot(MatchLevel(), out spot) && viewer != null && viewer.orbit != null)
            viewer.orbit.Focus(spot, 30f, 33f);
        story = false; target = level = minM - 0.5f; velocity = 0f;   // no modeled water: this is what the radar saw
        observed = true; obsT = 0f;
        if (viewer != null && viewer.timeline != null)
        {
            if (viewer.timeline.Playing) viewer.timeline.TogglePlay();
            viewer.timeline.SetPosition(0f);
        }
        if (card == Card.Photo || card == Card.Radar) Toggle(card);   // plain map while it floods
    }

    // phone: readouts go full width under the title and the story buttons
    static Rect PhoneTop(float W) { return new Rect(16f, 16f + 58f + 8f + 40f + 8f, W - 32f, 96f); }

    // A big readout at the top while modeled water is up: depth and how much village land is under it.
    void DrawFloodCounter(float W)
    {
        bool any = level > minM + 0.02f;
        float x0 = 16f + 268f + 12f;
        if (observed) { DrawObservedCounter(W, x0); return; }
        if (!any)
        {
            if (guided || SheetOpen) return;
            var b = new Rect(16f, 16f + 58f + 10f + 48f, 268f, 40f);
            Block(b, curScale);
            if (GUI.Button(b, "WATCH A VILLAGE FLOOD  >", sBtnOn)) StartStory();
            return;
        }
        float cw = Mathf.Min(460f, W - x0 - 16f - 120f);
        var r = OureaTheme.Phone ? PhoneTop(W) : new Rect(Mathf.Max(x0, W * 0.5f - cw * 0.5f), 16f, cw, 96f);
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

    // the counter while the real passes play: date, children near new water, new water area
    void DrawObservedCounter(float W, float x0)
    {
        if (viewer == null || viewer.passes.Count == 0 || viewer.timeline == null) return;
        float pos = viewer.timeline.Position;
        int a = Mathf.Clamp(Mathf.FloorToInt(pos), 0, viewer.passes.Count - 1), b = Mathf.Min(a + 1, viewer.passes.Count - 1);
        float f = pos - a;
        var pa = viewer.passes[a]; var pb = viewer.passes[b];
        float kids = Mathf.Lerp(pa.childrenNearWater, pb.childrenNearWater, f);
        float km2 = Mathf.Lerp(pa.newWaterKm2, pb.newWaterKm2, f);
        var pn = viewer.passes[Mathf.RoundToInt(pos)];
        float cw = Mathf.Min(480f, W - x0 - 16f - 120f);
        var r = OureaTheme.Phone ? PhoneTop(W) : new Rect(Mathf.Max(x0, W * 0.5f - cw * 0.5f), 16f, cw, 96f);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, curScale);
        float x = r.x + 18f, y = r.y + 12f;
        System.DateTime d;
        string date = System.DateTime.TryParse(pn.date, out d) ? d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture) : pn.date;
        GUI.Label(new Rect(x, y, 130, 34), date, sBig);
        GUI.Label(new Rect(x, y + 34, 130, 16), "NISAR pass", sSmall);
        float bx = x + 120f, bw = r.xMax - 18f - bx;
        GUI.Label(new Rect(bx, y, bw, 34), kids.ToString("#,0"), sBigRed);
        bool phone = OureaTheme.Phone;   // shorter lines, so nothing wraps into the next
        GUI.Label(new Rect(bx, y + 34, bw, 16), phone ? "under-5s near new water" : "under-5s within 100 m of new flood water", sSmall);
        GUI.Label(new Rect(x, y + 56, r.width - 36 - 64, 16), "+" + km2.ToString("#,0") + " km² new water since 11 Jul" + (phone ? "" : "  ·  NISAR, provisional"), sSmall);
        if (GUI.Button(new Rect(r.xMax - 70f, r.yMax - 32f, 56f, 22f), "Clear", sBtn)) observed = false;
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
    enum Card { None, Water, Photo, Radar, Ranking, Alerts, Key }
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
    static readonly Color KeyRiver = Lin(70, 58, 40);
    static readonly Color KeyFlood = Lin(176, 132, 84);
    static readonly Color KeySheet = Lin(150, 110, 70);
    static readonly Color KeyWetBuilding = Lin(219, 66, 46);
    static readonly Color KeyTop1 = Lin(219, 64, 52);
    static readonly Color KeyTop10 = Lin(255, 170, 60);
    static readonly Color KeySchool = Lin(64, 120, 245);

    float curScale = 1f;
    GUIStyle sBigRed, sField, sSms, sSmsHead, sSmsBox;
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
        sField = OureaTheme.Box(OureaTheme.Rounded(OureaTheme.S(255, 255, 255, 0.07f), 8, OureaTheme.GlassLine), 8, Ink, 14, false);
        sField.alignment = TextAnchor.MiddleLeft; sField.padding = new RectOffset(10, 10, 4, 4);
        sField.focused.background = OureaTheme.Rounded(OureaTheme.S(255, 255, 255, 0.10f), 8, Accent); sField.focused.textColor = Ink;
        sField.hover.background = sField.normal.background; sField.hover.textColor = Ink;
        sSmsBox = OureaTheme.Box(OureaTheme.Rounded(OureaTheme.S(111, 211, 143, 0.10f), 12, OureaTheme.S(111, 211, 143, 0.45f)), 12, Ink, 12, false);
        sSmsHead = OureaTheme.Text(10, OureaTheme.Good, true);
        sSms = OureaTheme.Text(13, Ink, false, FontStyle.Normal, TextAnchor.UpperLeft, true);
        sBody = Text(13, Ink, FontStyle.Normal, true);
        sSmall = Text(12, Muted, FontStyle.Normal, true);
        sBtn = OureaTheme.Button(12);
        sBtnOn = OureaTheme.Primary(12);
        sDock = OureaTheme.Box(Rounded(new Color(0f, 0f, 0f, 0f), 10), 10, Ink, 12);
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
        alertsFromStory = false;   // OpenAlerts sets it again when the story opens the card
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
        if (GlobeJourney.HideHud) return;
        Styles();
        GUI.color = OureaTheme.A(Color.white);
        float s = OureaTheme.UIScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float W = Screen.width / s, H = Screen.height / s;
        const float M = 16f;

        curScale = s;
        DrawWipe(W, H, s);
        DrawCommuneFocus(s);
        DrawFloodCounter(W);

        // --- dock: a column on the right; on a phone, a bar along the bottom
        string[] labels = { "WATER", "PHOTO", "RADAR", "TOP 10", "ALERTS", "KEY" };
        Card[] cards = { Card.Water, Card.Photo, Card.Radar, Card.Ranking, Card.Alerts, Card.Key };
        bool phone = OureaTheme.Phone;
        const float gap = 6f, pad = 8f;
        float bw = 64f, bh = 46f;
        Rect dock;
        if (phone)
        {
            dock = OureaTheme.PhoneDock(W, H);
            bw = (dock.width - pad * 2 - gap * (cards.Length - 1)) / cards.Length; bh = dock.height - pad * 2;
        }
        else
        {
            float dh = pad * 2 + cards.Length * bh + (cards.Length - 1) * gap;
            dock = new Rect(W - M - bw - pad * 2, Mathf.Max(70f, H * 0.5f - dh * 0.5f - 30f), bw + pad * 2, dh);
        }
        GUI.Box(dock, GUIContent.none, sPanel);
        Block(dock, s);
        for (int i = 0; i < cards.Length; i++)
        {
            var r = phone ? new Rect(dock.x + pad + i * (bw + gap), dock.y + pad, bw, bh)
                          : new Rect(dock.x + pad, dock.y + pad + i * (bh + gap), bw, bh);
            if (GUI.Button(r, labels[i], card == cards[i] ? sDockOn : sDock)) Toggle(cards[i]);
            // a small dot when a mode stays on behind another card (e.g. water raised while reading the key)
            bool live = (cards[i] == Card.Water && level > minM + 0.02f) || (cards[i] == Card.Photo && compareWanted) || (cards[i] == Card.Radar && radarWanted);
            if (live && card != cards[i])
            {
                var old = GUI.color; GUI.color = OureaTheme.A(KeyFlood);
                GUI.DrawTexture(new Rect(r.xMax - 10, r.y + 6, 7, 7), texDot);
                GUI.color = old;
            }
        }

        // --- the open card, beside the dock; on a phone, a sheet over the bottom of the screen
        SheetOpen = phone && card != Card.None;
        if (card != Card.None)
        {
            Rect c;
            if (phone)
            {
                // up to just under the title; during the story it stops above the guide's panel
                float bottom = StoryMode.Running && StoryMode.GuideTop > 0f ? StoryMode.GuideTop - 8f : dock.y - 8f;
                float maxH = bottom - (M + 58f + 8f);
                float ch = CardHeight(card, maxH);
                c = new Rect(M, bottom - ch, W - 2 * M, ch);
            }
            else
            {
                const float cw = 300f;
                float maxH = H - M - 96f - 12f - M;   // above the timeline pill
                float ch = CardHeight(card, maxH);
                float cy = Mathf.Clamp(dock.y, M, H - M - 96f - 12f - ch);
                c = new Rect(dock.x - 12f - cw, cy, cw, ch);
            }
            GUI.Box(c, GUIContent.none, sPanel);
            Block(c, s);
            if (GUI.Button(new Rect(c.xMax - 42, c.y + 9, 30, 30), "×", sClose)) Toggle(card);   // 30 px: the rounded texture is 28 px, smaller boxes draw seams
            switch (card)
            {
                case Card.Water: DrawWaterCard(c); break;
                case Card.Photo: DrawPhotoCard(c); break;
                case Card.Radar: DrawRadarCard(c); break;
                case Card.Ranking: DrawRankingCard(c); break;
                case Card.Key: DrawKeyCard(c); break;
                case Card.Alerts: DrawAlertsCard(c); break;
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
            case Card.Alerts: return Mathf.Min(maxH, 580f);
            default: return Mathf.Min(maxH, 560f);
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
            observed = false;   // the slider is the modeled "what if"
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
        if (!showCommunes)
        {
            if (viewer == null) return;
            // the hamlet list, and under it the walking routes to school on Google Maps (web/public/unity/routes/)
            viewer.DrawRanking(new Rect(body.x, body.y, body.width, body.height - 92f));
            // how sure the order is (handoff/findings/sensitivity.json, validation_baseline.json)
            GUI.Label(new Rect(body.x + 18f, body.yMax - 88f, body.width - 36f, 40f),
                      "Weights moved ±50%: 7 to 10 of these 10 stay. Ranks water new since 11 Jul; another baseline gives another list.", sSmall);
            var rb = new Rect(body.x + 18f, body.yMax - 44f, body.width - 36f, 32f);
            string sel = viewer.SelectedPlaceId;
            if (OureaLinks.Button(rb, string.IsNullOrEmpty(sel) ? "ROUTES TO DAY CARE & SCHOOL  >" : "ROUTES FROM THIS HAMLET  >", sBtnOn))
                OureaLinks.Routes(viewer.CurrentPassDate, sel);
            return;
        }

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
        const string what = "A commune (xã / phường) is the local government that acts on this list; hamlets (ấp, H1–H10) are villages inside it.";
        float wh = sSmall.CalcHeight(new GUIContent(what), w);
        GUI.Label(new Rect(x, y, w, wh + 2), what, sSmall); y += wh + 8;
        var list = new List<Row>();
        foreach (var b in live.baked)
            if (b.pass == date)
                list.Add(new Row { rank = b.rank, name = b.name, score = b.score, hotspots = b.hotspots,
                                   why = (string.IsNullOrEmpty(b.name_en) ? "" : b.name_en + " · ") + b.reasons + " · childcare gap not yet known", at = new Vector3(b.u * sizeX, 0f, (1f - b.v) * sizeZ) });
        if (list.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 46), "No flood water yet on the " + date + " pass: it is the baseline. Move the timeline to a later pass.", sBody);
            return;
        }
        DrawCommuneRows(body, y, list.ToArray());
    }

    struct Row { public int rank; public string name, why, hotspots; public float score; public Vector3 at; }

    // the commune the camera was last sent to, labelled on the map so its rank isn't read off the hamlet pins
    bool focusOn; Row focus;

    void DrawCommuneFocus(float s)
    {
        if (!focusOn || viewer == null || card != Card.Ranking || !showCommunes) return;
        var cam = Camera.main; if (cam == null) return;
        Vector3 sp = cam.WorldToScreenPoint(focus.at + Vector3.up * 0.5f);
        if (sp.z <= 0f) return;
        float x = sp.x / s, y = (Screen.height - sp.y) / s;
        string inside = "";
        if (!string.IsNullOrEmpty(focus.hotspots))
        {
            var ranks = new List<string>();
            foreach (var id in focus.hotspots.Split(',')) { int r = viewer.HamletRank(id); if (r > 0 && !ranks.Contains("H" + r)) ranks.Add("H" + r); }
            if (ranks.Count > 0) inside = "includes hamlets " + string.Join(", ", ranks.ToArray());
        }
        string title = "Commune #" + focus.rank + "  ·  " + focus.name;
        float tw = Mathf.Max(sTitle.CalcSize(new GUIContent(title)).x, sSmall.CalcSize(new GUIContent(inside)).x) + 28f;
        float th = inside.Length > 0 ? 50f : 32f;
        var r0 = new Rect(x - tw * 0.5f, y - th - 26f, tw, th);
        GUI.Box(r0, GUIContent.none, sPanel);
        GUI.Label(new Rect(r0.x + 14, r0.y + 7, tw - 28, 20), title, sTitle);
        if (inside.Length > 0) GUI.Label(new Rect(r0.x + 14, r0.y + 29, tw - 28, 16), inside, sSmall);
        var old = GUI.color; GUI.color = OureaTheme.A(Accent);
        GUI.DrawTexture(new Rect(x - 1, r0.yMax, 2, 20), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 5, y - 6, 10, 10), texDot);
        GUI.color = old;
    }

    void DrawCommuneRows(Rect body, float y, Row[] rows)
    {
        var view = new Rect(body.x + 8, y, body.width - 16, body.yMax - y - 8);
        // each row as tall as its reason text, with a gap before the next commune
        const float gapH = 12f;
        var whyH = new float[rows.Length];
        float total = 0f;
        for (int i = 0; i < rows.Length; i++) { whyH[i] = sSmall.CalcHeight(new GUIContent(rows[i].why), view.width - 24); total += 30 + whyH[i] + 8 + gapH; }
        communeScroll = OureaTheme.DragScroll(view, communeScroll, total + 8);
        communeScroll = GUI.BeginScrollView(view, communeScroll, new Rect(0, 0, view.width - 4, total + 8), false, false, GUIStyle.none, GUIStyle.none);
        float ry = 0f;
        for (int i = 0; i < rows.Length; i++)
        {
            var cm = rows[i];
            var row = new Rect(0, ry, view.width - 4, 30 + whyH[i] + 8);
            ry += row.height + gapH;
            if (GUI.Button(row, GUIContent.none, sDock) && viewer != null && viewer.orbit != null)
            {
                viewer.orbit.Focus(cm.at, 120f);
                focus = cm; focusOn = true;
            }
            GUI.Label(new Rect(row.x + 10, row.y + 6, row.width - 70, 18), "<b>" + cm.rank + ".  " + cm.name + "</b>", sBody);
            GUI.Label(new Rect(row.xMax - 64, row.y + 6, 56, 18), "<b>" + cm.score.ToString("0") + "</b>", sBody);
            GUI.Label(new Rect(row.x + 10, row.y + 28, row.width - 20, whyH[i] + 2), cm.why, sSmall);
        }
        GUI.EndScrollView();
    }

    // ---- flood texts: pick a commune, enter a number, sign up through Asher's Twilio endpoint ----
    public bool AlertsOpen { get { return card == Card.Alerts; } }
    public bool CardOpen { get { return card != Card.None; } }
    public static bool SheetOpen;
    public float OpenCardHeight { get { return card == Card.None ? 0f : CardHeight(card, 9999f); } }
    // the least a card can work in: the sign-up form needs all its fields, lists can scroll
    public float OpenCardMinHeight { get { return card == Card.Alerts ? 400f : 260f; } }   // a card is open as a phone sheet: the timeline steps aside

    bool alertsFromStory;   // opened from the story's checklist: offer the way back, and the routes

    public void OpenAlerts(string communeCode, bool fromStory = false)
    {
        if (card != Card.Alerts) Toggle(Card.Alerts);
        alertsFromStory = fromStory;
        var list = AlertCommunes();
        for (int i = 0; i < list.Count; i++) if (list[i].code == communeCode) alertPick = i;
    }

    int alertPick;
    string alertPhone = "+84 ";

    System.Collections.Generic.List<DataLoader.BakedCommune> AlertCommunes()
    {
        var list = new System.Collections.Generic.List<DataLoader.BakedCommune>();
        if (live == null || viewer == null || viewer.passes.Count == 0) return list;
        string date = viewer.passes[viewer.passes.Count - 1].date;   // the latest pass ranks who needs texts most
        foreach (var b in live.baked) if (b.pass == date && !string.IsNullOrEmpty(b.code)) list.Add(b);
        return list;
    }

    void DrawAlertsCard(Rect p)
    {
        float x = p.x + 18, y = p.y + 16, w = p.width - 36;
        GUI.Label(new Rect(x, y + 2, w - 40, 20), "Flood texts", sTitle);
        y += 26;
        float introH = sSmall.CalcHeight(new GUIContent("A text after each NISAR pass when new flood water is near homes in your commune. Sent by Twilio."), w);
        GUI.Label(new Rect(x, y, w, introH), "A text after each NISAR pass when new flood water is near homes in your commune. Sent by Twilio.", sSmall);
        y += introH + 12;

        var list = AlertCommunes();
        if (list.Count == 0) { GUI.Label(new Rect(x, y, w, 40), "No communes loaded in this build.", sBody); return; }
        alertPick = Mathf.Clamp(alertPick, 0, list.Count - 1);
        var cm = list[alertPick];
        GUI.Label(new Rect(x, y, w, 14), "COMMUNE", sEyebrow); y += 18;
        if (GUI.Button(new Rect(x, y, 30, 30), "<", sBtn)) alertPick = (alertPick + list.Count - 1) % list.Count;
        if (GUI.Button(new Rect(x + w - 30, y, 30, 30), ">", sBtn)) alertPick = (alertPick + 1) % list.Count;
        GUI.Label(new Rect(x + 38, y + 1, w - 76, 16), "<b>" + cm.name + "</b>", sBody);
        GUI.Label(new Rect(x + 38, y + 16, w - 76, 14), "#" + cm.rank + " this pass  ·  " + cm.province, sSmall);
        y += 42;

        GUI.Label(new Rect(x, y, w, 14), "PHONE (WITH COUNTRY CODE)", sEyebrow); y += 18;
        GUI.SetNextControlName("ourea-phone");
        alertPhone = GUI.TextField(new Rect(x, y, w, 30), alertPhone, 20, sField);
        y += 38;
        bool ok = DataLoader.ValidPhone(alertPhone);
        bool sending = live != null && live.subState == DataLoader.SubState.Sending;
        GUI.enabled = ok && !sending && live != null;
        if (GUI.Button(new Rect(x, y, w, 32), sending ? "SIGNING UP…" : "TEXT ME FLOOD ALERTS", sBtnOn)) live.Subscribe(alertPhone, cm.code);
        GUI.enabled = true;
        y += 38;
        string note = live != null && live.subState != DataLoader.SubState.Idle ? live.subMessage
                    : (ok ? "We only use your number for these texts." : "Start with + and the country code, e.g. +84 90 123 4567.");
        GUI.Label(new Rect(x, y, w, 30), note, sSmall);
        y += 36;

        // what the text looks like on the phone (left out when the card is too short for it)
        float byTop = p.yMax - 16 - 32 - 10;
        bool preview = byTop - y > 90f;
        if (preview) { GUI.Label(new Rect(x, y, w, 14), "WHAT YOU'LL GET", sEyebrow); y += 18; }
        string date = viewer.passes[viewer.passes.Count - 1].date;
        string sms = "Ourea: new flood water within 100 m of homes in " + cm.name + " on the " + date +
                     " NISAR pass (provisional). " + cm.under5Near.ToString("#,0") + " children under 5 live near it. Keep small children away from canals and flood water. Reply STOP to stop.";
        float bh = sSms.CalcHeight(new GUIContent(sms), w - 24) + 34;
        // the way on: the routes parents will be warned about, and back to the checklist
        float by = p.yMax - 16 - 32;
        if (alertsFromStory)
        {
            if (GUI.Button(new Rect(x, by, 92, 32), "<  BACK", sBtn)) { alertsFromStory = false; CloseCard(); }
            if (OureaLinks.Button(new Rect(x + 100, by, w - 100, 32), "ROUTES THAT FLOODED  >", sBtnOn)) OureaLinks.Routes(date, null);
        }
        else if (OureaLinks.Button(new Rect(x, by, w, 32), "ROUTES THAT FLOODED  >", sBtnOn)) OureaLinks.Routes(date, null);
        if (!preview) return;
        var bubble = new Rect(x, y, w, Mathf.Min(bh, by - 10 - y));
        GUI.Box(bubble, GUIContent.none, sSmsBox);
        GUI.Label(new Rect(bubble.x + 12, bubble.y + 8, bubble.width - 24, 14), "OUREA ALERTS  ·  SMS", sSmsHead);
        GUI.Label(new Rect(bubble.x + 12, bubble.y + 26, bubble.width - 24, bubble.height - 30), sms, sSms);
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

    Vector2 keyScroll;
    float keyH = 600f;

    // Every line measured, so wrapped text never runs into the next; scrolls when the window is short.
    void DrawKeyCard(Rect p)
    {
        GUI.Label(new Rect(p.x + 18, p.y + 18, p.width - 76, 20), "Key", sTitle);
        var view = new Rect(p.x + 4, p.y + 50, p.width - 8, p.height - 58);
        keyScroll = OureaTheme.DragScroll(view, keyScroll, keyH);
        keyScroll = GUI.BeginScrollView(view, keyScroll, new Rect(0, 0, view.width - 4, keyH), false, false, GUIStyle.none, GUIStyle.none);
        float x = 14, y = 0, w = view.width - 32;
        keyW = w;
        GUI.Label(new Rect(x, y, w, 14), "ON THE MAP NOW", sEyebrow); y += 22;
        if (radarWanted && radarOn > 0.5f)
        {
            y += Swatch(x, y, Lin(14, 20, 30), "Radar: dark = open water");
            y += Swatch(x, y, Lin(205, 230, 222), "Radar: bright = plants, rough ground");
        }
        else
        {
            y += Swatch(x, y, KeyRiver, "River and canals (water on 11 Jul)");
            y += Swatch(x, y, KeyFlood, "Flood water, new since 11 Jul");
        }
        if (compareWanted) y += Swatch(x, y, Color.white, "Left of the line: camera photo");
        if (level > minM + 0.02f)
        {
            y += Swatch(x, y, KeySheet, "Modeled flat water level");
            y += Swatch(x, y, KeyWetBuilding, "Buildings with ground under it");
        }
        bool sim = viewer != null && viewer.simulated;
        y += GradientSwatch(x, y, sim ? "Hamlet beams H1 to H10 (simulated)" : "Hamlet beams: check first, H1 (red) to H10");
        y += Swatch(x, y, KeySchool, sim ? "School (simulated)" : "School (OpenStreetMap)");
        y += Swatch(x, y, RouteField.RiskColour(0.6f), "Walking route: white = little at risk, amber, red = most");
        y += 12;

        y = Section(x, y, w, "COMMUNE OR HAMLET?",
            "A commune (xã, or phường in town) is the local government: about 10,000–30,000 people, with its own People's Committee. " +
            "It is who acts on the list: sends staff, opens day care, texts parents.\n" +
            "A hamlet (ấp) is a village inside a commune, a few hundred homes. Hamlets are numbered H1–H10 on the map; communes are ranked in TOP 10 → Communes.");
        y = Section(x, y, w, "CONTROLS", "Drag to rotate · scroll to zoom · WASD to move\n[ and ] step passes · Space plays them");
        if (GUI.Button(new Rect(x, y, w, 28), "Reset the view", sBtn) && viewer != null && viewer.orbit != null) viewer.orbit.ResetView();
        y += 42;
        y = Section(x, y, w, "SOURCES", "Water and radar: NISAR (NASA/ISRO), provisional. Children: WorldPop 2026. Schools and roads: OpenStreetMap. Ground: FABDEM. Imagery: Sentinel-2 cloudless 2024 by EOX, s2maps.eu.");
        keyH = y + 4;
        GUI.EndScrollView();
    }

    float Section(float x, float y, float w, string head, string body)
    {
        GUI.Label(new Rect(x, y, w, 14), head, sEyebrow); y += 20;
        float h = sSmall.CalcHeight(new GUIContent(body), w);
        GUI.Label(new Rect(x, y, w, h + 2), body, sSmall);
        return y + h + 16;
    }

    float keyW = 240f;

    float Swatch(float x, float y, Color c, string text)
    {
        var old = GUI.color;
        GUI.color = OureaTheme.A(new Color(0f, 0f, 0f, 0.25f));
        GUI.DrawTexture(new Rect(x - 1, y + 1, 16, 16), texDot);
        GUI.color = OureaTheme.A(c);
        GUI.DrawTexture(new Rect(x, y + 2, 14, 14), texDot);
        GUI.color = old;
        float h = Mathf.Max(18f, sBody.CalcHeight(new GUIContent(text), keyW - 24));
        GUI.Label(new Rect(x + 24, y + 1, keyW - 24, h + 2), text, sBody);
        return h + 6;
    }

    float GradientSwatch(float x, float y, string text)
    {
        var old = GUI.color;
        GUI.color = OureaTheme.A(KeyTop1);  GUI.DrawTexture(new Rect(x, y + 2, 9, 14), texDot);
        GUI.color = OureaTheme.A(KeyTop10); GUI.DrawTexture(new Rect(x + 6, y + 2, 9, 14), texDot);
        GUI.color = old;
        float h = Mathf.Max(18f, sBody.CalcHeight(new GUIContent(text), keyW - 24));
        GUI.Label(new Rect(x + 24, y + 1, keyW - 24, h + 2), text, sBody);
        return h + 6;
    }
}
