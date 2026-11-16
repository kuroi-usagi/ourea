using System.Runtime.InteropServices;
using UnityEngine;

// The story: an opening screen that says what this map is for, then a guided walk through the
// three NISAR passes with a fictional guide, ending at the "check first" list and what people do
// with it. Every number comes from the team's findings (handoff/findings, baked in by
// Editor/OureaLook.cs); the guide and her town are fictional and labelled so.
public class StoryMode : MonoBehaviour
{
    public OureaViewer viewer;
    public FloodLab lab;
    public GlobeJourney journey;   // the globe and the fly-in; the opening screen sits over the globe
    public float childrenInTile = 81100f;
    public int schoolsNear = 18, schoolsInTile = 94;
    public string drowningFact = "Nearly 2,000 children drown in Vietnam every year";
    public string drowningSource = "Ministry of Health, via Tuoi Tre News, Jul 2025";

    bool intro = true, running, storyAfterFlight;
    int chapter;
    float chapterStart, introFade = 1f;
    Vector2 introScroll;

    const int Chapters = 7;
    static readonly string[] Titles = { "HOME", "UNDER CLOUD", "THE WATER", "ONE VILLAGE", "CHECK FIRST", "WHAT PEOPLE DO", "EVERY PASS" };

    GUIStyle sPrimarySmall, sButtonSmall;
    GUIStyle sHuge, sLead, sBody, sSmall, sEyebrow, sWho, sWhoSmall, sAva, sPrimary, sButton, sPanel, sDotLabel, sHeadKey;
    Texture2D texShade, texSide, texRound;

