using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Menu: Ourea > Reset Game view zoom
// The Game view's "Scale" slider magnifies the rendered image (at 10x every pixel is a big
// blurry block). This snaps every open Game view back to 1x so you see the real resolution.
public static class OureaGameView
{
    [MenuItem("Ourea/Reset Game view zoom", priority = 3)]
    public static void ResetZoom()
    {
        var type = Type.GetType("UnityEditor.GameView,UnityEditor");
        var snap = type != null ? type.GetMethod("SnapZoom", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) : null;
        if (snap == null) { Debug.LogWarning("Ourea: this Unity version has no GameView.SnapZoom; drag the Game view Scale slider to 1x."); return; }
        foreach (var view in Resources.FindObjectsOfTypeAll(type))
        {
            snap.Invoke(view, new object[] { 1f });
            ((EditorWindow)view).Repaint();
        }
        Debug.Log("Ourea: Game view zoom set to 1x.");
    }
}
