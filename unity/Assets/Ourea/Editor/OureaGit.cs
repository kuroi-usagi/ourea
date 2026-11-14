using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Menu: Ourea > Git > ...  Runs git in the repo root so you don't need Terminal.
public static class OureaGit
{
    static string RepoRoot { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")); } }

    [MenuItem("Ourea/Git/Get latest data (pull + LFS)", priority = 20)]
    static void Pull()
    {
        Run("pull --ff-only");
        Run("lfs pull");
        AssetDatabase.Refresh();
    }

    [MenuItem("Ourea/Git/Push my commits", priority = 21)]
    static void Push() { Run("push origin HEAD"); }

    [MenuItem("Ourea/Git/Status", priority = 22)]
    static void Status() { Run("status -sb"); }

    static void Run(string args)
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var psi = new ProcessStartInfo("/usr/bin/git", args)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["PATH"] = home + "/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        EditorUtility.DisplayProgressBar("Ourea", "git " + args, 0.5f);
        try
        {
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                string msg = "git " + args + " (exit " + p.ExitCode + ")\n" + o + e;
                if (p.ExitCode == 0) Debug.Log(msg); else Debug.LogError(msg);
            }
        }
        catch (Exception ex) { Debug.LogError("git " + args + " failed: " + ex.Message); }
        finally { EditorUtility.ClearProgressBar(); }
    }
}
