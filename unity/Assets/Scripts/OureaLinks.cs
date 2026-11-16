using System.Runtime.InteropServices;
using UnityEngine;

// Links out of the 3D map: the routes to school and day care (web/public/unity/routes/, a Google map)
// in a panel over the app, and sharing a message to Zalo.
public static class OureaLinks
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void OureaRoutesPanel(string url);
    [DllImport("__Internal")] static extern int OureaShare(string text);
#else
    static void OureaRoutesPanel(string url) { Application.OpenURL("https://ourea-2wp.pages.dev/" + url); }
    static int OureaShare(string text) { GUIUtility.systemCopyBuffer = text; return 0; }
#endif

    public static void Routes(string passDate, string hamletId)
    {
        OureaRoutesPanel("routes/?pass=" + passDate + (string.IsNullOrEmpty(hamletId) ? "" : "&h=" + hamletId));
    }

    // true if the phone's share sheet opened; false if the text was copied and Zalo opened to paste it
    public static bool Share(string text) { return OureaShare(text) == 1; }

    // An IMGUI button that acts on the press (inside the browser's click, so pop-ups and sharing are allowed).
    public static bool Button(Rect r, string label, GUIStyle style)
    {
        var e = Event.current;
        bool pressed = e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition);
        GUI.Button(r, label, style);
        if (pressed) e.Use();
        return pressed;
    }
}
