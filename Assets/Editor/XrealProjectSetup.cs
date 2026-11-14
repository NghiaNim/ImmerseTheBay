using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine.XR.Management;

// Applies the Android player settings the XREAL SDK 3.x "Getting Started" guide asks for (Air 2 Ultra / Beam Pro),
// creates a URP pipeline asset, and enables both input backends.
// Runs once automatically on first editor load; re-run via Tools/XREAL/Apply Android Settings.
[InitializeOnLoad]
public static class XrealProjectSetup
{
    const string DoneKey = "ImmerseTheBay.XrealSetupDone.v3";

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
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29; // Android 10+
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait; // per XREAL docs (phone/Beam Pro screen is the controller)
        PlayerSettings.colorSpace = ColorSpace.Linear;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.vSyncCount = 0; // "Don't Sync" per XREAL docs
        }

        SetActiveInputHandler(2); // 2 = Both (old Input Manager + Input System)
        EnsureUrpAsset();
        EnableXrealLoader();

        EditorPrefs.SetBool(DoneKey + "." + Application.dataPath, true);
        AssetDatabase.SaveAssets();
        Debug.Log("[ImmerseTheBay] XREAL Android settings applied. Next: run Project Validation > Fix All under " +
                  "XR Plug-in Management, then Tools/XREAL/Create Sensor Debug Scene.");
    }

    /// <summary>Ticks XREAL under XR Plug-in Management > Android (same as clicking the checkbox).</summary>
    static void EnableXrealLoader()
    {
        const string loader = "Unity.XR.XREAL.XREALXRLoader";
        if (System.Type.GetType(loader + ", Unity.XR.XREAL") == null)
        {
            Debug.LogWarning("[ImmerseTheBay] XREAL SDK not imported yet; skipping loader setup.");
            return;
        }
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
        {
            Directory.CreateDirectory("Assets/XR");
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }
        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (settings.Manager == null)
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        settings.InitManagerOnStart = true;
        bool ok = XRPackageMetadataStore.AssignLoader(settings.Manager, loader, BuildTargetGroup.Android);
        EditorUtility.SetDirty(perTarget);
        Debug.Log($"[ImmerseTheBay] XREAL loader for Android: {(ok ? "enabled" : "already enabled or failed")}");
    }

    static void SetActiveInputHandler(int value)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (assets.Length == 0) return;
        var so = new SerializedObject(assets[0]);
        var prop = so.FindProperty("activeInputHandler");
        if (prop == null || prop.intValue == value) return;
        prop.intValue = value;
        so.ApplyModifiedProperties();
        Debug.Log("[ImmerseTheBay] Active Input Handling set to Both. Restart the editor when Unity asks.");
    }

    static void EnsureUrpAsset()
    {
        if (GraphicsSettings.defaultRenderPipeline != null) return;
        const string dir = "Assets/Settings";
        Directory.CreateDirectory(dir);
        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(rendererData, $"{dir}/URP_Renderer.asset");
        var urp = UniversalRenderPipelineAsset.Create(rendererData);
        urp.renderScale = 1f;
        urp.msaaSampleCount = 4;
        AssetDatabase.CreateAsset(urp, $"{dir}/URP_Asset.asset");
        GraphicsSettings.defaultRenderPipeline = urp;
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = urp;
        }
    }
}
