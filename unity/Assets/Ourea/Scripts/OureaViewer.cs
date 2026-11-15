using System;
using System.Collections.Generic;
using UnityEngine;

// Runtime viewer: water map on the delta, check-first top 10, pins.
// TimelineController (Assets/Scripts) owns the current pass; the slider here drives it.
// All data is baked into this component by Ourea > Build Scene (Editor/OureaSceneBuilder.cs).
public class OureaViewer : MonoBehaviour
{
    [Serializable] public class Pass
    {
        public string date;
        public Texture2D mask; // landwater, see Shaders/OureaWater.shader
        public float newWaterKm2;
        public int homesNearWater;
        public int childrenNearWater;
    }

    [Serializable] public class Place
    {
        public string id;
        public string name;
        public string type; // hamlet | school | childcare
        public float u;
        public float v;
        public Transform pin;
        [NonSerialized] public float scale;
    }

    [Serializable] public class Score
    {
        public string placeId;
        public int pass;
        public float score;
        public int rank;
        public int rankChange;
        public string reason;
    }

    public Renderer deltaRenderer;
    public TimelineController timeline;
    public Font uiFont;        // Be Vietnam Pro: Unity's built-in web font has no Vietnamese letters (ợ, Ớ, ...)
    public Font displayFont;   // Chakra Petch: headings and buttons
    public OrbitCamera orbit;
    public bool simulated = true;
    public string dataNote = "";
    public List<Pass> passes = new List<Pass>();
    public List<Place> places = new List<Place>();
    public List<Score> scores = new List<Score>();
    public int currentPass;

    static readonly List<Rect> uiRects = new List<Rect>();
    public static bool PointerOverUI(Vector2 guiPoint)
    {
        for (int i = 0; i < uiRects.Count; i++)
            if (uiRects[i].Contains(guiPoint)) return true;
        return FloodLab.PointerOver(guiPoint) || StoryMode.PointerOver(guiPoint);
    }

    readonly Dictionary<string, Place> placeById = new Dictionary<string, Place>();
    readonly List<Score> top = new List<Score>();
    MaterialPropertyBlock mpb;
    Material deltaMat;
    float uiScale = 1f;

    static readonly Color Gray = new Color(0.85f, 0.85f, 0.82f);
    static readonly Color SchoolBlue = new Color(0.25f, 0.45f, 0.95f);
    static readonly Color Top1 = new Color(0.86f, 0.12f, 0.12f);
    static readonly Color Top10 = new Color(1f, 0.62f, 0.15f);

    void Start()
    {
        mpb = new MaterialPropertyBlock();
        if (deltaRenderer != null) deltaMat = deltaRenderer.material; // instance, so the asset is untouched
        foreach (var p in places)
            if (!string.IsNullOrEmpty(p.id)) placeById[p.id] = p;
        if (timeline == null) timeline = gameObject.AddComponent<TimelineController>();
        timeline.passCount = passes.Count;
        timeline.Blended += ApplyBlend;
        timeline.IndexChanged += ShowPass;
        timeline.SetPosition(passes.Count > 0 ? passes.Count - 1 : 0);
    }

    // A script recompile during Play mode re-creates this component without running Start
    // again (runtime state like the top-10 list and the timeline hooks is lost). Rewire.
    void Update()
    {
        if (mpb == null) Start();
    }

    void OnDestroy()
    {
        if (timeline == null) return;
        timeline.Blended -= ApplyBlend;
        timeline.IndexChanged -= ShowPass;
    }

    void ApplyBlend(int a, int b, float t)
    {
        if (deltaMat == null || passes.Count == 0) return;
        deltaMat.SetTexture("_MaskA", passes[a].mask);
        deltaMat.SetTexture("_MaskB", passes[b].mask);
        deltaMat.SetTexture("_MaskBase", passes[0].mask);
        deltaMat.SetFloat("_Blend", t);
    }

