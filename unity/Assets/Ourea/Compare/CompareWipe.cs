// Compare wipe: left of the line = what a camera satellite (Sentinel-2) saw nearest this NISAR pass,
// right of the line = Kason's radar view. Owner: Tina.
// Self-installing: attaches itself to the OureaViewer at runtime, so no scene edits are needed
// (keeps Ourea.unity conflict-free while Kason works in it). Press C or the "Compare" button.
// Photos live in Resources/Optical/<pass date>.png, cloud % in Resources/Optical/optical.json
// (copied from handoff/layers/optical, made by pipeline/make_optical.py).
using System;
using System.Collections.Generic;
using UnityEngine;

public class CompareWipe : MonoBehaviour
{
    [Range(0.05f, 0.95f)] public float wipe = 0.5f;
    public bool on;

    OureaViewer viewer;
    GameObject opticalGO;
    Material mat;
    readonly Dictionary<string, Texture2D> photos = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, float> cloud = new Dictionary<string, float>();
    readonly Dictionary<string, string> photoDate = new Dictionary<string, string>();
    bool dragging;
    int shownPass = -1;
    GUIStyle sTag, sBtn;

    [Serializable] class OptRow { public string pass; public string image_date; public float cloud_pct; }
    [Serializable] class OptList { public OptRow[] rows; }

    // Retired (Kason, Nov 14): the Compare wipe now lives in the What-if panel (Scripts/FloodLab.cs,
    // "Compare with the camera photo"). Self-install is off so the app doesn't show two wipes.
    // Set this to true to bring this version back; the photos are in ../OpticalSource (move them
    // back to Resources/Optical first).
    const bool SelfInstall = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!SelfInstall) return;
        var v = FindFirstObjectByType<OureaViewer>();
        if (v != null && v.GetComponent<CompareWipe>() == null) v.gameObject.AddComponent<CompareWipe>();
    }

    void Start()
    {
        viewer = GetComponent<OureaViewer>();
        var json = Resources.Load<TextAsset>("Optical/optical");
        if (json != null)
        {
            var list = JsonUtility.FromJson<OptList>("{\"rows\":" + json.text + "}");
            foreach (var r in list.rows) { cloud[r.pass] = r.cloud_pct; photoDate[r.pass] = r.image_date; }
        }
        var src = viewer != null ? viewer.deltaRenderer : null;
        var mf = src != null ? src.GetComponent<MeshFilter>() : null;
        var sh = Shader.Find("Ourea/Optical");
        if (mf == null || sh == null) { Debug.LogWarning("CompareWipe: delta mesh or Ourea/Optical shader not found."); enabled = false; return; }
        opticalGO = new GameObject("Optical (Compare wipe)");
        opticalGO.transform.SetParent(src.transform, false);
        opticalGO.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
        mat = new Material(sh);
        var mr = opticalGO.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        opticalGO.SetActive(false);
    }

    Texture2D Photo(string date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        if (!photos.TryGetValue(date, out var t)) { t = Resources.Load<Texture2D>("Optical/" + date); photos[date] = t; }
        return t;
    }

    string CurrentDate()
    {
        if (viewer == null || viewer.passes == null || viewer.passes.Count == 0) return null;
        int i = Mathf.Clamp(viewer.currentPass, 0, viewer.passes.Count - 1);
        return viewer.passes[i].date;
    }

    void Update()
    {
        if (mat == null) return;
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.cKey.wasPressedThisFrame) on = !on;
#else
        if (Input.GetKeyDown(KeyCode.C)) on = !on;
#endif
        if (viewer != null && viewer.orbit != null) viewer.orbit.enabled = !dragging;
        var date = CurrentDate();
        var tex = Photo(date);
        opticalGO.SetActive(on && tex != null);
        if (!on || tex == null) return;
        if (viewer.currentPass != shownPass) { mat.SetTexture("_MainTex", tex); shownPass = viewer.currentPass; }
        mat.SetFloat("_WipeX", wipe);
    }

    void OnGUI()
    {
        if (mat == null) return;
        if (sBtn == null)
        {
            sBtn = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            sTag = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            sTag.normal.textColor = Color.white;
        }
        var date = CurrentDate();
        bool hasPhoto = Photo(date) != null;
        var bRect = new Rect(Screen.width / 2f - 110, 12, 220, 28);
        GUI.enabled = hasPhoto;
        if (GUI.Button(bRect, on ? "Compare: on  (C)" : (hasPhoto ? "Compare camera vs radar  (C)" : "No photo for this pass"), sBtn)) on = !on;
        GUI.enabled = true;
        if (!on || !hasPhoto) return;

        float x = wipe * Screen.width;
        GUI.color = new Color(0.37f, 0.83f, 0.88f, 1f);
        GUI.DrawTexture(new Rect(x - 1, 0, 2, Screen.height), Texture2D.whiteTexture);
        var grip = new Rect(x - 18, Screen.height / 2f - 18, 36, 36);
        GUI.DrawTexture(grip, Texture2D.whiteTexture);
        GUI.color = Color.white;

        string c = cloud.TryGetValue(date, out var pct) ? $"{pct:0}% cloud" : "cloud n/a";
        string d = photoDate.TryGetValue(date, out var pd) ? pd : date;
        GUI.Label(new Rect(Mathf.Max(8, x - 330), 52, 320, 26), $"Camera satellite (Sentinel-2, {d}) · {c}", sTag);
        GUI.Label(new Rect(Mathf.Min(Screen.width - 328, x + 8), 52, 320, 26), $"NISAR radar · {date} · sees through cloud", sTag);

        var e = Event.current;
        if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { dragging = true; e.Use(); }
        if (dragging && e.type == EventType.MouseDrag) { wipe = Mathf.Clamp(e.mousePosition.x / Screen.width, 0.05f, 0.95f); e.Use(); }
        if (e.type == EventType.MouseUp) dragging = false;
    }

    public bool IsDragging => dragging;
}
