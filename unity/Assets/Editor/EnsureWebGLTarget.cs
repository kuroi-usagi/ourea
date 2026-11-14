using UnityEditor;
using UnityEngine;

// The active build target lives in Library/ (gitignored), so a fresh clone
// would open on the default desktop target. Switch to WebGL on load.
[InitializeOnLoad]
static class EnsureWebGLTarget
{
    static EnsureWebGLTarget()
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            return;

        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
        {
            Debug.LogWarning("Ourea builds for WebGL. Add 'Web Build Support' to this editor in Unity Hub.");
            return;
        }

        EditorApplication.delayCall += () =>
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
    }
}