    void ShowPass(int i)
    {
        if (passes.Count == 0) return;
        currentPass = Mathf.Clamp(i, 0, passes.Count - 1);

        top.Clear();
        var rankById = new Dictionary<string, int>();
        foreach (var s in scores)
        {
            if (s.pass != currentPass) continue;
            rankById[s.placeId] = s.rank;
            if (s.rank >= 1 && s.rank <= 10 && s.score > 0f) top.Add(s);   // the baseline pass scores 0 everywhere: no ranking
        }
        top.Sort((a, b) => a.rank.CompareTo(b.rank));

        foreach (var p in places)
        {
            if (p.pin == null) continue;
            int rank;
            bool hasRank = rankById.TryGetValue(p.id, out rank);
            Color c = p.type == "school" ? SchoolBlue : Gray;
            float size = 1f;
            if (hasRank && rank <= 10)
            {
                c = Color.Lerp(Top1, Top10, (rank - 1) / 9f);
                size = 1.7f;
            }
            mpb.SetColor("_BaseColor", c);
            mpb.SetColor("_Color", c);
            var r = p.pin.GetComponent<Renderer>();
            if (r != null) r.SetPropertyBlock(mpb);
            var baseScale = p.type == "school" ? 2.2f : (hasRank && rank <= 10 ? 2.4f : 1.8f);   // ~200 m: reads as a marker, even zoomed in
            p.scale = baseScale * size;
            p.pin.localScale = Vector3.one * p.scale * PinShrink();
        }
    }

    // Pins are sized to read from the whole-map view; close in they'd be the size of a village,
    // so they shrink with the camera distance.
    float PinShrink() { return orbit != null ? Mathf.Clamp(orbit.distance / 260f, 0.2f, 1f) : 1f; }

    void LateUpdate()
    {
        float f = PinShrink();
        foreach (var p in places)
            if (p.pin != null && p.scale > 0f) p.pin.localScale = Vector3.one * p.scale * f;
    }

    // ---------------- UI ----------------
    // Flat dark cards with rounded corners, a big timeline along the bottom,
    // the check-first list on the left and a compact legend on the right.
    // dark glass and cyan, shared with every panel (Scripts/OureaTheme.cs)
    static readonly Color Ink = OureaTheme.Ink;
    static readonly Color Muted = OureaTheme.Muted;
    static readonly Color Accent = OureaTheme.Accent;
    static readonly Color Amber = OureaTheme.Warn;
    static readonly Color Up = new Color(0.80f, 0.27f, 0.22f);
    static readonly Color Down = new Color(0.20f, 0.55f, 0.32f);

    Texture2D texPanel, texRound, texRow, texRowHover, texRowSel;
    GUIStyle sPanel, sEyebrow, sTitle, sSub, sBody, sSmall, sBig, sBadge, sRowName, sRowReason, sTrend, sTick, sTickOn, sBtn, sPlay, sPill, sRow, sRowSel, sCard;
    Vector2 listScroll;
    string selectedId;
    bool draggingTrack;

