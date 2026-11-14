using System.Collections.Generic;
using UnityEngine;

// Score columns: one solid hexagonal prism per scored place for the current pass, like the
// building extrusions in the Urban Sentinel atlas. Height = check-first score, colour = the
// same low → critical ramp (grey-blue, sand, orange, red). The top 10 stand on a white plinth.
// Reads OureaViewer's places/scores/currentPass, so the viewer needs no changes.
public class BeaconField : MonoBehaviour
{
    public OureaViewer viewer;
    public Material columnMaterial;   // URP Lit; colour comes per column through a property block
    public float maxHeight = 42f;
    public float radius = 3.4f;

    static readonly Color Low = new Color(0.56f, 0.64f, 0.71f);
    static readonly Color Medium = new Color(0.86f, 0.76f, 0.52f);
    static readonly Color High = new Color(0.86f, 0.52f, 0.33f);
    static readonly Color Critical = new Color(0.70f, 0.24f, 0.22f);

    public static Color Ramp(float t)
    {
        t = Mathf.Clamp01(t);
        if (t < 0.33f) return Color.Lerp(Low, Medium, t / 0.33f);
        if (t < 0.66f) return Color.Lerp(Medium, High, (t - 0.33f) / 0.33f);
        return Color.Lerp(High, Critical, (t - 0.66f) / 0.34f);
    }

    class Column { public Transform root, body, plinth; public Renderer bodyR, plinthR; }
    readonly List<Column> pool = new List<Column>();
    MaterialPropertyBlock mpb;
    Mesh prism;
    int shownPass = -1;

    void Update()
    {
        if (viewer != null && viewer.currentPass != shownPass) Refresh(viewer.currentPass);
    }

    // Also called by Editor/OureaCapture.cs, outside Play mode.
    public void Refresh(int pass)
    {
        if (mpb == null) { mpb = new MaterialPropertyBlock(); prism = Prism(6); }
        shownPass = pass;
        var byId = new Dictionary<string, OureaViewer.Place>();
        foreach (var p in viewer.places) if (p.pin != null && !string.IsNullOrEmpty(p.id)) byId[p.id] = p;

        float max = 0f;
        foreach (var s in viewer.scores) if (s.pass == pass) max = Mathf.Max(max, s.score);
        if (max <= 0f) max = 1f;

        int used = 0;
        foreach (var s in viewer.scores)
        {
            if (s.pass != pass) continue;
            OureaViewer.Place pl;
            if (!byId.TryGetValue(s.placeId, out pl)) continue;
            float k = s.score / max;
            var col = Get(used++);
            col.root.position = pl.pin.position - Vector3.up * 4.5f;
            float h = 1.5f + maxHeight * k * k;   // squared: the worst places stand out
            bool top = s.rank >= 1 && s.rank <= 10;
            col.body.localScale = new Vector3(radius, h, radius);
            mpb.SetColor("_BaseColor", Ramp(k));
            col.bodyR.SetPropertyBlock(mpb);
            col.plinth.gameObject.SetActive(top);
            if (top)
            {
                col.plinth.localScale = new Vector3(radius * 1.9f, 0.9f, radius * 1.9f);
                mpb.SetColor("_BaseColor", new Color(0.97f, 0.97f, 0.95f));
                col.plinthR.SetPropertyBlock(mpb);
            }
            col.root.gameObject.SetActive(true);
        }
        for (int i = used; i < pool.Count; i++) pool[i].root.gameObject.SetActive(false);
    }

    Column Get(int i)
    {
        while (pool.Count <= i)
        {
            var flags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave; // never baked into the scene
            var root = new GameObject("Column") { hideFlags = flags }.transform;
            root.SetParent(transform, false);
            var c = new Column { root = root };
            c.body = Part(root, "Body", flags, out c.bodyR);
            c.plinth = Part(root, "Plinth", flags, out c.plinthR);
            pool.Add(c);
        }
        return pool[i];
    }

    Transform Part(Transform parent, string name, HideFlags flags, out Renderer r)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = flags };
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = prism;
        r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = columnMaterial;
        return go.transform;
    }

    // Flat-shaded prism with a top cap: radius 1, height 1, base at y = 0.
    static Mesh Prism(int sides)
    {
        var v = new List<Vector3>(); var nrm = new List<Vector3>(); var tri = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a0 = (i + 0.5f) / sides * Mathf.PI * 2f, a1 = (i + 1.5f) / sides * Mathf.PI * 2f;
            var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)); var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            var n = ((p0 + p1) * 0.5f).normalized;
            int b = v.Count;
            v.Add(p0); v.Add(p1); v.Add(p0 + Vector3.up); v.Add(p1 + Vector3.up);
            nrm.Add(n); nrm.Add(n); nrm.Add(n); nrm.Add(n);
            tri.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        int c = v.Count;
        v.Add(Vector3.up); nrm.Add(Vector3.up);
        for (int i = 0; i < sides; i++)
        {
            float a = (i + 0.5f) / sides * Mathf.PI * 2f;
            v.Add(new Vector3(Mathf.Cos(a), 1, Mathf.Sin(a))); nrm.Add(Vector3.up);
        }
        for (int i = 0; i < sides; i++) tri.AddRange(new[] { c, c + 1 + (i + 1) % sides, c + 1 + i });
        var m = new Mesh { name = "ScoreColumn" };
        m.SetVertices(v); m.SetNormals(nrm); m.SetTriangles(tri, 0);
        m.RecalculateBounds();
        return m;
    }
}
