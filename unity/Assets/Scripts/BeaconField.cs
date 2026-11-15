using System.Collections.Generic;
using UnityEngine;

// Puts a light column + ground ripple on each check-first top-10 place for the current pass.
// Reads OureaViewer's places/scores/currentPass, so the viewer needs no changes.
// Rank 1 is red, rank 10 amber, matching the list.
public class BeaconField : MonoBehaviour
{
    public OureaViewer viewer;
    public Material beamMaterial;
    public Material ringMaterial;
    public float beamHeight = 60f;
    public float beamRadius = 1.6f;
    public float ringRadius = 14f;

    static readonly Color Top1 = new Color(1f, 0.16f, 0.12f);
    static readonly Color Top10 = new Color(1f, 0.68f, 0.18f);

    class Beacon { public Transform root; public Renderer beam, ring; }
    readonly List<Beacon> pool = new List<Beacon>();
    MaterialPropertyBlock mpb;
    Mesh beamMesh, ringMesh;
    int shownPass = -1;

    void Update()
    {
        if (viewer != null && viewer.currentPass != shownPass) Refresh(viewer.currentPass);
    }

    // Also called by Editor/OureaCapture.cs, outside Play mode.
    public void Refresh(int pass)
    {
        if (mpb == null) { mpb = new MaterialPropertyBlock(); beamMesh = Column(24); ringMesh = Quad(); }
        shownPass = pass;
        var byId = new Dictionary<string, OureaViewer.Place>();
        foreach (var p in viewer.places) if (p.pin != null && !string.IsNullOrEmpty(p.id)) byId[p.id] = p;

        int used = 0;
        foreach (var s in viewer.scores)
        {
            if (s.pass != shownPass || s.rank < 1 || s.rank > 10 || s.score <= 0f) continue;
            OureaViewer.Place pl;
            if (!byId.TryGetValue(s.placeId, out pl)) continue;
            var b = Get(used++);
            var ground = pl.pin.position - Vector3.up * 4f;
            b.root.position = ground;
            float k = 1f - (s.rank - 1) / 9f;  // rank 1 tallest
            b.beam.transform.localScale = new Vector3(beamRadius, beamHeight * (0.55f + 0.45f * k), beamRadius);
            b.ring.transform.localScale = Vector3.one * ringRadius * (0.7f + 0.5f * k);
            var c = Color.Lerp(Top1, Top10, (s.rank - 1) / 9f);
            mpb.SetColor("_BaseColor", c);
            b.beam.SetPropertyBlock(mpb);
            b.ring.SetPropertyBlock(mpb);
            b.root.gameObject.SetActive(true);
        }
        for (int i = used; i < pool.Count; i++) pool[i].root.gameObject.SetActive(false);
    }

    Beacon Get(int i)
    {
        while (pool.Count <= i)
        {
            var root = new GameObject("Beacon").transform;
            if (!Application.isPlaying) root.gameObject.hideFlags = HideFlags.DontSave; // never baked into the scene
            root.SetParent(transform, false);
            var beam = new GameObject("Beam", typeof(MeshFilter), typeof(MeshRenderer));
            beam.hideFlags = root.gameObject.hideFlags;
            beam.transform.SetParent(root, false);
            beam.GetComponent<MeshFilter>().sharedMesh = beamMesh;
            var br = beam.GetComponent<MeshRenderer>();
            br.sharedMaterial = beamMaterial;
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var ring = new GameObject("Ring", typeof(MeshFilter), typeof(MeshRenderer));
            ring.hideFlags = root.gameObject.hideFlags;
            ring.transform.SetParent(root, false);
            ring.transform.localPosition = Vector3.up * 0.6f;
            ring.GetComponent<MeshFilter>().sharedMesh = ringMesh;
            var rr = ring.GetComponent<MeshRenderer>();
            rr.sharedMaterial = ringMaterial;
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pool.Add(new Beacon { root = root, beam = br, ring = rr });
        }
        return pool[i];
    }

    // Open cylinder, radius 1, height 1, uv.y 0 at the bottom.
    static Mesh Column(int seg)
    {
        var v = new Vector3[(seg + 1) * 2];
        var uv = new Vector2[v.Length];
        var tri = new int[seg * 6];
        for (int i = 0; i <= seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v[i * 2] = d; v[i * 2 + 1] = d + Vector3.up;
            uv[i * 2] = new Vector2(i / (float)seg, 0f); uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
        }
        for (int i = 0; i < seg; i++)
        {
            int b = i * 2;
            tri[i * 6] = b; tri[i * 6 + 1] = b + 1; tri[i * 6 + 2] = b + 2;
            tri[i * 6 + 3] = b + 2; tri[i * 6 + 4] = b + 1; tri[i * 6 + 5] = b + 3;
        }
        var m = new Mesh { name = "BeaconColumn", vertices = v, uv = uv, triangles = tri };
        m.RecalculateBounds();
        return m;
    }

    // Flat quad on XZ, side 2 (so scale = radius), uv 0..1.
    static Mesh Quad()
    {
        var m = new Mesh { name = "BeaconRing" };
        m.vertices = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        m.RecalculateBounds();
        return m;
    }
}
