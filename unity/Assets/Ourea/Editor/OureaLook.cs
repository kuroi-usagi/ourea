using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// The look layer on top of OureaSceneBuilder, styled after the Urban Sentinel atlas: a clean
// pale map toned from the satellite basemap, DEM hillshade, a stone diorama base, a light studio
// background, crisp neutral post-processing and solid score columns on every hamlet.
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
        baseRend.sharedMaterial = LitMaterial(GenDir + "/Base.mat", new Color(0.80f, 0.78f, 0.74f), 0.15f);

        // 3. Light studio: flat warm-grey backdrop, a whisper of distance fog, clean white sun
        var backdrop = new Color(0.905f, 0.898f, 0.878f);
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.86f, 0.88f, 0.90f);
        RenderSettings.ambientEquatorColor = new Color(0.78f, 0.78f, 0.76f);
        RenderSettings.ambientGroundColor = new Color(0.55f, 0.54f, 0.52f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = backdrop;
        RenderSettings.fogStartDistance = 1400f;
        RenderSettings.fogEndDistance = 3200f;

        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(52f, -32f, 0f);
            sun.color = new Color(1f, 0.98f, 0.95f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.55f;
            RenderSettings.sun = sun;
        }

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = backdrop;
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

        // 5. Pins stay small and clean; every scored hamlet gets a solid score column
        if (placesRoot != null && placesRoot.childCount > 0)
        {
            var pinMat = placesRoot.GetChild(0).GetComponent<Renderer>().sharedMaterial;
            pinMat.shader = Shader.Find("Universal Render Pipeline/Lit");
            pinMat.SetFloat("_Smoothness", 0.35f);
            EditorUtility.SetDirty(pinMat);
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

        var field = new GameObject("Score columns").AddComponent<BeaconField>();
        field.viewer = app;
        var colMat = LitMaterial(GenDir + "/Column.mat", Color.white, 0.25f);
        colMat.SetFloat("_Cull", 0f);  // two-sided, so prism winding never shows holes
        colMat.doubleSidedGI = true;
        field.columnMaterial = colMat;
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

    static VolumeProfile PostProfile()
    {
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        SaveAsset(p, GenDir + "/PostFX.asset");

        var tone = p.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.Neutral);

        var bloom = p.Add<Bloom>(true);
        bloom.threshold.Override(1.2f);
        bloom.intensity.Override(0.15f);

        var grade = p.Add<ColorAdjustments>(true);
        grade.postExposure.Override(0.1f);
        grade.contrast.Override(8f);
        grade.saturation.Override(6f);

        foreach (var c in p.components) AssetDatabase.AddObjectToAsset(c, p);
        EditorUtility.SetDirty(p);
        AssetDatabase.SaveAssets();
        return p;
    }
}
