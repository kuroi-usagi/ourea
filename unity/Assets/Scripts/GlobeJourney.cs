using System.Collections.Generic;
using UnityEngine;

// From space to the delta and back, as in the first concept demo, but on real imagery and at real
// scale: the Earth (radius 6,371 km = 63,710 world units) sits under the delta tile, so the camera
// flies one continuous path from orbit down to the villages. Around the tile a finer ring of ground
// (handoff/globe/context.jpg) fades into the planet, so the study area reads as part of the Earth's
// surface, framed by a thin line. Other deltas are marked on the globe as "not built yet".
// Built at load time from two images (pipeline/make_globe.py) to keep the web build small.
[DefaultExecutionOrder(100)]   // after OrbitCamera, so the flight owns the camera while it runs
public class GlobeJourney : MonoBehaviour
{
    public OureaViewer viewer;
    public Texture2D earthTex, contextTex;
    public Material globeMat, contextMat, atmosphereMat, starMat;
    public double siteLat = 10.675, siteLon = 105.6;
    public Vector4 contextBoundsDeg;   // lon0, lat0, lon1, lat1
    public float sizeX, sizeZ;         // the tile, world units
    public Material skyMat;
    public float baseFogDensity = 0.0006f;

    public const float R = 63710f;     // Earth radius in world units (1 unit = 100 m)

    public enum Mode { Globe, Flying, Site }
    public Mode mode = Mode.Globe;
    public static bool HideHud;        // the map's panels stay hidden in space and in flight
    public event System.Action Arrived;

    [System.Serializable] public class Site { public string name, note; public float lat, lon; public bool ready; }
    public List<Site> sites = new List<Site>();

    Vector3 C;                 // Earth centre, world
    Vector3 siteCentre;        // centre of the tile, world
    Camera cam;
    OrbitCamera orbit;
    Renderer earthR, ringR, atmoR, starR;
    float t, dur = 7f, idleAngle;
    bool outbound;
    Vector3 flyFromDir; float flyFromAlt;
    Quaternion homeRot; Vector3 homePos;
    float skyExposure = 1.25f;
    GUIStyle sLabel, sLabelDim, sFly;
    Texture2D texDot;

    // world direction of a lat/lon, in the frame where the site is straight up and north is +Z
    public Vector3 Dir(double latDeg, double lonDeg)
    {
        double la = latDeg * Mathf.Deg2Rad, lo = lonDeg * Mathf.Deg2Rad, sla = siteLat * Mathf.Deg2Rad, slo = siteLon * Mathf.Deg2Rad;
        var e = new Vector3((float)(System.Math.Cos(la) * System.Math.Cos(lo)), (float)(System.Math.Cos(la) * System.Math.Sin(lo)), (float)System.Math.Sin(la));
        var up = new Vector3((float)(System.Math.Cos(sla) * System.Math.Cos(slo)), (float)(System.Math.Cos(sla) * System.Math.Sin(slo)), (float)System.Math.Sin(sla));
        var east = new Vector3((float)-System.Math.Sin(slo), (float)System.Math.Cos(slo), 0f);
        var north = Vector3.Cross(up, east);
        return new Vector3(Vector3.Dot(e, east), Vector3.Dot(e, up), Vector3.Dot(e, north)).normalized;
    }

    void Start()
    {
        cam = Camera.main;
        orbit = viewer != null ? viewer.orbit : null;
        siteCentre = new Vector3(sizeX * 0.5f, -0.05f, sizeZ * 0.5f);
        C = siteCentre - Vector3.up * R;
        if (skyMat != null && skyMat.HasProperty("_Exposure")) skyExposure = skyMat.GetFloat("_Exposure");

        earthR = MakeSphere("Earth", R - 3f, 192, 96, globeMat, true);
        ringR = MakeContext();
        atmoR = MakeSphere("Atmosphere", R * 1.025f, 96, 48, atmosphereMat, false);
        starR = MakeStars();

        if (orbit != null) { homePos = cam.transform.position; homeRot = cam.transform.rotation; }
        idleAngle = -38f;
        if (mode == Mode.Globe) { HideHud = true; if (orbit != null) orbit.enabled = false; }
        else HideHud = false;
    }

