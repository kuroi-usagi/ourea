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
        RenderSettings.fogDensity = 0.0009f;

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
        var field = new GameObject("Beacons").AddComponent<BeaconField>();
        field.viewer = app;
        field.beamMaterial = BeamMaterial(GenDir + "/Beam.mat", false);
        field.ringMaterial = BeamMaterial(GenDir + "/Ring.mat", true);
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
        imp.anisoLevel = 8;
        imp.textureCompression = TextureImporterCompression.CompressedHQ;
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
        imp.textureCompression = TextureImporterCompression.CompressedHQ;
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
        bloom.intensity.Override(0.9f);
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
        vig.intensity.Override(0.26f);
        vig.smoothness.Override(0.45f);

        foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
        EditorUtility.SetDirty(p);
        AssetDatabase.SaveAssets();
        return p;
    }
}
