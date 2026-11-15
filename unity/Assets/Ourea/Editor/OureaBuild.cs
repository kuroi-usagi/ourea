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
        if (report.summary.result == BuildResult.Succeeded) FillWindow(Path.Combine(OutDir, "index.html"));
        var s = report.summary;
        Debug.Log("Ourea: WebGL build " + s.result + " → " + OutDir + " (" + (s.totalSize / 1048576f).ToString("0.0") + " MB, " + s.totalErrors + " errors)");
        if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }

    // Unity's default page draws a fixed 960x600 box. For a live demo the map should fill the
    // window: stretch the canvas, drop the footer, and render at up to 2x pixel density.
    static void FillWindow(string indexPath)
    {
        if (!File.Exists(indexPath)) return;
        var html = File.ReadAllText(indexPath);
        html = html.Replace("<title>Unity Web Player | Ourea</title>", "<title>Ourea · Mekong flood watch</title>");
        html = html.Replace("</head>",
            "<style>html,body{margin:0;height:100%;overflow:hidden;background:#e7e5e0}" +
            "#unity-container{position:fixed!important;inset:0;left:0!important;top:0!important;transform:none!important;width:100%;height:100%}" +
            "#unity-fullscreen-container,#unity-canvas{width:100%!important;height:100%!important;background:#e7e5e0}" +
            "#unity-footer{display:none}</style>\n  </head>");
        html = html.Replace("fullscreenContainer.style.width = \"960px\";", "");
        html = html.Replace("fullscreenContainer.style.height = \"600px\";", "");
        html = html.Replace("// config.devicePixelRatio = 1;", "config.devicePixelRatio = Math.min(window.devicePixelRatio || 1, 2);");
        File.WriteAllText(indexPath, html);
    }
}
