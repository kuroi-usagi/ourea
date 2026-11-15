using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Trees as 3D crowns, built at load time from two small images (handoff/trees, ETH Global Canopy
// Height 2020, 10 m, summarised to 100 m cells): one or two crowns per wooded cell, sized by the
// share of the cell under canopy and as tall as its mean canopy height. The Delta's orchards and
// tree lines follow the canals, so this is most of the 3D the flat ground has.
public class TreeField : MonoBehaviour
{
    public Texture2D heightTex;     // value / 4 = mean canopy height (m)
    public Texture2D coverTex;      // value / 255 = share of the cell with canopy >= 3 m
    public BuildingField ground;    // for the ground height under each crown
    public Material material;
    public float unitsPerM = 0.05f; // same stretch as the houses
    [System.NonSerialized] public int crowns;
    bool built;

    void Start() { EnsureBuilt(); }

    static float Rand(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }

    public void EnsureBuilt()
    {
        if (built || heightTex == null || coverTex == null || ground == null) return;
        built = true;
        int w = heightTex.width, h = heightTex.height;
        var hp = heightTex.GetPixels32();   // row 0 = south
        var cp = coverTex.GetPixels32();
        float cellX = ground.sizeX / w, cellZ = ground.sizeZ / h;
        float range = Mathf.Max(0.01f, ground.maxM - ground.minM);

        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        int chunk = 0;
        System.Action flush = () =>
        {
            if (verts.Count == 0) return;
            var go = new GameObject("Trees " + chunk++, typeof(MeshFilter), typeof(MeshRenderer));
            go.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            var m = new Mesh { name = "Trees", indexFormat = IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetNormals(norms); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            go.GetComponent<MeshFilter>().sharedMesh = m;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
            verts.Clear(); norms.Clear(); uvs.Clear(); tris.Clear();
        };

        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                float cover = cp[i].r / 255f, hm = hp[i].r / 4f;
                if (cover < 0.15f || hm < 3f) continue;
                float u = (c + 0.5f) / w, v = (r + 0.5f) / h;
                float gy = (ground.GroundM(u, v) - ground.minM) / range * ground.groundScale;
                uint seed = (uint)(i * 2246822519u + 1u);
                int n = cover > 0.55f ? 2 : 1;
                for (int k = 0; k < n; k++)
                {
                    float ox = (Rand(ref seed) - 0.5f) * cellX * 0.55f, oz = (Rand(ref seed) - 0.5f) * cellZ * 0.55f;
                    float rad = cellX * Mathf.Lerp(0.18f, 0.34f, cover) * (0.8f + 0.4f * Rand(ref seed)) / Mathf.Sqrt(n);
                    float tall = Mathf.Max(0.12f, hm * unitsPerM * (0.8f + 0.4f * Rand(ref seed)));
                    Crown(verts, norms, uvs, tris, new Vector3(u * ground.sizeX + ox, gy, v * ground.sizeZ + oz), rad, tall, Rand(ref seed) * 6.283f);
                    crowns++;
                }
                if (verts.Count > 200000) flush();
            }
        flush();
    }

    // a rounded six-sided crown: a ring at 40% height, a tip, and a short base under it
    static void Crown(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector3 at, float rad, float tall, float rot)
    {
        int s = v.Count;
        v.Add(at + Vector3.up * tall); n.Add(Vector3.up);
        v.Add(at + Vector3.up * (tall * 0.12f)); n.Add(Vector3.down);
        for (int k = 0; k < 6; k++)
        {
            float a = rot + k * Mathf.PI / 3f;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            v.Add(at + d * rad + Vector3.up * (tall * 0.42f));
            n.Add((d + Vector3.up * 0.6f).normalized);
        }
        for (int k = 0; k < 8; k++) uv.Add(new Vector2(at.y, 0f));   // ground height: flooded trees darken like houses
        for (int k = 0; k < 6; k++)
        {
            int a = s + 2 + k, b = s + 2 + (k + 1) % 6;
            t.Add(s); t.Add(b); t.Add(a);         // upper cone
            t.Add(s + 1); t.Add(a); t.Add(b);     // lower cone
        }
    }
}
