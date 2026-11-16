using System.Runtime.InteropServices;
using UnityEngine;

// Links out of the app: the Google Maps routes page (web/public/unity/routes/), on the same site.
public static class OureaLinks
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void OureaOpenOnRelease(string url);
#else
    static void OureaOpenOnRelease(string url) { Application.OpenURL("https://ourea-2wp.pages.dev/" + url); }
#endif

    // Call on MouseDown over the button (the tab opens when the click is released).
    public static void Routes(string passDate, string hamletId)
    {
        string q = "routes/?pass=" + passDate + (string.IsNullOrEmpty(hamletId) ? "" : "&h=" + hamletId);
        OureaOpenOnRelease(q);
    }

    // An IMGUI button that opens a link: returns true on the press, which is when the link is armed.
    public static bool Button(Rect r, string label, GUIStyle style)
    {
        var e = Event.current;
        bool pressed = e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition);
        GUI.Button(r, label, style);
        if (pressed) e.Use();
        return pressed;
    }
}
