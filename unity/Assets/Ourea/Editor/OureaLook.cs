using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// The "wow" layer on top of OureaSceneBuilder: satellite basemap, DEM normals, diorama base,
// sky + haze + sun, URP post-processing and glowing check-first beacons.
// Called once from OureaSceneBuilder.BuildScene; everything it makes lives in Generated/.
public static class OureaLook
{
    const string GenDir = "Assets/Ourea/Generated";
    const float BaseDepth = 7f;

    static string ProjectRoot { get { return Path.GetDirectoryName(Application.dataPath); } }
    static string Handoff { get { return Path.GetFullPath(Path.Combine(ProjectRoot, "..", "handoff")); } }

    public static void Apply(GameObject delta, Renderer deltaRend, float[] h, int n, float sizeX, float sizeZ, float heightScale,
        Camera cam, Light sun, Transform placesRoot, OureaViewer app)
    {
        var mat = deltaRend.sharedMaterial;

        // 1. Satellite basemap (pipeline/make_basemap.py) replaces the height-tinted land colour
        var basemap = ImportBasemap();
        if (basemap != null) mat.SetTexture("_BaseMap", basemap);
        mat.SetTexture("_NormalMap", MakeNormals(h, n, sizeX, sizeZ, heightScale));
        EditorUtility.SetDirty(mat);

        // 2. Diorama base: earth-coloured walls under the tile edge
        var baseGo = new GameObject("Diorama base");
        baseGo.AddComponent<MeshFilter>().sharedMesh = SaveMesh(Skirt(h, n, sizeX, sizeZ, heightScale), GenDir + "/BaseMesh.asset");
        var baseRend = baseGo.AddComponent<MeshRenderer>();
        baseRend.sharedMaterial = LitMaterial(GenDir + "/Base.mat", new Color(0.13f, 0.10f, 0.08f), 0.1f);

        // 3. Sky, haze, sun
        var sky = new Material(Shader.Find("Skybox/Procedural"));
        sky.SetFloat("_SunSize", 0.035f);
        sky.SetFloat("_AtmosphereThickness", 0.85f);
        sky.SetColor("_SkyTint", new Color(0.52f, 0.56f, 0.62f));
        sky.SetColor("_GroundColor", new Color(0.64f, 0.70f, 0.77f)); // below the horizon reads as haze
        sky.SetFloat("_Exposure", 1.25f);
        SaveAsset(sky, GenDir + "/Sky.mat");
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.58f, 0.68f, 0.84f);
        RenderSettings.ambientEquatorColor = new Color(0.50f, 0.52f, 0.50f);
        RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.18f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.70f, 0.77f, 0.84f);
        RenderSettings.fogDensity = 0.0006f;   // horizon haze without greying the map

        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(34f, -38f, 0f);
            sun.color = new Color(1f, 0.92f, 0.80f);
            sun.intensity = 2.1f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
        }

        cam.clearFlags = CameraClearFlags.Skybox;
        cam.allowHDR = true;
        cam.allowMSAA = true;
        cam.farClipPlane = 8000f;
        var camData = cam.GetUniversalAdditionalCameraData();
        camData.renderPostProcessing = true;
        camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        camData.antialiasingQuality = AntialiasingQuality.High;

        // WebGL uses the PC quality tier (MSAA, soft shadows). Set through the editor so an open
        // editor doesn't write its in-memory copy back over a hand edit.
        UseQualityTierForWebGL("PC");

        // 4. Post-processing
        var vol = new GameObject("Post FX").AddComponent<Volume>();
        vol.isGlobal = true;
        vol.sharedProfile = PostProfile();

        // 5. Pins glow; top 10 get light columns
        if (placesRoot != null && placesRoot.childCount > 0)
        {
            var pinMat = placesRoot.GetChild(0).GetComponent<Renderer>().sharedMaterial;
            pinMat.shader = Shader.Find("Ourea/Beacon");
            pinMat.SetFloat("_Glow", 1.3f);
            EditorUtility.SetDirty(pinMat);
            foreach (Transform t in placesRoot)
                t.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
        }
        // Re-bind the masks last: imports above can drop the references the builder set.
        if (app.passes.Count > 0)
        {
            var last = app.passes[app.passes.Count - 1].mask;
            mat.SetTexture("_MaskA", last);
            mat.SetTexture("_MaskB", last);
            mat.SetTexture("_MaskBase", app.passes[0].mask);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
        }

        var field = new GameObject("Beacons").AddComponent<BeaconField>();
        field.viewer = app;
        field.beamMaterial = BeamMaterial(GenDir + "/Beam.mat", false);
        field.ringMaterial = BeamMaterial(GenDir + "/Ring.mat", true);

        // UI font with Vietnamese letters (place names like Chợ Vàm, Gò Ớt); SIL OFL, see Fonts/OFL.txt
        app.uiFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Ourea/Fonts/BeVietnamPro-Regular.ttf");

        // 6. "What if" controls: modeled water surface + real radar view (Scripts/FloodLab.cs)
        AddFloodLab(delta, h, n, sizeX, sizeZ, heightScale, app);
    }

    [System.Serializable] class TerrainMeta { public float min_m; public float max_m; public double[] bounds_m; }
    [System.Serializable] class NisarPass { public string date; public float water_pct; }
    [System.Serializable] class NisarWrap { public NisarPass[] items; }
    [System.Serializable] class OpticalPass { public string pass; public string image_date; public float cloud_pct; }
    [System.Serializable] class OpticalWrap { public OpticalPass[] items; }
    [System.Serializable] class Hotspot { public float u; public float v; }
    [System.Serializable] class CommuneScore
    {
        public string pass, name, name_en, province, confidence;
        public int rank, under5_in_tile, under5_near_flood_water;
        public float score;
        public string[] reasons;
        public Hotspot[] hotspots;
    }
    [System.Serializable] class CommuneWrap { public CommuneScore[] items; }

    // Mitch's commune scores: top 10 per pass with any score, for the Communes tab until the API has a pass
    static DataLoader.BakedCommune[] BakedCommunes()
    {
        var path = Path.Combine(Handoff, "findings", "commune_scores.json");
        if (!File.Exists(path)) return new DataLoader.BakedCommune[0];
        var all = JsonUtility.FromJson<CommuneWrap>("{\"items\":" + File.ReadAllText(path) + "}").items;
        var outList = new System.Collections.Generic.List<DataLoader.BakedCommune>();
        foreach (var c in all)
        {
            if (c.rank > 10 || c.score <= 0f) continue;
            var hs = c.hotspots != null && c.hotspots.Length > 0 ? c.hotspots[0] : new Hotspot { u = 0.5f, v = 0.5f };
            outList.Add(new DataLoader.BakedCommune {
                pass = c.pass, rank = c.rank, name = c.name, name_en = c.name_en, province = c.province,
                confidence = c.confidence, score = c.score, under5 = c.under5_in_tile, under5Near = c.under5_near_flood_water,
                reasons = c.reasons != null && c.reasons.Length > 0 ? c.reasons[0] : "", u = hs.u, v = hs.v });
        }
        outList.Sort((a, b) => a.pass == b.pass ? a.rank.CompareTo(b.rank) : string.CompareOrdinal(a.pass, b.pass));
        Debug.Log("Ourea: " + outList.Count + " commune rows from commune_scores.json");
        return outList.ToArray();
    }

    static void AddFloodLab(GameObject delta, float[] h, int n, float sizeX, float sizeZ, float heightScale, OureaViewer app)
    {
        var lab = new GameObject("Flood lab").AddComponent<FloodLab>();
        lab.viewer = app;
        lab.minM = OureaSceneBuilder.GroundMinM; lab.maxM = OureaSceneBuilder.GroundMaxM;   // bare earth when available
        var tm = JsonUtility.FromJson<TerrainMeta>(File.ReadAllText(Path.Combine(Handoff, "terrain", "meta.json")));
        lab.boundsM = tm.bounds_m; lab.sizeX = sizeX; lab.sizeZ = sizeZ;
        lab.live = lab.gameObject.AddComponent<DataLoader>();   // Asher's Cloudflare API (k4)
        lab.live.baked = BakedCommunes();
        lab.groundSource = OureaSceneBuilder.GroundSource;
        lab.heightScale = heightScale;
        lab.tileAreaKm2 = sizeX * sizeZ * 100f * 100f / 1e6f;   // 1 unit = 100 m

        // DEM curve: fraction of the tile at or below each of 256 levels between min and max
        const int Bins = 256;
        var counts = new int[Bins];
        foreach (var v in h) counts[Mathf.Clamp((int)(v * (Bins - 1) + 0.5f), 0, Bins - 1)]++;
        var curve = new float[Bins];
        long acc = 0;
        for (int i = 0; i < Bins; i++) { acc += counts[i]; curve[i] = acc / (float)h.Length; }
        lab.demCurve = curve;

        // NISAR water % per pass, and the HV backscatter image per pass
        var pct = new float[app.passes.Count];
        string nj = Path.Combine(Handoff, "layers", "nisar.json");
        if (File.Exists(nj))
        {
            var wrap = JsonUtility.FromJson<NisarWrap>("{\"items\":" + File.ReadAllText(nj) + "}");
            for (int i = 0; i < app.passes.Count; i++)
                foreach (var it in wrap.items) if (it.date == app.passes[i].date) pct[i] = it.water_pct;
        }
        lab.observedWaterPct = pct;
        var radar = new Texture2D[app.passes.Count];
        for (int i = 0; i < app.passes.Count; i++)
        {
            string src = Path.Combine(Handoff, "layers", "backscatter", app.passes[i].date + ".png");
            if (!File.Exists(src) || new FileInfo(src).Length < 1024) continue;
            string dst = GenDir + "/radar_" + app.passes[i].date + ".png";
            File.Copy(src, Path.Combine(ProjectRoot, dst), true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
            imp.sRGBTexture = false;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.maxTextureSize = 1024;   // plenty for a whole-tile view, keeps the WebGL build small
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            var ps = imp.GetDefaultPlatformTextureSettings(); ps.format = TextureImporterFormat.R8; imp.SetPlatformTextureSettings(ps);
            imp.SaveAndReimport();
            radar[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }
        lab.radar = radar;

        // Sentinel-2 true colour nearest each pass, for the compare wipe (Tina, pipeline/make_optical.py)
        var optical = new Texture2D[app.passes.Count];
        var opticalDate = new string[app.passes.Count];
        var opticalCloud = new float[app.passes.Count];
        string oj = Path.Combine(Handoff, "layers", "optical", "optical.json");
        var om = File.Exists(oj) ? JsonUtility.FromJson<OpticalWrap>("{\"items\":" + File.ReadAllText(oj) + "}") : null;
        for (int i = 0; i < app.passes.Count; i++)
        {
            string date = app.passes[i].date;
            if (om != null) foreach (var it in om.items) if (it.pass == date) { opticalDate[i] = it.image_date; opticalCloud[i] = it.cloud_pct; }
            string src = Path.Combine(Handoff, "layers", "optical", date + ".png");
            if (!File.Exists(src) || new FileInfo(src).Length < 1024) continue;
            string dst = GenDir + "/optical_" + date + ".png";
            File.Copy(src, Path.Combine(ProjectRoot, dst), true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.anisoLevel = 8;
            imp.maxTextureSize = 2048;
            imp.textureCompression = TextureImporterCompression.Compressed;
            imp.crunchedCompression = true;
            imp.compressionQuality = 80;
            imp.SaveAndReimport();
            optical[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }
        lab.optical = optical;
        lab.opticalDate = opticalDate;
        lab.opticalCloud = opticalCloud;

        // the water sheet: a flat quad over the tile, a little larger than it
        var plane = new GameObject("Modeled water surface", typeof(MeshFilter), typeof(MeshRenderer));
        var m = new Mesh { name = "WaterSheet" };
        float pad = 0f;
        m.vertices = new[] { new Vector3(-pad, 0, -pad), new Vector3(sizeX + pad, 0, -pad), new Vector3(-pad, 0, sizeZ + pad), new Vector3(sizeX + pad, 0, sizeZ + pad) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        m.RecalculateNormals(); m.RecalculateBounds();
        plane.GetComponent<MeshFilter>().sharedMesh = SaveMesh(m, GenDir + "/WaterSheet.asset");
        var pm = new Material(Shader.Find("Ourea/FloodPlane"));
        SaveAsset(pm, GenDir + "/FloodPlane.mat");
        var pr = plane.GetComponent<MeshRenderer>();
        pr.sharedMaterial = pm;
        pr.shadowCastingMode = ShadowCastingMode.Off;
        plane.SetActive(false);
        lab.waterPlane = plane.transform;
        lab.waterPlaneMaterial = pm;

        // ground heights as a small texture: places the building blocks and shades flood depth
        var groundTex = GroundTexture(h, n);
        pm.SetTexture("_GroundTex", groundTex);
        pm.SetFloat("_GroundScale", heightScale);
        pm.SetFloat("_MetersToUnitsY", OureaSceneBuilder.MetersToUnitsY);
        pm.SetFloat("_MinM", lab.minM);
        EditorUtility.SetDirty(pm);
        lab.buildings = AddBuildings(groundTex, sizeX, sizeZ, heightScale, lab.minM, lab.maxM);
    }

    static Texture2D GroundTexture(float[] h, int n)
    {
        const int S = 1024;
        var t = new Texture2D(S, S, TextureFormat.RHalf, false, true) { name = "Ground", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[S * S];
        for (int r = 0; r < S; r++)          // texture row 0 = south
            for (int c = 0; c < S; c++)
                px[r * S + c] = new Color(H(h, n, c / (float)(S - 1), 1f - r / (float)(S - 1)), 0, 0, 1);
        t.SetPixels(px);
        t.Apply(false, false);
        SaveAsset(t, GenDir + "/GroundTex.asset");
        return t;
    }

    // Buildings from GHSL (pipeline/make_heights.py): two 100 m images, turned into blocks at load time.
    static BuildingField AddBuildings(Texture2D groundTex, float sizeX, float sizeZ, float heightScale, float minM, float maxM)
    {
        var hTex = ImportGrid("height.png");
        var bTex = ImportGrid("built.png");
        if (hTex == null || bTex == null) { Debug.LogWarning("Ourea: no handoff/buildings (run pipeline/make_heights.py); no building blocks."); return null; }
        var field = new GameObject("Buildings").AddComponent<BuildingField>();
        field.heightTex = hTex; field.builtTex = bTex; field.groundTex = groundTex;
        field.sizeX = sizeX; field.sizeZ = sizeZ; field.groundScale = heightScale;
        field.minM = minM; field.maxM = maxM;
        field.metersToUnitsY = OureaSceneBuilder.MetersToUnitsY;
        field.houseUnitsPerM = OureaSceneBuilder.MetersToUnitsY * 0.22f;   // houses less stretched than the ground
        var bm = new Material(Shader.Find("Ourea/Building"));   // turns red where the modeled water covers its ground
        bm.SetFloat("_Blend", OureaSceneBuilder.MetersToUnitsY * 0.3f);   // red over the first 30 cm of water
        SaveAsset(bm, GenDir + "/Building.mat");
        field.material = bm;
        return field;
    }

    static Texture2D ImportGrid(string file)
    {
        string src = Path.Combine(Handoff, "buildings", file);
        if (!File.Exists(src) || new FileInfo(src).Length < 100) return null;
        string dst = GenDir + "/buildings_" + file;
        File.Copy(src, Path.Combine(ProjectRoot, dst), true);
        AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
        imp.isReadable = true;                 // read on load to build the blocks
        imp.sRGBTexture = false;
        imp.mipmapEnabled = false;
        imp.npotScale = TextureImporterNPOTScale.None;
        imp.filterMode = FilterMode.Point;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.textureCompression = TextureImporterCompression.Uncompressed;
        var ps = imp.GetDefaultPlatformTextureSettings(); ps.format = TextureImporterFormat.R8; imp.SetPlatformTextureSettings(ps);
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
    }

    static void UseQualityTierForWebGL(string tierName)
    {
        int tier = System.Array.IndexOf(QualitySettings.names, tierName);
        if (tier < 0) return;
        var so = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var map = so.FindProperty("m_PerPlatformDefaultQuality");
        for (int i = 0; i < map.arraySize; i++)
        {
            var e = map.GetArrayElementAtIndex(i);
            if (e.FindPropertyRelative("first").stringValue == "WebGL") e.FindPropertyRelative("second").intValue = tier;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }

    // ---------- textures ----------
    static Texture2D ImportBasemap()
    {
        string src = Path.Combine(Handoff, "basemap", "s2cloudless.jpg");
        if (!File.Exists(src)) { Debug.LogWarning("Ourea: no handoff/basemap/s2cloudless.jpg (run pipeline/make_basemap.py); using height colours."); return null; }
        if (new FileInfo(src).Length < 1024) { Debug.LogWarning("Ourea: basemap is still an LFS pointer; use Ourea > Git > Get latest data."); return null; }
        string dst = GenDir + "/basemap.jpg";
        File.Copy(src, Path.Combine(ProjectRoot, dst), true);
        AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
        imp.maxTextureSize = 4096;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.mipmapEnabled = true;
        imp.anisoLevel = 16;
        imp.textureCompression = TextureImporterCompression.Compressed;
        imp.crunchedCompression = true;   // keeps the WebGL download small
        imp.compressionQuality = 80;
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
    }

    // World-space normals from the DEM at 2048², so fields, dikes and tree lines catch the light.
    static Texture2D MakeNormals(float[] h, int n, float sizeX, float sizeZ, float heightScale)
    {
        const int S = 2048;
        float dx = sizeX / (S - 1), dz = sizeZ / (S - 1);
        var px = new Color32[S * S];
        for (int r = 0; r < S; r++)          // r = 0 is the south edge (texture rows are bottom-up)
            for (int c = 0; c < S; c++)
            {
                float u = c / (float)(S - 1), v = 1f - r / (float)(S - 1); // v: 0 = north
                float hl = H(h, n, u - 1f / S, v), hr = H(h, n, u + 1f / S, v);
                float hs = H(h, n, u, v + 1f / S), hn = H(h, n, u, v - 1f / S);
                var nn = new Vector3(-(hr - hl) * heightScale / (2f * dx), 1f, -(hn - hs) * heightScale / (2f * dz)).normalized;
                px[r * S + c] = new Color32((byte)((nn.x * 0.5f + 0.5f) * 255), (byte)((nn.y * 0.5f + 0.5f) * 255), (byte)((nn.z * 0.5f + 0.5f) * 255), 255);
            }
        var tex = new Texture2D(S, S, TextureFormat.RGB24, false);
        tex.SetPixels32(px);
        string path = GenDir + "/normals.png";
        File.WriteAllBytes(Path.Combine(ProjectRoot, path), tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.sRGBTexture = false;
        imp.maxTextureSize = 2048;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.mipmapEnabled = true;
        imp.textureCompression = TextureImporterCompression.Compressed;
        imp.crunchedCompression = true;
        imp.compressionQuality = 80;
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static float H(float[] h, int n, float u, float vNorth)
    {
        int c = Mathf.Clamp(Mathf.RoundToInt(u * (n - 1)), 0, n - 1);
        int r = Mathf.Clamp(Mathf.RoundToInt(vNorth * (n - 1)), 0, n - 1);
        return h[r * n + c];
    }

    // ---------- meshes ----------
    static Mesh Skirt(float[] h, int n, float sizeX, float sizeZ, float heightScale)
    {
        const int R = 257;
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();
        // walk the four edges counter-clockwise seen from above; each step adds a top and bottom vertex
        System.Action<System.Func<float, Vector2>> side = edge =>
        {
            int start = verts.Count;
            for (int i = 0; i < R; i++)
            {
                var uv = edge(i / (float)(R - 1)); // u: 0 = west, v: 0 = south
                float y = H(h, n, uv.x, 1f - uv.y) * heightScale;
                verts.Add(new Vector3(uv.x * sizeX, y, uv.y * sizeZ));
                verts.Add(new Vector3(uv.x * sizeX, -BaseDepth, uv.y * sizeZ));
            }
            for (int i = 0; i < R - 1; i++)
            {
                int a = start + i * 2;
                tris.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
            }
        };
        side(t => new Vector2(t, 0f));      // south, west → east
        side(t => new Vector2(1f, t));      // east, south → north
        side(t => new Vector2(1f - t, 1f)); // north, east → west
        side(t => new Vector2(0f, 1f - t)); // west, north → south
        var m = new Mesh { name = "DioramaBase", indexFormat = IndexFormat.UInt32 };
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // ---------- assets ----------
    static Mesh SaveMesh(Mesh m, string path) { SaveAsset(m, path); return m; }

    static void SaveAsset(Object o, string path)
    {
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(o, path);
    }

    static Material LitMaterial(string path, Color c, float smooth)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smooth);
        SaveAsset(m, path);
        return m;
    }

    static Material BeamMaterial(string path, bool ring)
    {
        var m = new Material(Shader.Find("Ourea/Beam"));
        m.SetFloat("_Ring", ring ? 1f : 0f);
        m.SetFloat("_Intensity", ring ? 2.2f : 1.4f);
        SaveAsset(m, path);
        return m;
    }

    static VolumeProfile PostProfile()
    {
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        SaveAsset(p, GenDir + "/PostFX.asset");

        var tone = p.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.ACES);

        var bloom = p.Add<Bloom>(true);
        bloom.threshold.Override(1.05f);
        bloom.intensity.Override(0.5f);   // calm: highlights glow, the map does not
        bloom.scatter.Override(0.72f);
        bloom.highQualityFiltering.Override(true);

        var grade = p.Add<ColorAdjustments>(true);
        grade.postExposure.Override(0.35f);
        grade.contrast.Override(16f);
        grade.saturation.Override(14f);

        var smh = p.Add<ShadowsMidtonesHighlights>(true);
        smh.shadows.Override(new Vector4(0.92f, 0.98f, 1.08f, -0.02f));   // cool shadows
        smh.highlights.Override(new Vector4(1.06f, 1.0f, 0.92f, 0.02f));  // warm highlights

        var vig = p.Add<Vignette>(true);
        vig.intensity.Override(0.18f);
        vig.smoothness.Override(0.45f);

        foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
        EditorUtility.SetDirty(p);
        AssetDatabase.SaveAssets();
        return p;
    }
}
