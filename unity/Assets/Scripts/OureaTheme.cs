using UnityEngine;

// One look for every on-screen panel, taken from the first concept demo: dark glass over the map,
// a cyan accent, Chakra Petch for headings and buttons, Be Vietnam Pro for reading text.
// The project renders in linear space, so IMGUI colours are given linear: S() takes the colour
// you want to SEE (sRGB 0..255).
public static class OureaTheme
{
    public static Color S(int r, int g, int b, float a = 1f) { return new Color(Mathf.Pow(r / 255f, 2.2f), Mathf.Pow(g / 255f, 2.2f), Mathf.Pow(b / 255f, 2.2f), a); }

    public static readonly Color Glass = S(8, 14, 17, 0.92f);
    public static readonly Color GlassLine = S(200, 240, 240, 0.16f);
    public static readonly Color Ink = S(238, 245, 243);
    public static readonly Color Muted = S(178, 196, 192);
    public static readonly Color Accent = S(95, 212, 224);
    public static readonly Color OnAccent = S(3, 23, 26);
    public static readonly Color Warn = S(242, 181, 68);
    public static readonly Color Bad = S(255, 107, 87);
    public static readonly Color Good = S(111, 211, 143);

    public static Font display;

    // Phones held upright get their own layout: tools in a bar along the bottom, cards as sheets above it.
    public static bool Phone { get { return Screen.width < Screen.height * 0.85f; } }
    // IMGUI scale: the layout is ~820 units tall on a laptop, ~390 units wide on a phone
    public static float UIScale { get { return Mathf.Clamp(Phone ? Screen.width / 390f : Screen.height / 820f, 0.7f, 3.5f); } }
    public const float PhoneDockH = 56f;

    // IMGUI scroll views only scroll by scrollbar: add dragging (touch or mouse) and the wheel.
    // Call just before GUI.BeginScrollView with the same view rect. A drag longer than a few
    // pixels scrolls and cancels the tap, so rows inside still work as buttons.
    static bool dragOn; static Rect dragView; static Vector2 dragLast; static float dragMoved;
    public static Vector2 DragScroll(Rect view, Vector2 scroll, float contentH)
    {
        var e = Event.current;
        float max = Mathf.Max(0f, contentH - view.height);
        bool mine = dragOn && Mathf.Abs(dragView.y - view.y) < 1f && Mathf.Abs(dragView.x - view.x) < 1f;
        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0 && view.Contains(e.mousePosition)) { dragOn = true; dragView = view; dragLast = e.mousePosition; dragMoved = 0f; }
                break;
            case EventType.MouseDrag:
                if (mine)
                {
                    float dy = e.mousePosition.y - dragLast.y;
                    dragLast = e.mousePosition; dragMoved += Mathf.Abs(dy);
                    scroll.y -= dy;
                    if (dragMoved > 6f) { GUIUtility.hotControl = 0; e.Use(); }
                }
                break;
            case EventType.MouseUp:
                if (mine) { dragOn = false; if (dragMoved > 6f) e.Use(); }
                break;
            case EventType.ScrollWheel:
                if (view.Contains(e.mousePosition)) { scroll.y += e.delta.y * 40f; e.Use(); }
                break;
        }
        scroll.y = Mathf.Clamp(scroll.y, 0f, max);
        return scroll;
    }
    // the phone's bottom bar, in GUI units (W, H already divided by UIScale)
    public static Rect PhoneDock(float W, float H) { return new Rect(16f, H - 16f - PhoneDockH, W - 32f, PhoneDockH); }

    // the map's panels fade in after the fly-in lands instead of popping in (GlobeJourney sets it)
    public static float HudAlpha = 1f;
    public static Color A(Color c) { c.a *= HudAlpha; return c; }   // Chakra Petch SemiBold, set by OureaViewer

    // rounded rectangle with an optional 1 px border, drawn 9-sliced
    public static Texture2D Rounded(Color fill, int radius, Color border = default(Color))
    {
        int size = radius * 2 + 4;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(radius - (x + 0.5f), (x + 0.5f) - (size - radius)));
                float dy = Mathf.Max(0f, Mathf.Max(radius - (y + 0.5f), (y + 0.5f) - (size - radius)));
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float inside = Mathf.Clamp01(radius + 0.5f - d);
                // distance to the outer edge, for the border ring (straight edges included)
                float edge = Mathf.Min(Mathf.Min(x + 0.5f, size - x - 0.5f), Mathf.Min(y + 0.5f, size - y - 0.5f));
                float ring = (dx > 0f && dy > 0f) ? Mathf.Clamp01(1.2f - Mathf.Abs(radius - 0.6f - d)) : Mathf.Clamp01(1.6f - edge);
                Color c = border.a > 0f ? Color.Lerp(fill, new Color(border.r, border.g, border.b, Mathf.Max(border.a, fill.a)), ring * border.a / Mathf.Max(border.a, 0.001f)) : fill;
                c.a *= inside;
                t.SetPixel(x, y, c);
            }
        t.Apply();
        return t;
    }

    public static GUIStyle Text(int size, Color c, bool displayFace = false, FontStyle fs = FontStyle.Normal, TextAnchor anchor = TextAnchor.UpperLeft, bool wrap = false)
    {
        var st = new GUIStyle { fontSize = size, fontStyle = fs, alignment = anchor, wordWrap = wrap, richText = true, clipping = TextClipping.Clip };
        st.normal.textColor = c;
        if (displayFace && display != null) st.font = display;
        return st;
    }

    public static GUIStyle Box(Texture2D bg, int radius, Color textColor, int size = 12, bool displayFace = true, FontStyle fs = FontStyle.Normal)
    {
        var st = Text(size, textColor, displayFace, fs, TextAnchor.MiddleCenter);
        st.normal.background = bg;
        st.border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1);
        return st;
    }

    static Texture2D panel, button, buttonHover, primary, primaryHover;
    public static Texture2D PanelTex { get { if (panel == null) panel = Rounded(Glass, 14, GlassLine); return panel; } }

    // a quiet outline button and the bright cyan one, both in the display face, upper case
    public static GUIStyle Button(int size = 12)
    {
        if (button == null) { button = Rounded(S(255, 255, 255, 0.05f), 9, GlassLine); buttonHover = Rounded(S(255, 255, 255, 0.11f), 9, S(200, 240, 240, 0.30f)); }
        var st = Box(button, 9, Ink, size);
        st.hover.background = buttonHover; st.hover.textColor = Ink;
        return st;
    }

    public static GUIStyle Primary(int size = 13)
    {
        if (primary == null) { primary = Rounded(Accent, 10); primaryHover = Rounded(S(140, 230, 238), 10); }
        var st = Box(primary, 10, OnAccent, size);
        st.hover.background = primaryHover; st.hover.textColor = OnAccent;
        return st;
    }
}
