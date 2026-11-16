using UnityEngine;

// SOUND ON / OFF, top right on every screen (inside the title bar on a phone). The choice is remembered.
public class OureaSoundButton : MonoBehaviour
{
    public static bool Muted;
    static Rect screenRect;
    GUIStyle style, styleSmall;

    public static void Load()
    {
        Muted = PlayerPrefs.GetInt("ourea_muted", 0) == 1;
        AudioListener.volume = Muted ? 0f : 1f;
    }

    // so a tap on the button doesn't also turn the map (OureaViewer.PointerOverUI)
    public static bool Contains(Vector2 guiPoint) { return screenRect.Contains(guiPoint); }

    void OnGUI()
    {
        var j = FindAnyObjectByType<GlobeJourney>();
        if (j != null && j.mode == GlobeJourney.Mode.Flying) { screenRect = Rect.zero; return; }
        GUI.depth = -30;
        if (style == null) { style = OureaTheme.Button(11); styleSmall = OureaTheme.Button(10); }
        float s = OureaTheme.UIScale, W = Screen.width / s;
        bool onMap = j == null || j.mode == GlobeJourney.Mode.Site;
        const float M = 16f;
        bool inBar = onMap && OureaTheme.Phone;
        float bw = inBar ? 92f : 104f, bh = inBar ? 20f : 28f;
        // laptop map: under the PROVISIONAL tag; phone map: inside the title bar, under its tag; front page: top right
        float y = !onMap ? M : inBar ? M + 34f : M + 38f;
        var r = new Rect(W - M - (inBar ? 10f : 0f) - bw, y, bw, bh);
        screenRect = new Rect(r.x * s, r.y * s, r.width * s, r.height * s);
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        var old = GUI.color;
        GUI.color = onMap ? OureaTheme.A(Color.white) : Color.white;
        if (GUI.Button(r, Muted ? "SOUND OFF" : "SOUND ON", inBar ? styleSmall : style))
        {
            Muted = !Muted;
            AudioListener.volume = Muted ? 0f : 1f;
            PlayerPrefs.SetInt("ourea_muted", Muted ? 1 : 0);
            PlayerPrefs.Save();
        }
        GUI.color = old;
        GUI.matrix = Matrix4x4.identity;
    }
}
