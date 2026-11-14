using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

/// <summary>
/// Tools/XREAL/Import Official Samples: imports the package samples the official XREAL setup builds on
/// (XRI Starter Assets + Hands Interaction Demo, XR Hands HandVisualizer, XREAL Interaction Basics + AR Features)
/// into Assets/Samples. Batch: -executeMethod OfficialSamplesImporter.ImportAll
/// </summary>
public static class OfficialSamplesImporter
{
    static readonly (string package, string[] samples)[] k_Wanted =
    {
        ("com.unity.xr.interaction.toolkit", new[] { "Starter Assets", "Hands Interaction Demo" }),
        ("com.unity.xr.hands", new[] { "HandVisualizer" }),
        ("com.xreal.xr", new[] { "Interaction Basics", "AR Features" }),
    };

    [MenuItem("Tools/XREAL/Import Official Samples")]
    public static void ImportAll()
    {
        foreach (var (package, wanted) in k_Wanted)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(package);
            if (info == null) { Debug.LogWarning($"[ImmerseTheBay] package {package} not found"); continue; }
            var samples = Sample.FindByPackage(package, info.version).ToList();
            foreach (var name in wanted)
            {
                var s = samples.FirstOrDefault(x => x.displayName == name);
                if (string.IsNullOrEmpty(s.displayName)) { Debug.LogWarning($"[ImmerseTheBay] sample '{name}' not in {package}@{info.version} (have: {string.Join(", ", samples.Select(x => x.displayName))})"); continue; }
                if (s.isImported) { Debug.Log($"[ImmerseTheBay] sample already imported: {package}/{name}"); continue; }
                bool ok = s.Import(Sample.ImportOptions.HideImportWindow | Sample.ImportOptions.OverridePreviousImports);
                Debug.Log($"[ImmerseTheBay] import {package}/{name}: {(ok ? "ok" : "FAILED")} -> {s.importPath}");
            }
        }
        AssetDatabase.Refresh();
    }

    /// <summary>Path of the official XRI hands rig prefab, or null if the samples are not imported.</summary>
    public static string FindHandsSetupPrefab()
    {
        var guid = AssetDatabase.FindAssets("\"XR Interaction Hands Setup\" t:Prefab", new[] { "Assets/Samples" }).FirstOrDefault();
        if (guid == null) guid = AssetDatabase.FindAssets("XR Interaction Hands Setup t:Prefab").FirstOrDefault();
        return guid == null ? null : AssetDatabase.GUIDToAssetPath(guid);
    }
}