    void Styles()
    {
        if (sPanel != null) return;
        sPanel = OureaTheme.Box(OureaTheme.PanelTex, 14, OureaTheme.Ink, 12, false);
        sHuge = OureaTheme.Text(60, OureaTheme.Accent, true, FontStyle.Normal, TextAnchor.UpperLeft);
        sLead = OureaTheme.Text(19, OureaTheme.Ink, false, FontStyle.Normal, TextAnchor.UpperLeft, true);
        sBody = OureaTheme.Text(13, OureaTheme.Ink, false, FontStyle.Normal, TextAnchor.UpperLeft, true);
        sSmall = OureaTheme.Text(11, OureaTheme.Muted, false, FontStyle.Normal, TextAnchor.UpperLeft, true);
        sEyebrow = OureaTheme.Text(11, OureaTheme.Accent, true);
        sHeadKey = OureaTheme.Text(14, OureaTheme.Ink, true);
        sWho = OureaTheme.Text(12, OureaTheme.Accent, true);
        sWhoSmall = OureaTheme.Text(11, OureaTheme.Muted);
        sAva = OureaTheme.Text(22, OureaTheme.Accent, true, FontStyle.Normal, TextAnchor.MiddleCenter);
        sDotLabel = OureaTheme.Text(10, OureaTheme.Muted, true, FontStyle.Normal, TextAnchor.UpperLeft);
        sPrimary = OureaTheme.Primary(14);
        sButton = OureaTheme.Button(12);
        sPrimarySmall = OureaTheme.Primary(11);
        sButtonSmall = OureaTheme.Button(11);
        texRound = OureaTheme.Rounded(Color.white, 16);
        // dark from the bottom up, so the map still shows behind the opening screen
        texShade = new Texture2D(1, 256, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < 256; y++) texShade.SetPixel(0, y, new Color(0.002f, 0.005f, 0.008f, Mathf.Lerp(0.95f, 0f, Mathf.SmoothStep(0f, 1f, y / 255f / 0.62f))));
        texShade.Apply();
        // wide screens: dark on the left behind the text column, clear over the globe
        texSide = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        for (int x = 0; x < 256; x++) texSide.SetPixel(x, 0, new Color(0.002f, 0.005f, 0.008f, Mathf.Lerp(0.92f, 0f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(140f, 255f, x)))));
        texSide.Apply();
    }

    void Start()
    {
        if (journey != null) journey.Arrived += () => { if (storyAfterFlight) { storyAfterFlight = false; running = true; Go(0); } };
    }

    void Update()
    {
        if (journey != null) intro = journey.mode == GlobeJourney.Mode.Globe;
        introFade = Mathf.MoveTowards(introFade, intro ? 1f : 0f, Time.deltaTime * 2.5f);
        if (lab != null) lab.guided = intro || running;
    }

    void Go(int c)
    {
        chapter = Mathf.Clamp(c, 0, Chapters - 1);
        chapterStart = Time.time;
        if (viewer == null || lab == null) return;
        var orbit = viewer.orbit;
        switch (chapter)
        {
            case 0: lab.CloseCard(); lab.Drain(); Pass(0); if (orbit != null) orbit.ResetView(); break;
            case 1: lab.Drain(); Pass(1); lab.OpenPhoto(); if (orbit != null) orbit.ResetView(); break;
            case 2: lab.CloseCard(); lab.Drain(); Pass(2); if (orbit != null) orbit.ResetView(); break;
            case 3: lab.CloseCard(); Pass(2); lab.StartVillageFlood(); break;
            case 4: lab.Drain(); Pass(2); lab.OpenCommunes(); if (orbit != null) orbit.ResetView(); break;
            case 5: lab.CloseCard(); Pass(2); FlyToFirst(); break;
            case 6: lab.CloseCard(); Pass(2); if (orbit != null) orbit.ResetView(); break;
        }
    }

    void Pass(int i)
    {
        if (viewer.timeline == null) return;
        if (viewer.timeline.Playing) viewer.timeline.TogglePlay();
        viewer.timeline.SetPosition(Mathf.Min(i, Mathf.Max(0, viewer.passes.Count - 1)));
    }

    DataLoader.BakedCommune First() { return Ranked(1); }

    // the commune at this rank on the latest pass
    DataLoader.BakedCommune Ranked(int rank)
    {
        if (lab == null || lab.live == null || viewer.passes.Count == 0) return null;
        string date = viewer.passes[viewer.passes.Count - 1].date;
        foreach (var b in lab.live.baked) if (b.pass == date && b.rank == rank) return b;
        return null;
    }

    int RankedCount()
    {
        int n = 0;
        while (n < 10 && Ranked(n + 1) != null) n++;
        return n;
    }

    void FlyToFirst()
    {
        var f = Ranked(actRank);
        if (f != null && viewer.orbit != null) viewer.orbit.Focus(new Vector3(f.u * lab.sizeX, 0f, (1f - f.v) * lab.sizeZ), 60f, 38f);
    }

    string N(float v) { return v.ToString("#,0"); }

    string Line(int c)
    {
        var p = viewer.passes;
        int last = p.Count - 1;
        switch (c)
        {
            case 0:
                return "This is home: 60 km of rice fields, orchards and canals on the Mekong. " + N(childrenInTile) +
                       " children under five live inside this one radar frame. Every flood season the water rises around our houses.";
            case 1:
            {
                float cloud = lab.CloudPct(1);
                string cl = cloud >= 0f ? cloud.ToString("0") + "% cloud" : "cloud";
                return "28 August, the middle of the monsoon. The camera satellite's picture on the left is " + cl +
                       ". Drag the line: NISAR's radar on the right sees straight through it, day or night.";
            }
            case 2:
                return "By 3 October NISAR saw " + lab.WaterPct(last).ToString("0") + "% of the frame under water: " +
                       N(p[last].newWaterKm2) + " km² that was dry land in July. About " + N(p[last].childrenNearWater) +
                       " children under five live within 100 m of that new water, and " + schoolsNear + " of " + schoolsInTile + " schools.";
            case 3:
                return "This is one town as the radar saw it, from July to October. A house turns red when new flood water comes within 100 m of it: " +
                       "the same rule behind the " + N(p[last].childrenNearWater) + " children I told you about.";
            case 4:
            {
                var f = First();
                string top = f != null ? (string.IsNullOrEmpty(f.name_en) ? f.name : f.name_en) + " is first this pass: " + f.reasons + "." : "";
                return "So where do we go first? Ourea ranks every commune by water near homes, young children, new water and schools. " + top;
            }
            case 5:
                return "Commune officials and child-protection staff use the list to send someone to check those hamlets first, " +
                       "open flood-season day care so small children are not home alone near the water, and warn parents about routes that flooded.";
            default:
                return "NISAR passes again every 12 days, and every pass re-checks the list. We never call a place safe, only " +
                       "\"least water on the " + (p.Count > 0 ? p[last].date : "") + " pass\". Now explore it yourself.";
        }
    }

    void OnGUI()
    {
        if (viewer == null || lab == null) return;
        GUI.depth = -20;   // above the map labels and the tool dock
        if (Event.current.type == EventType.Layout) uiRects.Clear();
        if (viewer.uiFont != null) GUI.skin.font = viewer.uiFont;
        if (viewer.displayFont != null) OureaTheme.display = viewer.displayFont;
        Styles();
        if (introFade <= 0.01f) GUI.color = OureaTheme.A(Color.white);
        float s = Mathf.Clamp(Screen.height / 820f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float W = Screen.width / s, H = Screen.height / s;
        const float M = 16f;

        if (introFade > 0.01f) DrawIntro(W, H, s);
        else if (journey != null && journey.mode == GlobeJourney.Mode.Flying) { }
        else if (running)
        {
            // the action panel steps aside while a dock card is open, and can be folded to a chip
            if (chapter == 5 && !lab.CardOpen) { if (actMin) DrawActChip(W, s); else DrawAct(W, H, s); }
            DrawGuide(W, H, s);
        }
        else
        {
            // the story is always one click away, under the title
            var b = new Rect(M, M + 58f + 10f, journey != null ? 184f : 268f, 40f);
            Block(b, s);
            if (GUI.Button(b, "WHY THIS MAP?  ·  STORY", sButton)) { running = true; Go(0); }
            if (journey != null)
            {
                var g = new Rect(b.xMax + 8f, b.y, 268f - b.width - 8f, 40f);
                Block(g, s);
                if (GUI.Button(g, "GLOBE", sButton)) { lab.CloseCard(); lab.Drain(); journey.FlyOut(); }
            }
        }
        GUI.matrix = Matrix4x4.identity;
    }

    static readonly System.Collections.Generic.List<Rect> uiRects = new System.Collections.Generic.List<Rect>();
    public static bool PointerOver(Vector2 guiPoint)
    {
        for (int i = 0; i < uiRects.Count; i++) if (uiRects[i].Contains(guiPoint)) return true;
        return false;
    }
    void Block(Rect r, float s) { uiRects.Add(new Rect(r.x * s, r.y * s, r.width * s, r.height * s)); }

    // The front page: the globe on the right (GlobeJourney frames it there on wide screens), one
    // clean column on the left: what this is, the question, three short points, two buttons.
    // On narrow screens the globe sits on top and the column below it.
    public static bool Wide { get { return Screen.width > Screen.height * 1.15f; } }

    void DrawIntro(float W, float H, float s)
    {
        var old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, introFade);   // the opening screen sits over the globe: not tied to the map panels
        bool wide = Wide;
        GUI.DrawTexture(wide ? new Rect(0, 0, W * 0.62f, H) : new Rect(0, 0, W, H), wide ? texSide : texShade);
        Block(new Rect(0, 0, W, H), s);

        float x = wide ? Mathf.Max(40f, W * 0.07f) : 24f;
        float cw = wide ? Mathf.Min(470f, W * 0.42f) : W - 48f;
        string[] heads = { "THE PROBLEM", "WHAT OUREA DOES", "WHO USES IT" };
        string[] bodies = {
            drowningFact + ". In the Delta the floods arrive under monsoon cloud, when camera satellites see nothing.",
            "NASA-ISRO's NISAR radar sees through cloud. Each pass becomes a map of new flood water and a ranked list of communes where it reaches homes with small children.",
            "Commune officials, child-protection and education offices and drowning-prevention programs: where to check first, which day care to open, who to warn."
        };
        // measure, then centre the column vertically (wide) or sit it at the bottom (narrow)
        float leadH = sLead.CalcHeight(new GUIContent(Lead), cw);
        float itemsH = 0f;
        for (int i = 0; i < 3; i++) itemsH += 22f + sBody.CalcHeight(new GUIContent(bodies[i]), cw) + 18f;
        float total = 18f + 74f + leadH + 26f + itemsH + 8f + 48f;
        float y = wide ? Mathf.Max(24f, (H - total) * 0.5f) : Mathf.Max(24f, H - 24f - total - 30f);

        GUI.Label(new Rect(x, y, cw, 16), "NASA SPACE APPS 2026  ·  NISAR FLOOD WATCH", sEyebrow); y += 18f;
        GUI.Label(new Rect(x - 2, y, cw, 70), "O U R E A", sHuge); y += 74f;
        GUI.Label(new Rect(x, y, cw, leadH), Lead, sLead); y += leadH + 26f;

        for (int i = 0; i < 3; i++)
        {
            GUI.color = new Color(OureaTheme.GlassLine.r, OureaTheme.GlassLine.g, OureaTheme.GlassLine.b, introFade * 0.9f);
            GUI.DrawTexture(new Rect(x, y, cw, 1), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, introFade);
            GUI.Label(new Rect(x, y + 10, cw, 16), heads[i], sEyebrow);
            float bh = sBody.CalcHeight(new GUIContent(bodies[i]), cw);
            GUI.Label(new Rect(x, y + 30, cw, bh), bodies[i], sBody);
            y += 22f + bh + 18f;
        }
        y += 8f;

        float bw = Mathf.Min(210f, (cw - 12f) * 0.5f);
        var go = new Rect(x, y, bw, 48f);
        var explore = new Rect(x + bw + 12f, y, bw, 48f);
        if (GUI.Button(go, "BEGIN THE STORY  >", sPrimary))
        {
            if (journey != null)
            {
                storyAfterFlight = true; running = false;
                lab.CloseCard(); lab.Drain(); Pass(0);   // chapter 1's date and view, set before landing
                journey.FlyIn();
            }
            else { intro = false; running = true; Go(0); }
        }
        if (GUI.Button(explore, "EXPLORE THE MAP", sButton))
        {
            if (journey != null) { storyAfterFlight = false; running = false; journey.FlyIn(); }
            else { intro = false; running = false; }
        }
        GUI.Label(new Rect(x, H - 34f, wide ? W * 0.55f : cw, 30), "Source: " + drowningSource + ". NISAR data is provisional. The guide in the story is fictional; her numbers are real.",
                  OureaTheme.Text(11, OureaTheme.Muted, false, FontStyle.Normal, TextAnchor.UpperLeft, true));
        GUI.color = old;
    }

    const string Lead = "Flood season in Vietnam's Mekong Delta. Where should someone check first for young children near flood water?";

    // Chapter 6: the decision itself, for the first commune on the list — a marker on the map and
    // the three things a commune office does with it, as a checklist people can tick.
    readonly bool[] done = new bool[3];
    int actRank = 1;            // which commune on the list the panel is about (1 = check first)
    bool actMin;                // folded to a chip so the map shows
    int draftFor = -1;          // which action's message is open (0 = send someone, 1 = day care)
    bool draftVi = true;        // Vietnamese first: it goes to commune staff
    float copiedAt = -10f;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void OureaCopy(string text);
