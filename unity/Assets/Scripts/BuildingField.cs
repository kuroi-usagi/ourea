using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Buildings as 3D blocks, built at load time from two small images (handoff/buildings, GHSL 100 m):
// one block per built-up 100 m cell, its footprint from the share of the cell covered by buildings,
// its height from the cell's average building height. Standing on the bare-earth ground, so the
// modeled flood rises around them. Building here (not as a saved mesh) keeps the web build small.
public class BuildingField : MonoBehaviour
{
    public Texture2D heightTex;     // value / 4 = average building height (m)
    public Texture2D builtTex;      // value / 255 = share of the cell covered by buildings
    public Texture2D groundTex;     // bare-earth ground, 0..1 over the ground range (RHalf)
    public Material material;
    public float sizeX, sizeZ;      // tile size in world units
    public float groundScale;       // world units for the whole ground range
    public float minM, maxM;        // ground range (m)
    public float metersToUnitsY;    // vertical exaggeration: world units per metre
    public float houseUnitsPerM = 0.08f;   // building heights get less exaggeration than the ground

    // for the readout: ground elevation under each built-up cell, sorted, with built-up area (ha)
    [System.NonSerialized] public float[] builtGroundM;
    [System.NonSerialized] public float[] builtAreaHaCum;
    [System.NonSerialized] public float builtAreaHa;
    [System.NonSerialized] public int blocks;
    // every built-up cell: x,z in world units, ground (m), share built — for finding a village to watch
    [System.NonSerialized] public List<Vector4> cellList;

    bool built;

    void Start() { EnsureBuilt(); }

    public float GroundM(float u, float vSouth)
    {
        if (groundTex == null) return minM;
        return Mathf.Lerp(minM, maxM, groundTex.GetPixelBilinear(u, vSouth).r);
    }

    // Also called by Editor/OureaCapture.cs outside Play mode.
    public void EnsureBuilt()
    {
        if (built || heightTex == null || builtTex == null) return;
        built = true;
        int w = heightTex.width, h = heightTex.height;
        var hp = heightTex.GetPixels32();   // row 0 = south (texture space)
        var bp = builtTex.GetPixels32();
        float cellX = sizeX / w, cellZ = sizeZ / h;

        var cells = new List<Vector2>();   // (groundM, areaHa) for the readout
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var tris = new List<int>();
        var uvs = new List<Vector2>();   // x = ground height under the block (units), for the flood colour
        cellList = new List<Vector4>();
        int chunk = 0;
        System.Action flush = () =>
        {
            if (verts.Count == 0) return;
            var go = new GameObject("Buildings " + chunk++, typeof(MeshFilter), typeof(MeshRenderer));
            go.hideFlags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            var m = new Mesh { name = "Buildings", indexFormat = IndexFormat.UInt32 };
            m.SetVertices(verts); m.SetNormals(norms); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            go.GetComponent<MeshFilter>().sharedMesh = m;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
            verts.Clear(); norms.Clear(); tris.Clear(); uvs.Clear();
        };

        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = r * w + c;
                float frac = bp[i].r / 255f, hm = hp[i].r / 4f;
                if (frac < 0.05f || hm < 0.5f) continue;
                float u = (c + 0.5f) / w, v = (r + 0.5f) / h;
                float g = GroundM(u, v);
                cells.Add(new Vector2(g, frac));   // 1 cell = 1 ha
                float y0 = (g - minM) / Mathf.Max(0.01f, maxM - minM) * groundScale - 0.15f;
                // a few small houses per cell (more where more of it is built up), on a jittered 3x3
                // grid, so a village reads as houses rather than one tower per 100 m cell
                int houses = 1 + (frac > 0.2f ? 1 : 0) + (frac > 0.4f ? 1 : 0) + (frac > 0.65f ? 1 : 0);
                uint seed = (uint)(i * 2654435761u);
                int slots = 0;
                for (int k = 0; k < houses; k++)
                {
                    int slot;
                    do { slot = (int)(Rand(ref seed) * 9f) % 9; } while ((slots & (1 << slot)) != 0);
                    slots |= 1 << slot;
                    float ox = ((slot % 3) - 1 + (Rand(ref seed) - 0.5f) * 0.35f) * cellX / 3f;
                    float oz = ((slot / 3) - 1 + (Rand(ref seed) - 0.5f) * 0.35f) * cellZ / 3f;
                    float side = Mathf.Lerp(0.22f, 0.31f, frac) * (0.8f + 0.4f * Rand(ref seed));
                    float aspect = 0.7f + 0.6f * Rand(ref seed);
                    float hx = cellX * side * 0.5f * aspect, hz = cellZ * side * 0.5f / aspect;
                    float y1 = y0 + 0.15f + Mathf.Max(0.1f, hm * houseUnitsPerM * (0.7f + 0.6f * Rand(ref seed)));
                    Box(verts, norms, tris, new Vector3(u * sizeX + ox, 0, v * sizeZ + oz), hx, hz, y0, y1);
                    float pick = Rand(ref seed);   // which roof and wall colour (Shaders/OureaBuilding.shader)
                    while (uvs.Count < verts.Count) uvs.Add(new Vector2(y0 + 0.15f, pick));
                }
                cellList.Add(new Vector4(u * sizeX, v * sizeZ, g, frac));
                blocks++;
                if (verts.Count > 200000) flush();
            }
        flush();

