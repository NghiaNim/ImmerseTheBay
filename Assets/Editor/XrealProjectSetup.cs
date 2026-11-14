using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

// Applies Android build settings required by the XREAL SDK (Air 2 Ultra).
// Runs once automatically on first editor load; re-run via menu Tools/XREAL/Apply Android Settings.
[InitializeOnLoad]
public static class XrealProjectSetup
{
    const string DoneKey = "ImmerseTheBay.XrealSetupDone";

    static XrealProjectSetup()
    {
        if (EditorPrefs.GetBool(DoneKey + "." + Application.dataPath)) return;
        EditorApplication.delayCall += Apply;
    }

    [MenuItem("Tools/XREAL/Apply Android Settings")]
    public static void Apply()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var android = NamedBuildTarget.Android;
        PlayerSettings.companyName = "ImmerseTheBay";
        PlayerSettings.productName = "ImmerseTheBay";
        PlayerSettings.SetApplicationIdentifier(android, "com.immersethebay.app");
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.colorSpace = ColorSpace.Linear;

        EditorPrefs.SetBool(DoneKey + "." + Application.dataPath, true);
        AssetDatabase.SaveAssets();
        Debug.Log("[ImmerseTheBay] XREAL Android settings applied. Next: import XREAL SDK tarball and enable XREAL in XR Plug-in Management (Android).");
    }
}
