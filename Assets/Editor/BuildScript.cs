using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Tools/XREAL/Build APK (or batch: -executeMethod BuildScript.BuildAndroid). Output: Builds/ImmerseTheBay.apk.
/// Install on Beam Pro: adb install -r Builds/ImmerseTheBay.apk
/// </summary>
public static class BuildScript
{
    const string Output = "Builds/ImmerseTheBay.apk";

    [MenuItem("Tools/XREAL/Build APK")]
    public static void BuildAndroid()
    {
        Directory.CreateDirectory("Builds");
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/SensorDebug.unity" };
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Output,
            target = BuildTarget.Android,
            options = BuildOptions.Development,
        });
        var s = report.summary;
        Debug.Log($"[ImmerseTheBay] Build {s.result}: {Output} {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalTime.TotalSeconds:F0}s");
        if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }
}