        cells.Sort((a, b) => a.x.CompareTo(b.x));
        builtGroundM = new float[cells.Count];
        builtAreaHaCum = new float[cells.Count];
        float acc = 0f;
        for (int k = 0; k < cells.Count; k++) { acc += cells[k].y; builtGroundM[k] = cells[k].x; builtAreaHaCum[k] = acc; }
        builtAreaHa = acc;
    }

    // The densest cluster of built-up cells that a water level would flood (centre, world units):
    // where to point the camera to watch villages go under.
    public bool BestFloodSpot(float levelM, out Vector3 spot)
    {
        spot = new Vector3(sizeX * 0.5f, 0f, sizeZ * 0.5f);
        if (cellList == null || cellList.Count == 0) return false;
        const int N = 24;   // ~2.7 km bins
        var score = new float[N * N];
        foreach (var c in cellList)
        {
            if (c.z >= levelM || c.w < 0.12f) continue;
            int bx = Mathf.Clamp((int)(c.x / sizeX * N), 0, N - 1), bz = Mathf.Clamp((int)(c.y / sizeZ * N), 0, N - 1);
            score[bz * N + bx] += c.w;
        }
        int best = 0;
        for (int i = 1; i < score.Length; i++) if (score[i] > score[best]) best = i;
        if (score[best] <= 0f) return false;
        int gx = best % N, gz = best / N;
        Vector3 sum = Vector3.zero; float wsum = 0f;
        foreach (var c in cellList)
        {
            if (c.z >= levelM || c.w < 0.12f) continue;
            if ((int)(c.x / sizeX * N) != gx || (int)(c.y / sizeZ * N) != gz) continue;
            sum += new Vector3(c.x, (c.z - minM) / Mathf.Max(0.01f, maxM - minM) * groundScale, c.y) * c.w; wsum += c.w;
        }
        spot = sum / wsum;
        return true;
    }

    // Built-up area (ha) whose ground is below a water level (m).
    public float BuiltAreaBelow(float levelM)
    {
        if (builtGroundM == null || builtGroundM.Length == 0) return 0f;
        int lo = 0, hi = builtGroundM.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (builtGroundM[mid] < levelM) lo = mid + 1; else hi = mid; }
        return lo == 0 ? 0f : builtAreaHaCum[lo - 1];
    }

    static void Box(List<Vector3> v, List<Vector3> n, List<int> t, Vector3 c, float hx, float hz, float y0, float y1)
    {
        Vector3 a = new Vector3(c.x - hx, 0, c.z - hz), b = new Vector3(c.x + hx, 0, c.z - hz),
                d = new Vector3(c.x + hx, 0, c.z + hz), e = new Vector3(c.x - hx, 0, c.z + hz);
        Quad(v, n, t, Up(a, y0), Up(a, y1), Up(b, y1), Up(b, y0), Vector3.back);
        Quad(v, n, t, Up(b, y0), Up(b, y1), Up(d, y1), Up(d, y0), Vector3.right);
        Quad(v, n, t, Up(d, y0), Up(d, y1), Up(e, y1), Up(e, y0), Vector3.forward);
        Quad(v, n, t, Up(e, y0), Up(e, y1), Up(a, y1), Up(a, y0), Vector3.left);
        Quad(v, n, t, Up(a, y1), Up(e, y1), Up(d, y1), Up(b, y1), Vector3.up);
    }

    static float Rand(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }

    static Vector3 Up(Vector3 p, float y) { return new Vector3(p.x, y, p.z); }

    static void Quad(List<Vector3> v, List<Vector3> n, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 nrm)
    {
        int s = v.Count;
        v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
        n.Add(nrm); n.Add(nrm); n.Add(nrm); n.Add(nrm);
        t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
    }
}
