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
        return false;
    }

    readonly Dictionary<string, Place> placeById = new Dictionary<string, Place>();
    readonly List<Score> top = new List<Score>();
    MaterialPropertyBlock mpb;
    Material deltaMat;
    float uiScale = 1f;
    GUIStyle title, body, small, banner, rowStyle, pinLabel;

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
            if (s.rank >= 1 && s.rank <= 10) top.Add(s);
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
            var baseScale = p.type == "school" ? 4.5f : 6f;
            p.pin.localScale = Vector3.one * baseScale * size;
        }
    }

    void MakeStyles()
    {
        if (title != null) return;
        title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
        body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        banner = new GUIStyle(GUI.skin.box) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true, alignment = TextAnchor.MiddleCenter };
        banner.normal.textColor = new Color(1f, 0.85f, 0.3f);
        rowStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft, wordWrap = true };
        pinLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        pinLabel.normal.textColor = Color.white;
    }

    Rect Panel(Rect r)
    {
        GUI.Box(r, GUIContent.none);
        GUI.Box(r, GUIContent.none); // darker
        uiRects.Add(new Rect(r.x * uiScale, r.y * uiScale, r.width * uiScale, r.height * uiScale));
        return r;
    }

    void OnGUI()
    {
        if (Event.current.type == EventType.Layout) uiRects.Clear();
        MakeStyles();
        uiScale = Mathf.Max(1f, Screen.height / 900f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
        float W = Screen.width / uiScale, H = Screen.height / uiScale;

        // Top bar: title, pass slider, headline
        var bar = Panel(new Rect(10, 10, W - 20, simulated ? 118 : 92));
        GUI.Label(new Rect(bar.x + 12, bar.y + 6, 520, 28), "Ourea · Mekong Delta flood watch", title);
        if (passes.Count > 0)
        {
            var p = passes[currentPass];
            GUI.Label(new Rect(bar.x + 12, bar.y + 34, 520, 22),
                "NISAR pass " + p.date + "  (" + (currentPass + 1) + " of " + passes.Count + ")", body);
            string head = "New water since " + passes[0].date + ": " + p.newWaterKm2.ToString("0.0") + " km²";
            if (p.homesNearWater > 0) head += "  ·  homes near water: " + p.homesNearWater;
            if (p.childrenNearWater > 0) head += "  ·  children under 5 near water: " + p.childrenNearWater;
            GUI.Label(new Rect(bar.x + 12, bar.y + 56, W - 60, 22), head, body);

            float sx = Mathf.Max(bar.x + 540, W * 0.5f), sw = bar.xMax - sx - 160;
            if (GUI.Button(new Rect(sx, bar.y + 14, 40, 26), "◀")) timeline.Step(-1);
            if (passes.Count > 1)
            {
                float nv = GUI.HorizontalSlider(new Rect(sx + 50, bar.y + 22, sw, 20), timeline.Position, 0, passes.Count - 1);
                if (!Mathf.Approximately(nv, timeline.Position)) timeline.SetPosition(nv);
            }
            if (GUI.Button(new Rect(sx + 60 + sw, bar.y + 14, 40, 26), "▶")) timeline.Step(1);
            if (GUI.Button(new Rect(sx + 110 + sw, bar.y + 14, 40, 26), timeline.Playing ? "❚❚" : "▷")) timeline.TogglePlay();
            GUI.Label(new Rect(sx + 50, bar.y + 38, sw, 20), passes[0].date + "  →  " + passes[passes.Count - 1].date + "   ·   [ ] step · Space play", small);
        }
        if (simulated)
            GUI.Box(new Rect(bar.x + 8, bar.yMax - 34, bar.width - 16, 28),
                "SIMULATED DATA: placeholder water maps and places until the NISAR pipeline lands. " + dataNote, banner);

        // Check-first list
        float listTop = bar.yMax + 10;
        var list = Panel(new Rect(10, listTop, 330, Mathf.Min(H - listTop - 10, 74 + 10 * 46)));
        GUI.Label(new Rect(list.x + 12, list.y + 6, 300, 26), "Check first: top 10", title);
        GUI.Label(new Rect(list.x + 12, list.y + 34, 300, 22), "Least water is not \"safe\". Ranked for this pass.", small);
        float y = list.y + 60;
        foreach (var s in top)
        {
            Place pl;
            placeById.TryGetValue(s.placeId, out pl);
            string name = pl != null ? pl.name : s.placeId;
            string change = s.rankChange > 0 ? "  ▲" + s.rankChange : s.rankChange < 0 ? "  ▼" + (-s.rankChange) : "";
            string label = s.rank + ". " + name + change + "\n" + s.reason;
            if (GUI.Button(new Rect(list.x + 8, y, list.width - 16, 42), label, rowStyle) && pl != null && pl.pin != null && orbit != null)
                orbit.Focus(pl.pin.position, 90f);
            y += 46;
            if (y > list.yMax - 40) break;
        }
        if (top.Count == 0)
            GUI.Label(new Rect(list.x + 12, y, 300, 40), "No scores for this pass yet.", body);

        // Legend
        var leg = Panel(new Rect(W - 230, H - 150, 220, 140));
        GUI.Label(new Rect(leg.x + 10, leg.y + 6, 200, 22), "Legend", body);
        Swatch(leg.x + 12, leg.y + 34, new Color(0.16f, 0.43f, 0.75f), "Water · bright = new");
        Swatch(leg.x + 12, leg.y + 56, new Color(0.27f, 0.67f, 0.67f), "Rice flooded on purpose");
        Swatch(leg.x + 12, leg.y + 78, Top1, "Check-first (top 10)");
        Swatch(leg.x + 12, leg.y + 100, SchoolBlue, "School");
        GUI.Label(new Rect(leg.x + 10, leg.y + 118, 200, 20), "Drag: rotate · Scroll: zoom · WASD: move", small);

        // Rank numbers over the top-10 pins
        var cam = Camera.main;
        if (cam != null)
        {
            foreach (var s in top)
            {
                Place pl;
                if (!placeById.TryGetValue(s.placeId, out pl) || pl.pin == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(pl.pin.position + Vector3.up * 8f);
                if (sp.z < 0) continue;
                var r = new Rect(sp.x / uiScale - 15, (Screen.height - sp.y) / uiScale - 12, 30, 24);
                GUI.Label(r, s.rank.ToString(), pinLabel);
            }
        }
        GUI.matrix = Matrix4x4.identity;
    }

    void Swatch(float x, float y, Color c, string text)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y + 3, 16, 14), Texture2D.whiteTexture);
        GUI.color = old;
        GUI.Label(new Rect(x + 24, y, 180, 22), text, small);
    }
}