    Renderer MakeSphere(string name, float radius, int seg, int rings, Material mat, bool uv)
    {
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            float lat = 90f - 180f * r / rings;
            for (int s = 0; s <= seg; s++)
            {
                float lon = -180f + 360f * s / seg;
                var d = Dir(lat, lon);
                verts.Add(C + d * radius); norms.Add(d);
                uvs.Add(new Vector2(s / (float)seg, 1f - r / (float)rings));
            }
        }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < seg; s++)
            {
                int a = r * (seg + 1) + s, b = a + 1, c = a + seg + 1, d = c + 1;
                tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(b); tris.Add(d); tris.Add(c);
            }
        return Make(name, verts, norms, uvs, null, tris, mat);
    }

    // the finer ground around the tile: a lat/lon grid over the context window, faded at its rim
    Renderer MakeContext()
    {
        if (contextTex == null) return null;
        const int N = 160;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var cols = new List<Color>(); var tris = new List<int>();
        float lo0 = contextBoundsDeg.x, la0 = contextBoundsDeg.y, lo1 = contextBoundsDeg.z, la1 = contextBoundsDeg.w;
        float half = Mathf.Min(lo1 - lo0, la1 - la0) * 0.5f;
        for (int r = 0; r <= N; r++)
            for (int c = 0; c <= N; c++)
            {
                float u = c / (float)N, v = r / (float)N;
                float lon = Mathf.Lerp(lo0, lo1, u), lat = Mathf.Lerp(la0, la1, v);
                var d = Dir(lat, lon);
                verts.Add(C + d * R); norms.Add(d); uvs.Add(new Vector2(u, v));
                float ang = Mathf.Sqrt(Mathf.Pow(lat - (float)siteLat, 2) + Mathf.Pow((lon - (float)siteLon) * Mathf.Cos((float)siteLat * Mathf.Deg2Rad), 2));
                cols.Add(new Color(1, 1, 1, 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(half * 0.55f, half * 0.98f, ang))));
            }
        for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
            {
                int a = r * (N + 1) + c, b = a + 1, cc = a + N + 1, d = cc + 1;
                tris.Add(a); tris.Add(cc); tris.Add(b); tris.Add(b); tris.Add(cc); tris.Add(d);
            }
        return Make("Context ground", verts, norms, uvs, cols, tris, contextMat);
    }

    Renderer MakeStars()
    {
        var verts = new List<Vector3>(); var idx = new List<int>();
        var rnd = new System.Random(7);
        for (int i = 0; i < 2500; i++)
        {
            var d = new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1);
            if (d.sqrMagnitude > 1f || d.sqrMagnitude < 0.01f) { i--; continue; }
            verts.Add(C + d.normalized * R * 5f); idx.Add(i);
        }
        var go = new GameObject("Stars", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(verts); m.SetIndices(idx.ToArray(), MeshTopology.Points, 0);
        m.bounds = new Bounds(C, Vector3.one * R * 12f);
        go.GetComponent<MeshFilter>().sharedMesh = m;
        var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = starMat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return r;
    }

    Renderer Make(string name, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Color> col, List<int> t, Material mat)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); if (col != null) m.SetColors(col); m.SetTriangles(t, 0);
        m.RecalculateBounds();
        go.GetComponent<MeshFilter>().sharedMesh = m;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    // ---- flight ----
    float AltStart()
    {
        float half = (cam != null ? cam.fieldOfView : 40f) * 0.5f * Mathf.Deg2Rad;
        return R / Mathf.Sin(half * 0.47f) - R;   // the globe fills about half the view height
    }

    Vector3 IdleDir()
    {
        // a little west and north of the site, drifting east, so Asia turns into view
        return Quaternion.AngleAxis(-12f, Vector3.right) * Quaternion.AngleAxis(idleAngle, PolarAxis()) * Vector3.up;
    }

    Vector3 PolarAxis() { return Dir(90, 0); }

    public void FlyIn()
    {
        if (mode == Mode.Site) return;
        flyFromDir = mode == Mode.Globe ? IdleDir() : ((cam.transform.position - C).normalized);
        flyFromAlt = mode == Mode.Globe ? AltStart() : (cam.transform.position - C).magnitude - R;
        outbound = false; t = 0f; dur = 7.5f; mode = Mode.Flying; HideHud = true;
        if (orbit != null) { orbit.ResetView(); orbit.enabled = false; }
    }

    public void FlyOut()
    {
        if (mode != Mode.Site) return;
        homePos = cam.transform.position; homeRot = cam.transform.rotation;
        outbound = true; t = 0f; dur = 5.5f; mode = Mode.Flying; HideHud = true;
        if (orbit != null) orbit.enabled = false;
    }

    static float Ease(float x) { return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f; }

    void LateUpdate()
    {
        if (cam == null) return;
        if (mode == Mode.Globe)
        {
            idleAngle = -26f + 14f * Mathf.Sin(Time.time * 0.06f);
            Place(IdleDir(), AltStart(), 1f);
        }
        else if (mode == Mode.Flying)
        {
            t += Time.deltaTime / dur;
            float k = Mathf.Clamp01(t);
            float e = Ease(outbound ? 1f - k : k);
            Vector3 startDir = outbound ? IdleDir() : flyFromDir;
            float startAlt = outbound ? AltStart() : flyFromAlt;
            var dir = Vector3.Slerp(startDir, Vector3.up, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(e * 1.25f)));
            float alt = Mathf.Exp(Mathf.Lerp(Mathf.Log(startAlt), Mathf.Log(Mathf.Max(60f, HomeDistance())), e));
            Place(dir, alt, 1f - Mathf.SmoothStep(0.35f, 0.85f, e));
            // ease into the map's own view over the last stretch
            float land = Mathf.SmoothStep(0.8f, 1f, e);
            if (land > 0f && orbit != null)
            {
                Vector3 hp; Quaternion hr; HomePose(out hp, out hr);
                cam.transform.position = Vector3.Lerp(cam.transform.position, hp, land);
                cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, hr, land);
            }
            if (k >= 1f)
            {
                if (outbound) mode = Mode.Globe;
                else
                {
                    mode = Mode.Site; HideHud = false;
                    if (orbit != null) { orbit.SnapHome(); orbit.enabled = true; }
                    if (Arrived != null) Arrived();
                }
            }
        }
        Atmosphere();
    }

    float HomeDistance() { return orbit != null ? orbit.HomeDistance : 500f; }

    void HomePose(out Vector3 pos, out Quaternion rot)
    {
        rot = Quaternion.Euler(orbit.HomePitch, orbit.HomeYaw, 0f);
        pos = orbit.HomeTarget - rot * Vector3.forward * orbit.HomeDistance;
    }

    // camera above a direction on the globe; lookCentre 1 = at the Earth's centre, 0 = at the site
    void Place(Vector3 dir, float alt, float lookCentre)
    {
        var pos = C + dir * (R + alt);
        float k = lookCentre * Mathf.Clamp01(alt / 20000f);
        var look = Vector3.Lerp(siteCentre, C, k);
        cam.transform.position = pos;
        var fwd0 = (look - pos).normalized;
        var up0 = Vector3.ProjectOnPlane(PolarAxis(), fwd0).normalized;
        look -= up0 * R * 0.55f * k;   // aim a little low, so the globe sits in the upper part of the view
        var fwd = (look - pos).normalized;
        var upHint = Vector3.ProjectOnPlane(PolarAxis(), fwd);
        cam.transform.rotation = Quaternion.LookRotation(fwd, upHint.sqrMagnitude > 1e-4f ? upHint : Vector3.forward);
    }

    // sky, fog, clip planes and which parts of the Earth to draw, by height above the ground
    void Atmosphere()
    {
        float alt = Mathf.Max(1f, (cam.transform.position - C).magnitude - R);
        float space = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(800f, 5000f, alt));
        cam.nearClipPlane = Mathf.Clamp(alt * 0.002f, 0.3f, 400f);
        cam.farClipPlane = alt < 1500f ? 8000f : alt + 2.2f * R;
        RenderSettings.fogDensity = baseFogDensity * Mathf.Clamp01(700f / alt);
        if (skyMat != null && skyMat.HasProperty("_Exposure")) skyMat.SetFloat("_Exposure", skyExposure * (1f - space));
        cam.clearFlags = space > 0.98f ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
        cam.backgroundColor = new Color(0.004f, 0.006f, 0.012f);
        if (earthR != null) earthR.enabled = alt > 900f;
        if (ringR != null)
        {
            float f = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(6000f, 30000f, alt));
            ringR.sharedMaterial.SetFloat("_Fade", f);
            ringR.enabled = f > 0.01f;
        }
        if (atmoR != null) { atmoR.enabled = alt > 2500f; atmosphereMat.SetFloat("_Fade", space); }
        if (starR != null) starR.enabled = space > 0.6f;
    }

    // ---- labels on the globe and the flight caption ----
    void OnGUI()
    {
        if (cam == null || mode == Mode.Site) return;
        if (sLabel == null)
        {
            sLabel = OureaTheme.Box(OureaTheme.PanelTex, 14, OureaTheme.Ink, 12);
            sLabel.padding = new RectOffset(12, 12, 5, 5);
            sLabelDim = OureaTheme.Box(OureaTheme.PanelTex, 14, OureaTheme.Muted, 11);
            sLabelDim.padding = new RectOffset(10, 10, 4, 4);
            sFly = OureaTheme.Text(13, OureaTheme.Accent, true, FontStyle.Normal, TextAnchor.MiddleCenter);
            texDot = OureaTheme.Rounded(Color.white, 10);
        }
        GUI.depth = -5;
        float s = Mathf.Clamp(Screen.height / 820f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float alt = (cam.transform.position - C).magnitude - R;
        if (alt > 3000f)
        {
            var camDir = (cam.transform.position - C).normalized;
            foreach (var site in sites)
            {
                var d = Dir(site.lat, site.lon);
                if (Vector3.Dot(d, camDir) < 0.25f) continue;   // round the back of the globe
                Vector3 sp = cam.WorldToScreenPoint(C + d * R);
                if (sp.z < 0f) continue;
                float x = sp.x / s, y = (Screen.height - sp.y) / s;
                var old = GUI.color;
                float pulse = site.ready ? 1f + 0.3f * Mathf.Sin(Time.time * 3f) : 1f;
                GUI.color = site.ready ? OureaTheme.Accent : OureaTheme.S(150, 160, 162);
                GUI.DrawTexture(new Rect(x - 6 * pulse, y - 6 * pulse, 12 * pulse, 12 * pulse), texDot);
                GUI.color = old;
                string label = site.ready ? site.name : site.name + "  ·  " + site.note;
                var st = site.ready ? sLabel : sLabelDim;
                var size = st.CalcSize(new GUIContent(label));
                var r = new Rect(x + 12, y - size.y * 0.5f, size.x, size.y);
                if (GUI.Button(r, label, st) && site.ready && mode == Mode.Globe) FlyIn();
            }
        }
        if (mode == Mode.Flying && !outbound)
        {
            float W = Screen.width / s;
            GUI.color = new Color(1, 1, 1, Mathf.Clamp01(Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI) * 2f));
            GUI.Label(new Rect(0, 24, W, 24), "F L Y I N G   T O   Đ Ồ N G   T H Á P   ·   M E K O N G   D E L T A", sFly);
            GUI.color = Color.white;
        }
        GUI.matrix = Matrix4x4.identity;
    }
}
