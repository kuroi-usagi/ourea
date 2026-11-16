using System.Collections.Generic;
using UnityEngine;

// Walking routes on the 3D map, from the selected check-first hamlet to its nearest day care and school,
// on OpenStreetMap roads (pipeline/make_osm_routes.py, Resources/osm_routes.json). Google's routes stay on
// the routes page; these are our own. Each route is coloured by how much of it is at risk on the pass on
// the timeline: under water, or low ground within ~100 m of new flood water (raised dikes don't count).
public class RouteField : MonoBehaviour
{
    public OureaViewer viewer;
    public BuildingField ground;   // bare-earth heights, the same the houses stand on
    public Material material;      // Ourea/Beacon: solid, glowing
    public float sizeX, sizeZ;
    public float lift = 1.2f, width = 0.7f;

    [System.Serializable] public class Route { public string to, to_name, to_type, kind; public float m, min, wet, under, low, raised, near; public float[] pts; }
    [System.Serializable] public class Hamlet { public string id, name; public int rank; public Route[] routes; }
    [System.Serializable] public class Pass { public string date; public Hamlet[] hamlets; }
    [System.Serializable] class Doc { public Pass[] passes; }

    Doc doc;
    string shownKey;
    readonly List<GameObject> lines = new List<GameObject>();
    readonly List<Route> shown = new List<Route>();
    readonly List<Vector3> ends = new List<Vector3>();
    MaterialPropertyBlock mpb;
    GUIStyle sChip;

    static readonly Color Low = new Color(0.93f, 0.96f, 0.95f), Mid = new Color(1f, 0.71f, 0.27f), High = new Color(1f, 0.30f, 0.22f);
    public static Color RiskColour(float r) { return r < 0.15f ? Low : r < 0.45f ? Mid : High; }

    void Awake()
    {
        var t = Resources.Load<TextAsset>("osm_routes");
        if (t != null) doc = JsonUtility.FromJson<Doc>(t.text);
    }

    public Hamlet Current()
    {
        if (doc == null || viewer == null || string.IsNullOrEmpty(viewer.SelectedPlaceId)) return null;
        string date = viewer.CurrentPassDate;
        foreach (var p in doc.passes)
            if (p.date == date)
                foreach (var h in p.hamlets) if (h.id == viewer.SelectedPlaceId) return h;
        return null;
    }

    void Update()
    {
        if (viewer == null) return;
        string key = viewer.SelectedPlaceId + "|" + viewer.CurrentPassDate;
        if (key == shownKey) return;
        shownKey = key;
        Rebuild(Current());
    }

    void Rebuild(Hamlet h)
    {
        foreach (var g in lines) Destroy(g);
        lines.Clear(); shown.Clear(); ends.Clear();
        if (h == null || h.routes == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        foreach (var r in h.routes)
        {
            if (r.pts == null || r.pts.Length < 4) continue;
            // the one to take to each place: the least at risk; the others are thinner
            bool best = true;
            foreach (var o in h.routes) if (o.to == r.to && o.wet < r.wet) best = false;
            var go = new GameObject("Route " + r.to_type + " " + r.kind);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Ribbon(r.pts, best ? width : width * 0.55f, best ? lift : lift * 0.9f);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var c = RiskColour(r.wet);
            mpb.SetColor("_BaseColor", best ? c : c * 0.55f);
            mr.SetPropertyBlock(mpb);
            lines.Add(go);
            if (best) { shown.Add(r); int n = r.pts.Length; ends.Add(World(r.pts[n - 2], r.pts[n - 1], lift)); }
        }
    }

    Vector3 World(float u, float v, float up)
    {
        float y = 0f;
        if (ground != null)
            y = (ground.GroundM(u, 1f - v) - ground.minM) / Mathf.Max(0.01f, ground.maxM - ground.minM) * ground.groundScale;
        return new Vector3(u * sizeX, y + up, (1f - v) * sizeZ);
    }

    // a flat strip along the path, lying on the ground
    Mesh Ribbon(float[] pts, float w, float up)
    {
        int n = pts.Length / 2;
        var p = new Vector3[n];
        for (int i = 0; i < n; i++) p[i] = World(pts[2 * i], pts[2 * i + 1], up);
        var verts = new Vector3[n * 2];
        var norms = new Vector3[n * 2];
        var tris = new int[(n - 1) * 6];
        for (int i = 0; i < n; i++)
        {
            Vector3 d = (p[Mathf.Min(i + 1, n - 1)] - p[Mathf.Max(i - 1, 0)]); d.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward) * (w * 0.5f);
            verts[2 * i] = p[i] - side; verts[2 * i + 1] = p[i] + side;
            norms[2 * i] = norms[2 * i + 1] = Vector3.up;
        }
        for (int i = 0; i < n - 1; i++)
        {
            int k = i * 6, a = 2 * i;
            tris[k] = a; tris[k + 1] = a + 2; tris[k + 2] = a + 1;
            tris[k + 3] = a + 1; tris[k + 4] = a + 2; tris[k + 5] = a + 3;
        }
        var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.vertices = verts; m.normals = norms; m.triangles = tris;
        m.RecalculateBounds();
        return m;
    }

    // a tag at each end: where it goes, how far, how much is at risk
    void OnGUI()
    {
        if (shown.Count == 0 || GlobeJourney.HideHud) return;
        var cam = Camera.main; if (cam == null) return;
        if (sChip == null)
        {
            sChip = OureaTheme.Box(OureaTheme.PanelTex, 10, OureaTheme.Ink, 11);
            sChip.richText = true; sChip.padding = new RectOffset(10, 10, 5, 5);
        }
        GUI.depth = -5;
        GUI.color = OureaTheme.A(Color.white);
        float s = OureaTheme.UIScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        for (int i = 0; i < shown.Count; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(ends[i] + Vector3.up * 2f);
            if (sp.z <= 0f || OureaViewer.PointerOverUI(new Vector2(sp.x, Screen.height - sp.y))) continue;
            var r = shown[i];
            string km = r.m < 150f ? "in the hamlet" : (r.m / 1000f).ToString("0.0") + " km · " + r.min.ToString("0") + " min";
            string col = ColorUtility.ToHtmlStringRGB(RiskColour(r.wet));
            string t = "<b>" + (r.to_type == "childcare" ? "DAY CARE" : "SCHOOL") + "</b>  " + r.to_name + "\n" + km +
                       "  ·  <color=#" + col + "><b>" + (r.wet * 100f).ToString("0") + "% at risk</b></color>";
            var size = sChip.CalcSize(new GUIContent(t));
            // beside the end point, so a short route's tag doesn't sit on the hamlet's name
            GUI.Box(new Rect(sp.x / s + 10f, (Screen.height - sp.y) / s - size.y * 0.5f, size.x, size.y), t, sChip);
        }
        GUI.matrix = Matrix4x4.identity;
    }
}