#else
    static void OureaCopy(string text) { GUIUtility.systemCopyBuffer = text; }
#endif

    // Ready-to-send messages for the commune office, filled from the data (a draft: they edit and send
    // it themselves, by Zalo, SMS or email)
    string Draft(int action, DataLoader.BakedCommune f, bool vi)
    {
        string near = f.under5Near.ToString("#,0"), total = f.under5.ToString("#,0");
        if (action == 0)
            return vi
                ? "Kiểm tra trước hôm nay: " + f.name + ". Ảnh radar NISAR ngày " + f.pass + " (số liệu tạm thời) cho thấy nước lũ mới trong vòng 100 m quanh nhà dân; khoảng " + near + "/" + total + " trẻ dưới 5 tuổi sống gần đó. Đề nghị đến các ấp gần nước nhất, thăm các gia đình có con nhỏ và báo lại tình hình trong ngày. — UBND xã"
                : "Check first today: " + f.name + ". The NISAR radar pass of " + f.pass + " (provisional) shows new flood water within 100 m of homes; about " + near + " of " + total + " children under 5 live near it. Please visit the hamlets closest to the water, check on families with small children and report back today. — Commune office";
        return vi
            ? "Điểm giữ trẻ mùa lũ: " + f.name + " cần mở điểm giữ trẻ có người trông cho trẻ dưới 5 tuổi trong thời gian nước lên. Ảnh radar NISAR ngày " + f.pass + " (tạm thời) cho thấy nước lũ mới gần nhà dân; khoảng " + near + " trẻ nhỏ sống trong vòng 100 m. Đề nghị xác nhận địa điểm, người trông trẻ và giờ mở cửa trước ngày mai. — UBND xã"
            : "Flood-season day care: " + f.name + " needs a supervised day-care point for children under 5 while the water is high. The NISAR pass of " + f.pass + " (provisional) shows new flood water near homes; about " + near + " young children live within 100 m. Please confirm a site, carers and opening hours by tomorrow. — Commune office";
    }

    void DrawDraft(Rect act, float W, float H, float s, DataLoader.BakedCommune f)
    {
        const float M = 16f;
        float dw = 330f;
        string text = Draft(draftFor, f, draftVi);
        float th = sBody.CalcHeight(new GUIContent(text), dw - 36f);
        string sub = "Draft from the " + f.pass + " pass. Share it to Zalo, or copy it into SMS or email. Edit before sending.";
        float sh = sSmall.CalcHeight(new GUIContent(sub), dw - 36f);
        float dh = 16 + 22 + sh + 8 + th + 16 + 34 + 16;
        // beside the action panel when there's room, otherwise under it
        var r = act.x - 12f - dw >= M ? new Rect(act.x - 12f - dw, act.y, dw, dh) : new Rect(act.x, Mathf.Min(act.yMax + 12f, H - M - 96f - dh), act.width, dh);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, s);
        float x = r.x + 18, w = r.width - 36, y = r.y + 16;
        GUI.Label(new Rect(x, y, w - 90, 14), draftFor == 0 ? "MESSAGE  ·  TO THE FIELD TEAM" : "MESSAGE  ·  TO THE DAY-CARE LEAD", sEyebrow);
        if (GUI.Button(new Rect(r.xMax - 18 - 76, y - 4, 36, 22), "VI", draftVi ? sPrimarySmall : sButtonSmall)) draftVi = true;
        if (GUI.Button(new Rect(r.xMax - 18 - 36, y - 4, 36, 22), "EN", draftVi ? sButtonSmall : sPrimarySmall)) draftVi = false;
        y += 22;
        GUI.Label(new Rect(x, y, w, sh), sub, sSmall);
        y += sh + 8;
        GUI.Label(new Rect(x, y, w, th), text, sBody);
        y += th + 16;
        bool copied = Time.time - copiedAt < 2f;
        // Zalo is how commune staff message each other: the phone's share sheet, or copy + open Zalo on the web
        if (OureaLinks.Button(new Rect(x, y, 116, 32), "SHARE · ZALO", sPrimarySmall)) { if (!OureaLinks.Share(text)) copiedAt = Time.time; }
        if (GUI.Button(new Rect(x + 124, y, 94, 32), copied ? "COPIED" : "COPY", sButtonSmall)) { OureaCopy(text); copiedAt = Time.time; }
        if (GUI.Button(new Rect(x + 226, y, w - 226, 32), "CLOSE", sButtonSmall)) draftFor = -1;
    }
    void DrawActChip(float W, float s)
    {
        var f = Ranked(actRank);
        if (f == null) return;
        const float M = 16f;
        string t = (actRank == 1 ? "CHECK FIRST  ·  " : "#" + actRank + "  ·  ") + f.name;
        float cw = Mathf.Min(W - 2 * M - 108f, sHeadKey.CalcSize(new GUIContent(t)).x + 112f);
        var r = new Rect(W - M - 96f - 12f - cw, M + 64f, cw, 44f);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, s);
        GUI.Label(new Rect(r.x + 16, r.y + 13, r.width - 100, 20), t, sHeadKey);
        if (GUI.Button(new Rect(r.xMax - 80, r.y + 8, 68, 28), "SHOW", sPrimarySmall)) actMin = false;
    }

    void DrawAct(float W, float H, float s)
    {
        var f = Ranked(actRank);
        if (f == null) return;
        string name = f.name;   // Vietnamese first, as people there write it
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 sp = cam.WorldToScreenPoint(new Vector3(f.u * lab.sizeX, 0.5f, (1f - f.v) * lab.sizeZ));
            if (sp.z > 0f)
            {
                float x = sp.x / s, y = (Screen.height - sp.y) / s;
                float pulse = 1f + 0.25f * Mathf.Sin(Time.time * 4f);
                var old = GUI.color;
                GUI.color = OureaTheme.A(new Color(OureaTheme.Bad.r, OureaTheme.Bad.g, OureaTheme.Bad.b, 0.35f));
                GUI.DrawTexture(new Rect(x - 22 * pulse, y - 22 * pulse, 44 * pulse, 44 * pulse), texRound);
                GUI.color = OureaTheme.A(OureaTheme.Bad);
                GUI.DrawTexture(new Rect(x - 7, y - 7, 14, 14), texRound);
                GUI.color = old;
                string tag = "#" + actRank + "  " + name;
                float tw = sHeadKey.CalcSize(new GUIContent(tag)).x + 24f;
                GUI.Box(new Rect(x - tw * 0.5f, y - 54, tw, 30), GUIContent.none, sPanel);
                GUI.Label(new Rect(x - tw * 0.5f + 12, y - 48, tw, 20), tag, sHeadKey);
            }
        }

        const float M = 16f;
        float pw = 340f, ph = 412f;
        var r = new Rect(W - M - 96f - 12f - pw, M + 64f, pw, ph);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, s);
        float x0 = r.x + 18, w = r.width - 36, yy = r.y + 16;
        GUI.Label(new Rect(x0, yy, w - 110, 14), (actRank == 1 ? "CHECK FIRST" : "#" + actRank + " ON THE LIST") + "  ·  " + f.pass, sEyebrow);
        // step through the list, or fold the panel away to see the map
        int count = RankedCount();
        if (GUI.Button(new Rect(r.xMax - 18 - 100, yy - 6, 30, 26), "<", sButtonSmall)) { actRank = actRank <= 1 ? count : actRank - 1; draftFor = -1; FlyToFirst(); }
        if (GUI.Button(new Rect(r.xMax - 18 - 66, yy - 6, 30, 26), ">", sButtonSmall)) { actRank = actRank >= count ? 1 : actRank + 1; draftFor = -1; FlyToFirst(); }
        if (GUI.Button(new Rect(r.xMax - 18 - 30, yy - 6, 30, 26), "–", sButtonSmall)) { actMin = true; draftFor = -1; }
        yy += 20;
        GUI.Label(new Rect(x0, yy, w - 50, 24), name, OureaTheme.Text(18, OureaTheme.Ink, true)); 
        GUI.Label(new Rect(r.xMax - 18 - 50, yy, 50, 24), f.score.ToString("0"), OureaTheme.Text(18, OureaTheme.Bad, true, FontStyle.Normal, TextAnchor.UpperRight)); yy += 26;
        GUI.Label(new Rect(x0, yy, w, 40), f.reasons + ". Childcare gap not yet known.", OureaTheme.Text(12, OureaTheme.Ink, false, FontStyle.Normal, TextAnchor.UpperLeft, true)); yy += 48;

        GUI.Label(new Rect(x0, yy, w, 14), "WHAT THE COMMUNE OFFICE DOES", sEyebrow); yy += 20;
        string[] acts = {
            "Send someone to check the hamlets with the most children near water today",
            "Open a flood-season day-care point so under-fives aren't home alone near water",
            "Text parents whose route to school or day care now has water near it"
        };
        for (int i = 0; i < 3; i++)
        {
            var row = new Rect(x0, yy, w, 60);
            if (GUI.Button(row, GUIContent.none, sButton))
            {
                done[i] = !done[i];
                if (i == 2 && done[i]) lab.OpenAlerts(f.code);   // texting parents: the real sign-up (Twilio)
                if (i < 2) draftFor = done[i] ? i : (draftFor == i ? -1 : draftFor);   // the message to send for it
            }
            var box = new Rect(row.x + 10, row.y + 20, 18, 18);
            var old = GUI.color;
            GUI.color = OureaTheme.A(done[i] ? OureaTheme.Good : OureaTheme.S(70, 84, 88));
            GUI.DrawTexture(box, texRound);
            GUI.color = old;
            if (!done[i]) { GUI.color = OureaTheme.A(OureaTheme.Glass); GUI.DrawTexture(new Rect(box.x + 3, box.y + 3, 12, 12), texRound); GUI.color = old; }   // hollow until ticked
            GUI.Label(new Rect(row.x + 38, row.y + 7, row.width - 46, 50), acts[i], OureaTheme.Text(12, done[i] ? OureaTheme.Muted : OureaTheme.Ink, false, FontStyle.Normal, TextAnchor.UpperLeft, true));
            yy += 66;
        }
        GUI.Label(new Rect(x0, yy, w, 30), "The next NISAR pass re-checks this commune in 12 days.", sSmall);
        if (OureaLinks.Button(new Rect(x0, yy + 30, w, 30), "ROUTES TO DAY CARE & SCHOOL  >", sButton)) OureaLinks.Routes(f.pass, null);
        if (draftFor >= 0) DrawDraft(r, W, H, s, f);
    }

    void DrawGuide(float W, float H, float s)
    {
        const float M = 16f;
        float gw = Mathf.Min(470f, W - 2 * M), gh = 214f;
        var r = new Rect(M, H - M - 72f - 12f - gh, gw, gh);
        GUI.Box(r, GUIContent.none, sPanel);
        Block(r, s);

        // avatar
        var av = new Rect(r.x + 16, r.y + 16, 52, 52);
        var old = GUI.color;
        GUI.color = OureaTheme.A(OureaTheme.S(27, 58, 64));
        GUI.DrawTexture(av, texRound);
        GUI.color = old;
        GUI.Label(av, "L", sAva);

        float tx = av.xMax + 14, tw = r.xMax - 16 - tx;
        GUI.Label(new Rect(tx, r.y + 16, tw, 16), "LAN  <color=#93a8a3><size=11>preschool teacher · fictional guide</size></color>", sWho);
        GUI.Label(new Rect(tx, r.y + 34, tw, 16), (chapter + 1) + " / " + Chapters + "   ·   " + Titles[chapter], sDotLabel);

        // typewriter
        string line = Line(chapter);
        int n = Mathf.Clamp(Mathf.FloorToInt((Time.time - chapterStart) * 70f), 0, line.Length);
        GUI.Label(new Rect(r.x + 16, r.y + 76, r.width - 32, gh - 76 - 52), line.Substring(0, n), sBody);

        // controls
        float by = r.yMax - 44f;
        if (chapter > 0 && GUI.Button(new Rect(r.x + 16, by, 80, 30), "BACK", sButton)) Go(chapter - 1);
        if (GUI.Button(new Rect(r.x + (chapter > 0 ? 104 : 16), by, 80, 30), "CLOSE", sButton)) { running = false; lab.CloseCard(); }
        bool lastCh = chapter == Chapters - 1;
        if (GUI.Button(new Rect(r.xMax - 16 - 150, by, 150, 30), n < line.Length ? "SKIP  >" : (lastCh ? "EXPLORE  >" : "NEXT  >"), sPrimary))
        {
            if (n < line.Length) chapterStart = Time.time - 1000f;
            else if (lastCh) { running = false; }
            else Go(chapter + 1);
        }
        // progress dots
        for (int i = 0; i < Chapters; i++)
        {
            GUI.color = OureaTheme.A(i <= chapter ? OureaTheme.Accent : OureaTheme.S(70, 84, 88));
            GUI.DrawTexture(new Rect(r.x + 200 + i * 12, by + 12, 7, 7), texRound);
        }
        GUI.color = old;
    }
}
