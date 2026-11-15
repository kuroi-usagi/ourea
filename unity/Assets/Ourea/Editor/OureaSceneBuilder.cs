using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Menu: Ourea > Build Scene
// Reads ../handoff (see handoff/CONTRACT.md) and builds Assets/Scenes/Ourea.unity.
// If the data team's layers/findings aren't there yet, it simulates them and the app says so.
public static class OureaSceneBuilder
{
    const float MetersPerUnit = 100f;   // 1 Unity unit = 100 m, so the 65 km tile is ~650 units wide
    const float Exaggeration = 24f;     // the delta is ~9 m tall; 40x read as noisy tree-line ridges
    const int MeshRes = 513;
    const string GenDir = "Assets/Ourea/Generated";
    const string ScenePath = "Assets/Scenes/Ourea.unity";

    [Serializable] class Meta { public float[] bounds_m; public int size; public float pixel_m; public float pixel_m_y; public float min_m; public float max_m; }
    [Serializable] class DateJ { public string date; public string landwater; public float cloud_pct; }
    [Serializable] class DatesWrap { public DateJ[] items; }
    [Serializable] class AssetJ { public string id; public string name; public string type; public float lon; public float lat; public float u; public float v; }
    [Serializable] class AssetsWrap { public AssetJ[] items; }
    [Serializable] class ScoreJ { public string place_id; public string pass; public float score; public int rank; public int rank_change; public string[] reasons; }
    [Serializable] class ScoresWrap { public ScoreJ[] items; }
    [Serializable] class HeadlineJ { public float new_water_km2; public string pass; public int homes_near_water; public int children_under5_near_water; }

    static string ProjectRoot { get { return Path.GetDirectoryName(Application.dataPath); } }
    static string Handoff { get { return Path.GetFullPath(Path.Combine(ProjectRoot, "..", "handoff")); } }
    static string Full(string assetPath) { return Path.Combine(ProjectRoot, assetPath); }

    [MenuItem("Ourea/Build Scene", priority = 1)]
    public static void Build()
    {
        try { BuildInternal(); }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Ourea", "Build Scene failed:\n\n" + e.Message, "OK");
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    static void Progress(string msg, float t) { EditorUtility.DisplayProgressBar("Ourea: Build Scene", msg, t); }

    static void BuildInternal()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Directory.CreateDirectory(Full(GenDir));
        AssetDatabase.Refresh();

        // 1. Heightmap
        Progress("Reading heightmap", 0.05f);
        var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(Path.Combine(Handoff, "terrain", "meta.json")));
        byte[] raw = File.ReadAllBytes(Path.Combine(Handoff, "terrain", "heightmap.r16"));
        int n = meta.size;
        if (raw.Length < 1024)
            throw new Exception("handoff/terrain/heightmap.r16 is still a Git LFS pointer.\nIn Terminal run:\n  cd ~/Projects/ourea && git lfs pull\nthen build again.");
        if (raw.Length != n * n * 2)
            throw new Exception("heightmap.r16 is " + raw.Length + " bytes; expected " + (n * n * 2) + " for " + n + "x" + n + ".");
        var h = new float[n * n]; // row 0 = north
        for (int i = 0; i < h.Length; i++) h[i] = (raw[2 * i] | (raw[2 * i + 1] << 8)) / 65535f;

        float sizeX = (n - 1) * meta.pixel_m / MetersPerUnit;
        float sizeZ = (n - 1) * (meta.pixel_m_y > 0 ? meta.pixel_m_y : meta.pixel_m) / MetersPerUnit;
        float heightScale = (meta.max_m - meta.min_m) * Exaggeration / MetersPerUnit;

        // 2. Mesh
        Progress("Building delta mesh", 0.15f);
        var mesh = BuildMesh(h, n, sizeX, sizeZ, heightScale);
        var meshPath = GenDir + "/DeltaMesh.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(mesh, meshPath);

        // 3. Passes (real or simulated)
        bool simulated;
        var dates = new List<string>();
        var masks = new List<byte[]>();
        int mw, mh;
        LoadOrSimulateMasks(h, n, dates, masks, out mw, out mh, out simulated);

        float pixelAreaKm2 = (sizeX * MetersPerUnit / mw) * (sizeZ * MetersPerUnit / mh) / 1e6f;
        Progress("Coloring land", 0.3f);
        var allLand = new byte[mw * mh];
        for (int i = 0; i < allLand.Length; i++) allLand[i] = 255;
        var landTex = Colorize(allLand, mw, mh, h, n);
        var land = SavePng(GenDir + "/land.png", landTex);
        UnityEngine.Object.DestroyImmediate(landTex);