    static Texture2D Rounded(Color c, int radius)
    {
        int size = radius * 2 + 4;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(radius - (x + 0.5f), (x + 0.5f) - (size - radius)));
                float dy = Mathf.Max(0f, Mathf.Max(radius - (y + 0.5f), (y + 0.5f) - (size - radius)));
                float a = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                t.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * a));
            }
        t.Apply();
        return t;
    }

    static GUIStyle Text(int size, Color color, FontStyle fs = FontStyle.Normal, TextAnchor anchor = TextAnchor.UpperLeft, bool wrap = false)
    {
        var st = new GUIStyle { fontSize = size, fontStyle = fs, alignment = anchor, wordWrap = wrap, richText = true, clipping = TextClipping.Clip };
        st.normal.textColor = color;
        return st;
    }

    static GUIStyle Box(Texture2D bg, int radius, Color textColor, int fontSize = 13, FontStyle fs = FontStyle.Normal, TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        var st = Text(fontSize, textColor, fs, anchor);
        st.normal.background = bg;
        st.border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1);
        return st;
    }

    void MakeStyles()
    {
        if (sPanel != null) return;
        texPanel = OureaTheme.PanelTex;
        texRound = Rounded(Color.white, 12); // drawn scaled, so it reads as a circle
        texRow = Rounded(new Color(1f, 1f, 1f, 0f), 8);
        texRowHover = Rounded(new Color(1f, 1f, 1f, 0.06f), 8);
        texRowSel = Rounded(new Color(Accent.r, Accent.g, Accent.b, 0.14f), 8);

        sPanel = Box(texPanel, 14, Ink);
        sEyebrow = OureaTheme.Text(11, Accent, true);
        sTitle = OureaTheme.Text(19, Ink, true);
        sSub = Text(12, Muted);
        sBody = Text(13, Ink);
        sSmall = Text(11, Muted, FontStyle.Normal, TextAnchor.UpperLeft, true);
        sBig = OureaTheme.Text(30, Ink, true);
        sBadge = Box(texRound, 10, Color.white, 12, FontStyle.Bold);
        sRowName = Text(14, Ink, FontStyle.Bold);
        sRowReason = Text(11, Muted);
        sTrend = Text(12, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
        sTick = Text(11, Muted, FontStyle.Normal, TextAnchor.UpperCenter);
        sTickOn = Text(12, Ink, FontStyle.Bold, TextAnchor.UpperCenter);
        sBtn = OureaTheme.Button();
        sPlay = OureaTheme.Box(Rounded(Accent, 20), 20, OureaTheme.OnAccent, 16);
        sPlay.hover.background = Rounded(OureaTheme.S(140, 230, 238), 20);
        sPlay.hover.textColor = OureaTheme.OnAccent;
        sPill = OureaTheme.Box(OureaTheme.Rounded(OureaTheme.S(8, 14, 17, 0.85f), 6, Amber), 6, Amber, 11);
        sPill.padding = new RectOffset(8, 8, 2, 2);
        sRow = Box(texRow, 8, Ink, 13, FontStyle.Normal, TextAnchor.MiddleLeft);
        sRow.hover.background = texRowHover;
        sRowSel = Box(texRowSel, 8, Ink, 13, FontStyle.Normal, TextAnchor.MiddleLeft);
        sCard = Box(OureaTheme.PanelTex, 14, Ink, 13, FontStyle.Bold);
        sCard.padding = new RectOffset(10, 10, 4, 4);
    }

    Rect Panel(Rect r)
    {
        GUI.Box(r, GUIContent.none, sPanel);
        uiRects.Add(new Rect(r.x * uiScale, r.y * uiScale, r.width * uiScale, r.height * uiScale));
        return r;
    }

    static string ShortDate(string iso, bool withYear = false)
    {
        DateTime d;
        if (DateTime.TryParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
            return d.ToString(withYear ? "d MMM yyyy" : "d MMM", System.Globalization.CultureInfo.InvariantCulture);
        return iso;
    }

    static string Hex(Color c) { return ColorUtility.ToHtmlStringRGB(c); }

    Color RankColor(int rank) { return Color.Lerp(Top1, Top10, (rank - 1) / 9f); }

    void Select(Place pl)
    {
        if (pl == null) return;
        selectedId = pl.id;
        if (orbit != null && pl.pin != null) orbit.Focus(pl.pin.position, 70f);
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.Layout) uiRects.Clear();
        if (GlobeJourney.HideHud) return;   // in space or in flight: the globe has its own labels
        if (uiFont != null) GUI.skin.font = uiFont;
        if (displayFont != null) OureaTheme.display = displayFont;
        MakeStyles();
        uiScale = Mathf.Clamp(Screen.height / 820f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
        float W = Screen.width / uiScale, H = Screen.height / uiScale;
        const float M = 16f;

        DrawMapLabels();

        // Quiet map: a small title, a small data note, the timeline pill. Everything else lives
        // behind the tool dock on the right (Scripts/FloodLab.cs).
        var head = Panel(new Rect(M, M, 268, 58));
        GUI.Label(new Rect(head.x + 14, head.y + 10, 240, 14), "O U R E A   ·   NISAR FLOOD WATCH", sEyebrow);
        GUI.Label(new Rect(head.x + 14, head.y + 26, 240, 24), "Mekong Delta, Vietnam", sTitle);

        string note = simulated ? "WATER: REAL NISAR  ·  RANKS SIMULATED" : "NISAR  ·  PROVISIONAL";
        var noteSize = sPill.CalcSize(new GUIContent(note));
        GUI.Label(new Rect(W - M - noteSize.x - 16, M + 6, noteSize.x + 16, 24), note, sPill);

        float pw = Mathf.Min(760f, W - 2 * M - 120f);
        var tl = Panel(new Rect((W - pw) * 0.5f, H - M - 72f, pw, 72f));
        DrawTimeline(tl);

        GUI.matrix = Matrix4x4.identity;
    }

    void DrawMapLabels()
    {
        var cam = Camera.main;
        if (cam == null) return;
        foreach (var s in top)
        {
            Place pl;
            if (!placeById.TryGetValue(s.placeId, out pl) || pl.pin == null) continue;
            Vector3 sp = cam.WorldToScreenPoint(pl.pin.position + Vector3.up * 7f);
            if (sp.z < 0) continue;
            float x = sp.x / uiScale, y = (Screen.height - sp.y) / uiScale;
            var old = GUI.color;
            GUI.color = RankColor(s.rank);
            GUI.DrawTexture(new Rect(x - 11, y - 22, 22, 22), texRound);
            GUI.color = old;
            if (GUI.Button(new Rect(x - 11, y - 22, 22, 22), GUIContent.none, GUIStyle.none)) Select(pl);
            GUI.color = old;
            GUI.Label(new Rect(x - 11, y - 22, 22, 22), s.rank.ToString(), Text(12, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (pl.id == selectedId)
            {
                var size = sCard.CalcSize(new GUIContent(pl.name));
                GUI.Label(new Rect(x - size.x / 2f, y - 52, size.x, 24), pl.name, sCard);
            }
        }
    }

    void DrawList(Rect list)
    {
        GUI.Label(new Rect(list.x + 16, list.y + 14, 300, 14), simulated ? "CHECK FIRST  ·  SIMULATED" : "CHECK FIRST", simulated ? Text(11, Amber, FontStyle.Bold) : sEyebrow);
        GUI.Label(new Rect(list.x + 16, list.y + 30, list.width - 60, 24), "Top 10 this pass", sTitle);
        GUI.Label(new Rect(list.x + 16, list.y + 56, 300, 16), simulated ? "Simulated until Mitch's scores land." : "Ranked by water near homes. Low rank is not \"safe\".", sSub);

        var view = new Rect(list.x + 8, list.y + 80, list.width - 16, list.height - 88);
        const float rowH = 54f;
        var content = new Rect(0, 0, view.width, Mathf.Max(view.height, top.Count * rowH));
        listScroll = GUI.BeginScrollView(view, listScroll, content, false, false, GUIStyle.none, GUIStyle.none);
        float y = 0;
        foreach (var s in top)
        {
            Place pl;
            placeById.TryGetValue(s.placeId, out pl);
            string name = pl != null ? pl.name : s.placeId;
            var row = new Rect(0, y, view.width, rowH - 4);
            bool sel = pl != null && pl.id == selectedId;
            if (GUI.Button(row, GUIContent.none, sel ? sRowSel : sRow)) Select(pl);

            var old = GUI.color;
            GUI.color = RankColor(s.rank);
            GUI.DrawTexture(new Rect(row.x + 10, row.y + 12, 26, 26), texRound);
            GUI.color = old;
            GUI.Label(new Rect(row.x + 10, row.y + 12, 26, 26), s.rank.ToString(), Text(13, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));

            GUI.Label(new Rect(row.x + 46, row.y + 8, row.width - 100, 18), name, sRowName);
            GUI.Label(new Rect(row.x + 46, row.y + 27, row.width - 56, 16), s.reason, sRowReason);

            if (s.rankChange != 0)
            {
                string t = s.rankChange > 0 ? "+" + s.rankChange : "-" + (-s.rankChange); // ▲▼ are missing from the WebGL font
                GUI.Label(new Rect(row.xMax - 60, row.y + 8, 50, 18), "<color=#" + Hex(s.rankChange > 0 ? Up : Down) + ">" + t + "</color>", sTrend);
            }
            y += rowH;
        }
        if (top.Count == 0) GUI.Label(new Rect(8, 8, view.width - 16, 60), currentPass == 0
            ? "No flood water yet. " + ShortDate(passes[0].date) + " is the baseline pass: flood water is water that was land on this date."
            : "No rankings for this pass yet.", Text(13, Ink, FontStyle.Normal, TextAnchor.UpperLeft, true));
        GUI.EndScrollView();
    }

    void DrawTimeline(Rect tl)
    {
        if (passes.Count == 0 || timeline == null)
        {
            GUI.Label(new Rect(tl.x + 20, tl.y + 26, 400, 20), "No radar passes loaded.", sBody);
            return;
        }
        var p = passes[currentPass];

        // play
        if (GUI.Button(new Rect(tl.x + 14, tl.y + 14, 44, 44), timeline.Playing ? "||" : ">", sPlay)) timeline.TogglePlay();

        // headline number on the right
        float nx = tl.xMax - 186;
        GUI.Label(new Rect(nx, tl.y + 12, 172, 30), "+" + p.newWaterKm2.ToString("#,0") + " km²", OureaTheme.Text(22, Ink, true, FontStyle.Normal, TextAnchor.UpperRight));
        string sub = p.childrenNearWater > 0
            ? p.childrenNearWater.ToString("#,0") + " under-5s within ~100 m"
            : "new flood water since " + ShortDate(passes[0].date);
        GUI.Label(new Rect(nx - 40, tl.y + 42, 212, 16), sub, Text(11, Muted, FontStyle.Normal, TextAnchor.UpperRight));

        // track with the passes
        float tx0 = tl.x + 84, tx1 = nx - 28;
        float trackY = tl.y + 30;
        var trackRect = new Rect(tx0, trackY - 3, tx1 - tx0, 6);
        int max = Mathf.Max(1, passes.Count - 1);
        float frac = passes.Count > 1 ? timeline.Position / max : 1f;
        var oldC = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(trackRect, Texture2D.whiteTexture);
        GUI.color = Accent;
        GUI.DrawTexture(new Rect(trackRect.x, trackRect.y, trackRect.width * frac, trackRect.height), Texture2D.whiteTexture);
        for (int i = 0; i < passes.Count; i++)
        {
            float x = passes.Count > 1 ? Mathf.Lerp(tx0, tx1, i / (float)max) : tx1;
            bool on = i == currentPass;
            float d = on ? 18f : 12f;
            GUI.color = i <= timeline.Position + 0.001f ? Accent : OureaTheme.S(70, 84, 88);
            GUI.DrawTexture(new Rect(x - d / 2f, trackY - d / 2f, d, d), texRound);
            GUI.color = oldC;
            if (GUI.Button(new Rect(x - 34, trackY + 12, 68, 18), ShortDate(passes[i].date), on ? sTickOn : sTick))
                timeline.Step(i - timeline.Index);
        }
        GUI.color = oldC;

        // drag along the track to scrub (blends between passes)
        var hit = new Rect(tx0 - 10, trackY - 14, (tx1 - tx0) + 20, 24);
        var e = Event.current;
        if (e.type == EventType.MouseDown && hit.Contains(e.mousePosition)) { draggingTrack = true; e.Use(); }
        if (e.type == EventType.MouseUp) draggingTrack = false;
        if (draggingTrack && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
        {
            float t = Mathf.InverseLerp(tx0, tx1, e.mousePosition.x);
            timeline.SetPosition(t * max);
            e.Use();
        }
    }

    // The full top-10 list, drawn inside the dock's "Top 10" card (FloodLab).
    public void DrawRanking(Rect card)
    {
        MakeStyles();
        DrawList(new Rect(card.x, card.y + 2, card.width, card.height - 4));
    }

    void Swatch(float x, float y, Color c, string text)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y + 2, 14, 14), texRound);
        GUI.color = old;
        GUI.Label(new Rect(x + 24, y + 1, 170, 18), text, sBody);
    }
}
