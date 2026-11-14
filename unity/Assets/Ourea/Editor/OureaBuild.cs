using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Menu: Ourea > Build WebGL for web
// Builds Scenes/Ourea.unity for WebGL into ../web/public/unity/, where the web app (Asher)
// serves and links it. Gzip with decompression fallback, so it loads from any static host
// (Cloudflare Pages, a plain file server) without special Content-Encoding headers.
// Command line: Unity -batchmode -quit -projectPath unity -buildTarget WebGL -executeMethod OureaBuild.BuildWebGL
public static class OureaBuild
{
    const string Scene = "Assets/Scenes/Ourea.unity";

    static string OutDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OUREA_WEBGL_OUT");
            return !string.IsNullOrEmpty(env) ? env
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "web", "public", "unity"));
        }
    }

    [MenuItem("Ourea/Build WebGL for web", priority = 4)]
    public static void BuildWebGL()
    {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.template = "APPLICATION:Default";
        PlayerSettings.runInBackground = true;

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = OutDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });
        var s = report.summary;
        Debug.Log("Ourea: WebGL build " + s.result + " → " + OutDir + " (" + (s.totalSize / 1048576f).ToString("0.0") + " MB, " + s.totalErrors + " errors)");
        if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }
}