        // Raw masks go to the GPU; Shaders/OureaWater.shader colours and blends them.
        var passes = new List<OureaViewer.Pass>();
        int baseWater = CountWater(masks[0]);
        for (int i = 0; i < masks.Count; i++)
        {
            Progress("Saving mask " + dates[i], 0.35f + 0.35f * i / masks.Count);
            passes.Add(new OureaViewer.Pass
            {
                date = dates[i],
                mask = SaveMask(GenDir + "/mask_" + dates[i] + ".png", masks[i], mw, mh),
                newWaterKm2 = Mathf.Max(0f, (CountWater(masks[i]) - baseWater) * pixelAreaKm2)
            });
        }

        // 4. Places + scores
        Progress("Places and scores", 0.75f);
        var places = new List<OureaViewer.Place>();
        var scores = new List<OureaViewer.Score>();
        bool simPlaces = !LoadFindings(places, scores, passes, dates);
        if (simPlaces) SimulatePlaces(places, scores, masks, mw, mh);

        // 5. Scene
        Progress("Building scene", 0.85f);
        BuildScene(mesh, land, passes, places, scores, h, n, sizeX, sizeZ, heightScale, simulated || simPlaces,
            simulated ? "Water = lowest ground plus noise, not radar yet." : (simPlaces ? "Places and scores simulated." : ""));

