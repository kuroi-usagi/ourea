using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: Ourea > Capture 4K stills
// Renders the Ourea scene camera at 3840x2160 for every pass into docs/stills/ (for slides and video).
// The in-app UI is not included; it's a clean shot of the map.
public static class OureaCapture
{
    const int W = 3840, Hgt = 2160;

    [MenuItem("Ourea/Capture 4K stills", priority = 2)]
    public static void CaptureAll()
    {
        var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "docs", "stills"));
        Directory.CreateDirectory(outDir);
        if (EditorSceneManager.GetActiveScene().path != "Assets/Scenes/Ourea.unity")
            EditorSceneManager.OpenScene("Assets/Scenes/Ourea.unity");

        var viewer = Object.FindAnyObjectByType<OureaViewer>();
        var beacons = Object.FindAnyObjectByType<BeaconField>();
        var cam = Camera.main;
        var mat = viewer.deltaRenderer.sharedMaterial;
        var oldA = mat.GetTexture("_MaskA"); var oldB = mat.GetTexture("_MaskB");
        var oldPos = cam.transform.position; var oldRot = cam.transform.rotation;

        // Cinematic angle: lower than the app's working view, so the haze and horizon show.
        var b = viewer.deltaRenderer.bounds;
        float size = Mathf.Max(b.size.x, b.size.z);
        Pose(cam, b.center, size * 0.80f, 33f, 18f);

        for (int i = 0; i < viewer.passes.Count; i++)
        {
            var p = viewer.passes[i];
            mat.SetTexture("_MaskA", p.mask);
            mat.SetTexture("_MaskB", p.mask);
            mat.SetFloat("_Blend", 0f);
            viewer.currentPass = i;
            if (beacons != null) beacons.Refresh(i);
            string file = Path.Combine(outDir, "ourea_" + p.date + "_4k.png");
            Render(cam, file);
            Debug.Log("Ourea: wrote " + file);
        }

        // Hero close-up over the river on the latest pass
        Pose(cam, b.center + new Vector3(-size * 0.18f, 0f, -size * 0.08f), size * 0.30f, 24f, 40f);
        Render(cam, Path.Combine(outDir, "ourea_hero_4k.png"));

        cam.transform.SetPositionAndRotation(oldPos, oldRot);
        mat.SetTexture("_MaskA", oldA); mat.SetTexture("_MaskB", oldB);
        if (beacons != null) foreach (Transform t in beacons.transform) Object.DestroyImmediate(t.gameObject);
    }

    static void Pose(Camera cam, Vector3 target, float distance, float pitch, float yaw)
    {
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.SetPositionAndRotation(target - rot * Vector3.forward * distance, rot);
    }

    public static void Render(Camera cam, string file)
    {
        var rt = new RenderTexture(W, Hgt, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        var old = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(W, Hgt, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, W, Hgt), 0, 0);
        tex.Apply();
        File.WriteAllBytes(file, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = old;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }
}