        Debug.Log("Ourea: built " + ScenePath + " with " + passes.Count + " passes, " + places.Count + " places" +
                  (simulated || simPlaces ? " (SIMULATED data)." : "."));
    }

    // ---------- mesh ----------
    static Mesh BuildMesh(float[] h, int n, float sizeX, float sizeZ, float heightScale)
    {
        int R = MeshRes;
        var verts = new Vector3[R * R];
        var uvs = new Vector2[R * R];
        for (int r = 0; r < R; r++)
        {
            float zr = r / (float)(R - 1);          // 0 = south
            int srcRow = Mathf.RoundToInt((1f - zr) * (n - 1)); // heightmap row 0 = north
            for (int c = 0; c < R; c++)
            {
                float xr = c / (float)(R - 1);
                int srcCol = Mathf.RoundToInt(xr * (n - 1));
                int i = r * R + c;
                verts[i] = new Vector3(xr * sizeX, h[srcRow * n + srcCol] * heightScale, zr * sizeZ);
                uvs[i] = new Vector2(xr, zr);
            }
        }
        var tris = new int[(R - 1) * (R - 1) * 6];
        int t = 0;
        for (int r = 0; r < R - 1; r++)
            for (int c = 0; c < R - 1; c++)
            {
                int a = r * R + c, b = a + 1, d = a + R, e = d + 1;
                tris[t++] = a; tris[t++] = d; tris[t++] = b;
                tris[t++] = b; tris[t++] = d; tris[t++] = e;
            }
        var mesh = new Mesh { name = "Delta", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static float HeightAt(float[] h, int n, float u, float vNorth)
    {
        int c = Mathf.Clamp(Mathf.RoundToInt(u * (n - 1)), 0, n - 1);
        int r = Mathf.Clamp(Mathf.RoundToInt(vNorth * (n - 1)), 0, n - 1);
        return h[r * n + c];
    }

    // ---------- passes ----------
    static bool TryLoadMasks(string baseDir, List<string> dates, List<byte[]> masks, ref int w, ref int hh)
    {
        string datesPath = Path.Combine(baseDir, "dates.json");
        if (!File.Exists(datesPath)) return false;
        var wrap = JsonUtility.FromJson<DatesWrap>("{\"items\":" + File.ReadAllText(datesPath) + "}");
        if (wrap == null || wrap.items == null) return false;
        foreach (var d in wrap.items)
        {
            string p = string.IsNullOrEmpty(d.landwater) ? "" : Path.Combine(baseDir, d.landwater);
            if (!File.Exists(p)) p = Path.Combine(baseDir, "layers", "landwater", d.date + ".png");
            if (!File.Exists(p)) { Debug.LogWarning("Ourea: missing landwater for " + d.date); continue; }
            var bytes = File.ReadAllBytes(p);
            if (bytes.Length < 1024) { Debug.LogWarning("Ourea: " + p + " is an LFS pointer; use Ourea > Git > Get latest data"); continue; }
            var t = new Texture2D(2, 2);
            t.LoadImage(bytes);
            var px = t.GetPixels32(); // bottom row first
            w = t.width; hh = t.height;
            var m = new byte[w * hh];
            for (int r = 0; r < hh; r++)
                for (int c = 0; c < w; c++)
                    m[r * w + c] = px[(hh - 1 - r) * w + c].r; // store north row first
            UnityEngine.Object.DestroyImmediate(t);
            dates.Add(d.date);
            masks.Add(m);
        }
        return masks.Count > 0;
    }

    static void LoadOrSimulateMasks(float[] h, int n, List<string> dates, List<byte[]> masks, out int w, out int hh, out bool simulated)
    {
        w = hh = 0;
        // 1. Real pipeline output (handoff/dates.json)
        if (TryLoadMasks(Handoff, dates, masks, ref w, ref hh)) { simulated = false; return; }
        dates.Clear(); masks.Clear();
        // 2. Eric's placeholder layers (handoff/placeholder/, flagged simulated)
        if (TryLoadMasks(Path.Combine(Handoff, "placeholder"), dates, masks, ref w, ref hh)) { simulated = true; return; }
        dates.Clear(); masks.Clear();

        // Simulated: water rises over the season on the lowest ground, with blobby noise.
        simulated = true;
        w = hh = 2048;
        string[] simDates = { "2026-08-12", "2026-08-24", "2026-09-17", "2026-10-11" };
        float[] frac = { 0.08f, 0.18f, 0.31f, 0.24f };
        var e = new float[w * hh];
        for (int r = 0; r < hh; r++)
            for (int c = 0; c < w; c++)
            {
                float u = c / (float)(w - 1), v = r / (float)(hh - 1);
                float noise = Mathf.PerlinNoise(u * 9f + 3.1f, v * 9f + 7.7f) * 0.6f + Mathf.PerlinNoise(u * 40f, v * 40f) * 0.4f;
                e[r * w + c] = HeightAt(h, n, u, v) + (noise - 0.5f) * 0.18f;
            }
        var sorted = (float[])e.Clone();
        Array.Sort(sorted);
        for (int i = 0; i < simDates.Length; i++)
        {
            float thr = sorted[Mathf.Clamp((int)(frac[i] * sorted.Length), 0, sorted.Length - 1)];
            var m = new byte[w * hh];
            for (int k = 0; k < m.Length; k++) m[k] = e[k] < thr ? (byte)0 : (byte)255;
            dates.Add(simDates[i]);
            masks.Add(m);
        }
    }

    static int CountWater(byte[] m)
    {
        int c = 0;
        for (int i = 0; i < m.Length; i++) if (m[i] == 0) c++;
        return c;
    }

    static Texture2D Colorize(byte[] m, int w, int hh, float[] h, int n)
    {
        var low = new Color(0.36f, 0.55f, 0.27f);
        var high = new Color(0.70f, 0.66f, 0.45f);
        var water = new Color32(40, 110, 190, 255);
        var rice = new Color32(70, 170, 170, 255);
        var nodata = new Color32(120, 120, 120, 255);
        var px = new Color32[w * hh];
        for (int r = 0; r < hh; r++)
            for (int c = 0; c < w; c++)
            {
                byte val = m[r * w + c];
                Color32 col;
                if (val == 0) col = water;
                else if (val == 200) col = rice;
                else if (val == 128) col = nodata;
                else col = Color.Lerp(low, high, Mathf.Pow(HeightAt(h, n, c / (float)(w - 1), r / (float)(hh - 1)), 0.6f));
                px[(hh - 1 - r) * w + c] = col; // texture rows are bottom-up
            }
        var tex = new Texture2D(w, hh, TextureFormat.RGB24, false);
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static Texture2D SavePng(string assetPath, Texture2D tex)
    {
        File.WriteAllBytes(Full(assetPath), tex.EncodeToPNG());
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (imp != null)
        {
            imp.maxTextureSize = 2048;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.mipmapEnabled = true;
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    static Texture2D SaveMask(string assetPath, byte[] m, int w, int hh)
    {
        var px = new Color32[w * hh];
        for (int r = 0; r < hh; r++)
            for (int c = 0; c < w; c++)
            {
                byte v = m[r * w + c];
                px[(hh - 1 - r) * w + c] = new Color32(v, v, v, 255); // texture rows are bottom-up
            }
        var tex = new Texture2D(w, hh, TextureFormat.RGB24, false);
        tex.SetPixels32(px);
        File.WriteAllBytes(Full(assetPath), tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (imp != null)
        {
            // Class values (0/128/200/255) must survive import: linear, uncompressed, one channel.
            imp.sRGBTexture = false;
            imp.mipmapEnabled = false;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.filterMode = FilterMode.Bilinear;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            var ps = imp.GetDefaultPlatformTextureSettings();
            ps.format = TextureImporterFormat.R8;
            imp.SetPlatformTextureSettings(ps);
            imp.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // ---------- findings ----------
    static bool LoadFindings(List<OureaViewer.Place> places, List<OureaViewer.Score> scores, List<OureaViewer.Pass> passes, List<string> dates)
    {
        string dir = Path.Combine(Handoff, "findings");
        string ap = Path.Combine(dir, "assets.json"), sp = Path.Combine(dir, "scores.json");
        if (!File.Exists(ap) || !File.Exists(sp)) return false;

        var aw = JsonUtility.FromJson<AssetsWrap>("{\"items\":" + File.ReadAllText(ap) + "}");
        var sw = JsonUtility.FromJson<ScoresWrap>("{\"items\":" + File.ReadAllText(sp) + "}");
        if (aw == null || aw.items == null || sw == null || sw.items == null) return false;

        foreach (var a in aw.items)
            places.Add(new OureaViewer.Place { id = a.id, name = a.name, type = a.type, u = a.u, v = a.v });
        foreach (var s in sw.items)
        {
            int pi = dates.IndexOf(s.pass);
            if (pi < 0) { int parsed; if (int.TryParse(s.pass, out parsed)) pi = parsed; }
            if (pi < 0 || pi >= dates.Count) continue;
            scores.Add(new OureaViewer.Score
            {
                placeId = s.place_id, pass = pi, score = s.score, rank = s.rank, rankChange = s.rank_change,
                reason = s.reasons != null && s.reasons.Length > 0 ? s.reasons[0] : ""
            });
        }

        string hp = Path.Combine(dir, "headline.json");
        if (File.Exists(hp))
        {
            var hl = JsonUtility.FromJson<HeadlineJ>(File.ReadAllText(hp));
            int pi = hl != null ? dates.IndexOf(hl.pass) : -1;
            if (pi >= 0)
            {
                passes[pi].newWaterKm2 = hl.new_water_km2;
                passes[pi].homesNearWater = hl.homes_near_water;
                passes[pi].childrenNearWater = hl.children_under5_near_water;
            }
        }
        return true;
    }

    static void SimulatePlaces(List<OureaViewer.Place> places, List<OureaViewer.Score> scores, List<byte[]> masks, int w, int hh)
    {
        var rng = new System.Random(47);
        var popFactor = new Dictionary<string, float>();
        for (int i = 1; i <= 40; i++)
        {
            var id = "H" + i.ToString("00");
            places.Add(new OureaViewer.Place { id = id, name = "Hamlet " + i.ToString("00") + " (sim)", type = "hamlet",
                u = 0.05f + 0.9f * (float)rng.NextDouble(), v = 0.05f + 0.9f * (float)rng.NextDouble() });
            popFactor[id] = 0.6f + 0.8f * (float)rng.NextDouble();
        }
        for (int i = 1; i <= 12; i++)
            places.Add(new OureaViewer.Place { id = "S" + i.ToString("00"), name = "School " + i.ToString("00") + " (sim)", type = "school",
                u = 0.05f + 0.9f * (float)rng.NextDouble(), v = 0.05f + 0.9f * (float)rng.NextDouble() });

        const int radius = 20; // px, ~600 m
        var prevRank = new Dictionary<string, int>();
        for (int p = 0; p < masks.Count; p++)
        {
            var m = masks[p];
            var list = new List<OureaViewer.Score>();
            foreach (var pl in places)
            {
                if (pl.type != "hamlet") continue;
                int cx = Mathf.RoundToInt(pl.u * (w - 1)), cy = Mathf.RoundToInt(pl.v * (hh - 1));
                int water = 0, total = 0;
                for (int dy = -radius; dy <= radius; dy += 2)
                    for (int dx = -radius; dx <= radius; dx += 2)
                    {
                        if (dx * dx + dy * dy > radius * radius) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= w || y >= hh) continue;
                        total++;
                        if (m[y * w + x] == 0) water++;
                    }
                float frac = total > 0 ? water / (float)total : 0f;
                list.Add(new OureaViewer.Score { placeId = pl.id, pass = p, score = frac * popFactor[pl.id] * 100f,
                    reason = Mathf.RoundToInt(frac * 100f) + "% of ground within 600 m is water (sim)" });
            }
            list.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < list.Count; i++)
            {
                list[i].rank = i + 1;
                int pr;
                list[i].rankChange = prevRank.TryGetValue(list[i].placeId, out pr) ? pr - list[i].rank : 0;
            }
            prevRank.Clear();
            foreach (var s in list) prevRank[s.placeId] = s.rank;
            scores.AddRange(list);
        }
    }

    // ---------- scene ----------
    static Material MakeMaterial(string path, Color color, float smooth, string shaderName = "Universal Render Pipeline/Lit")
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        AssetDatabase.DeleteAsset(path);
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smooth);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static void BuildScene(Mesh mesh, Texture2D land, List<OureaViewer.Pass> passes, List<OureaViewer.Place> places, List<OureaViewer.Score> scores,
        float[] h, int n, float sizeX, float sizeZ, float heightScale, bool simulated, string note)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var deltaMat = MakeMaterial(GenDir + "/Delta.mat", Color.white, 0.05f, "Ourea/Water");
        deltaMat.SetTexture("_BaseMap", land);
        if (passes.Count > 0)
        {
            var last = passes[passes.Count - 1].mask;
            deltaMat.SetTexture("_MaskA", last);
            deltaMat.SetTexture("_MaskB", last);
            deltaMat.SetTexture("_MaskBase", passes[0].mask);
        }
        var pinMat = MakeMaterial(GenDir + "/Pin.mat", Color.white, 0.4f);
        EditorUtility.SetDirty(deltaMat);

        var delta = new GameObject("Mekong Delta");
        delta.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = delta.AddComponent<MeshRenderer>();
        rend.sharedMaterial = deltaMat;

        var root = new GameObject("Places").transform;
        foreach (var pl in places)
        {
            var go = GameObject.CreatePrimitive(pl.type == "school" ? PrimitiveType.Cube : PrimitiveType.Sphere);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = pl.name;
            go.transform.SetParent(root, false);
            // u: 0 = west → 1 = east; v: 0 = north → 1 = south (image coordinates, row 0 = north)
            float y = HeightAt(h, n, pl.u, pl.v) * heightScale + 4f;
            go.transform.position = new Vector3(pl.u * sizeX, y, (1f - pl.v) * sizeZ);
            go.GetComponent<Renderer>().sharedMaterial = pinMat;
            pl.pin = go.transform;
        }

        var cam = Camera.main;
        cam.nearClipPlane = 0.5f;
        cam.farClipPlane = 6000f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.1f, 0.13f);
        var orbit = cam.gameObject.AddComponent<OrbitCamera>();
        // Start a little west/south of centre so the map sits right of the list and above the timeline.
        orbit.target = new Vector3(sizeX * 0.42f, 0f, sizeZ * 0.44f);
        orbit.distance = Mathf.Max(sizeX, sizeZ) * 0.78f;
        orbit.pitch = 50f;
        orbit.boundsMin = new Vector2(0f, 0f);
        orbit.boundsMax = new Vector2(sizeX, sizeZ);
        orbit.maxDistance = Mathf.Max(sizeX, sizeZ) * 3f;
        cam.transform.rotation = Quaternion.Euler(orbit.pitch, 0f, 0f);
        cam.transform.position = orbit.target - cam.transform.forward * orbit.distance;

        var light = UnityEngine.Object.FindAnyObjectByType<Light>();
        if (light != null) light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

        var appGo = new GameObject("Ourea");
        var timeline = appGo.AddComponent<TimelineController>();
        timeline.passCount = passes.Count;
        var app = appGo.AddComponent<OureaViewer>();
        app.timeline = timeline;
        app.deltaRenderer = rend;
        app.orbit = orbit;
        app.simulated = simulated;
        app.dataNote = note;
        app.passes = passes;
        app.places = places;
        app.scores = scores;
        app.currentPass = Mathf.Max(0, passes.Count - 1);

        OureaLook.Apply(delta, rend, h, n, sizeX, sizeZ, heightScale, cam, light, root, app);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Selection.activeGameObject = app.gameObject;
    }
}
