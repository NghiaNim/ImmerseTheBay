using System.IO;
using ImmerseTheBay.Audio;
using ImmerseTheBay.Media;
using ImmerseTheBay.Net;
using ImmerseTheBay.SensorDebug;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Tools/XREAL/Create Sensor Debug Scene: builds a self-contained test scene with an XR Origin, AR Session,
/// plane / mesh / anchor managers, the sensor HUD, a spatial media screen, audio + mic visualisers and the network client.
/// Prefer replacing the XR Origin with XREAL's "XR Interaction Hands Setup" prefab once the SDK samples are imported.
/// </summary>
public static class SensorDebugSceneBuilder
{
    const string ScenePath = "Assets/Scenes/SensorDebug.unity";
    const string MatDir = "Assets/Materials";

    [MenuItem("Tools/XREAL/Create Sensor Debug Scene")]
    public static void Create()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory(MatDir);
        Directory.CreateDirectory("Assets/Prefabs");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- AR Session
        var session = new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

        // --- XR rig: XREAL's official "XR Interaction Hands Setup" (XRI hands + controller, poke / near-far / ray,
        //     EventSystem, Input Action Manager, XR Interaction Manager). Fallback: minimal hand-built origin.
        var (originGo, origin, offset, cam) = CreateRig();
        cam.clearFlags = CameraClearFlags.SolidColor;   // black = transparent on optical see-through glasses
        cam.backgroundColor = Color.black;
        cam.nearClipPlane = 0.1f;
        cam.fieldOfView = 25f;

        // Official system notification popups (battery, temperature, tracking lost, ...)
        var notif = AssetDatabase.LoadAssetAtPath<GameObject>($"{XrealPrefabs}/NotificationListener.prefab");
        if (notif) PrefabUtility.InstantiatePrefab(notif);

        // --- AR managers (must live on / under the XR Origin)
        var planes = GetOrAdd<ARPlaneManager>(originGo);
        planes.planePrefab = CreatePlanePrefab();
        GetOrAdd<ARAnchorManager>(originGo);
        originGo.AddComponent<ImmerseTheBay.Anchors.SpatialAnchorController>();
        var meshGo = new GameObject("AR Mesh Manager");
        meshGo.transform.SetParent(originGo.transform, false);
        var meshMgr = meshGo.AddComponent<ARMeshManager>();
        meshMgr.meshPrefab = CreateMeshChunkPrefab().GetComponent<MeshFilter>();
        meshMgr.density = 0.5f;

        // --- Sensor collector lives in tracking space so hand joint poses can be applied as local positions
        var collectorGo = new GameObject("Sensor Collector");
        collectorGo.transform.SetParent(offset.transform, false);
        var collector = collectorGo.AddComponent<SensorDataCollector>();
        collector.arSession = session.GetComponent<ARSession>();
        collector.planeManager = planes;
        collector.meshManager = meshMgr;
        collector.anchorManager = originGo.GetComponent<ARAnchorManager>();

        var hud = new GameObject("Sensor HUD").AddComponent<SensorDataHud>();
        hud.collector = collector;
        hud.transform.position = new Vector3(0, 0, 1.4f);

        // --- Spatial media screen (to the left)
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "Spatial Media Screen";
        Object.DestroyImmediate(screen.GetComponent<Collider>());
        screen.transform.position = new Vector3(-1.3f, 0, 1.8f);
        screen.transform.rotation = Quaternion.Euler(0, -35, 0);
        screen.GetComponent<MeshRenderer>().sharedMaterial = SaveMat(new Material(Shader.Find("ImmerseTheBay/StereoUnlit")), "StereoScreen");
        var player = screen.AddComponent<SpatialMediaPlayer>();
        player.source = ""; // e.g. "demo_sbs.mp4" in StreamingAssets, or an https URL
        player.layout = StereoLayout.SideBySide;

        // --- Audio visualisers (to the right): one for media audio, one for the microphone
        var lineMat = SaveMat(new Material(Shader.Find("Sprites/Default")), "WaveLine");
        var barMat = SaveMat(new Material(Shader.Find("Universal Render Pipeline/Unlit")), "SpectrumBar");

        var musicVis = new GameObject("Music Visualizer").AddComponent<AudioVisualizer>();
        musicVis.transform.position = new Vector3(1.3f, 0.25f, 1.8f);
        musicVis.transform.rotation = Quaternion.Euler(0, 35, 0);
        musicVis.input = AudioVisualizer.Input.AudioSource;
        var mediaAudio = screen.GetComponent<AudioSource>();
        if (!mediaAudio) mediaAudio = screen.AddComponent<AudioSource>();
        mediaAudio.playOnAwake = false;
        player.audioSource = mediaAudio;
        musicVis.audioSource = mediaAudio;
        musicVis.lineMaterial = lineMat; musicVis.barMaterial = barMat;

        var micGo = new GameObject("Microphone");
        var mic = micGo.AddComponent<MicrophoneCapture>();
        var micVis = new GameObject("Mic Visualizer").AddComponent<AudioVisualizer>();
        micVis.transform.position = new Vector3(1.3f, -0.35f, 1.8f);
        micVis.transform.rotation = Quaternion.Euler(0, 35, 0);
        micVis.input = AudioVisualizer.Input.Microphone;
        micVis.microphone = mic;
        micVis.waveColor = new Color(1f, 0.85f, 0.3f);
        micVis.lineMaterial = lineMat; micVis.barMaterial = barMat;

        // --- Network
        var net = new GameObject("Network").AddComponent<NetClient>();
        net.webSocketUrl = "wss://echo.websocket.org"; // public echo server for testing; replace with your own

        // --- Hands, content placement (2D video / image / 3D model), image tracking, recenter, poke menu
        GetOrAdd<ImmerseTheBay.Tracking.HandInput>(offset);
        var imageMgr = GetOrAdd<ARTrackedImageManager>(originGo);
        imageMgr.enabled = false; // ImageTrackingController enables it once its runtime library is built
        collector.imageManager = imageMgr;

        var contentGo = new GameObject("Content");
        var spawner = contentGo.AddComponent<ImmerseTheBay.Content.ContentSpawner>();
        spawner.anchorManager = originGo.GetComponent<ARAnchorManager>();
        spawner.screenMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/StereoScreen.mat");
        spawner.primitiveMaterial = SaveMat(new Material(Shader.Find("Universal Render Pipeline/Lit")), "ContentPrimitive");
        contentGo.AddComponent<ImmerseTheBay.Content.HandManipulator>().spawner = spawner;
        var recenter = contentGo.AddComponent<ImmerseTheBay.Tracking.RecenterController>();
        originGo.AddComponent<ImmerseTheBay.Tracking.ImageTrackingController>().spawner = spawner;

        var menu = new GameObject("Poke Menu").AddComponent<ImmerseTheBay.Content.PokeMenu>();
        menu.spawner = spawner;
        menu.recenter = recenter;
        menu.hud = hud.gameObject;
        var xreal = contentGo.AddComponent<ImmerseTheBay.Xreal.XrealFeatures>();
        xreal.meshManager = meshMgr;
        xreal.recenter = recenter;
        menu.xreal = xreal;
        imageMgr.referenceLibrary = EnsureReferenceImageLibrary();

        // --- Visko (Reactor) live video screen, synced with the web portal (portal/)
        var viskoScreen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        viskoScreen.name = "Visko Screen";
        Object.DestroyImmediate(viskoScreen.GetComponent<Collider>());
        viskoScreen.transform.position = new Vector3(0, 0.15f, 2.6f);
        viskoScreen.transform.localScale = new Vector3(1.6f, 0.9f, 1);
        viskoScreen.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/StereoScreen.mat");
        var visko = new GameObject("Visko Session").AddComponent<ImmerseTheBay.Visko.ViskoSession>();
        visko.screen = viskoScreen.GetComponent<MeshRenderer>();
        menu.visko = visko;

        // --- First-person view of the glasses to the portal (/cast, viewer at portal/cast.html)
        var caster = new GameObject("First Person Caster").AddComponent<ImmerseTheBay.Cast.FirstPersonCaster>();
        menu.caster = caster;

        IncludeGltfShaders();

        EditorSceneManager.SaveScene(scene, ScenePath);
        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        list.RemoveAll(s => s.path == ScenePath);
        list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log($"[ImmerseTheBay] Created {ScenePath} and set it as the first build scene.");
    }

    static T GetOrAdd<T>(GameObject go) where T : Component => go.TryGetComponent<T>(out var c) ? c : go.AddComponent<T>();

    const string XrealPrefabs = "Packages/com.xreal.xr/Runtime/Prefabs";

    static (GameObject originGo, XROrigin origin, GameObject offset, Camera cam) CreateRig()
    {
        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{XrealPrefabs}/XR Interaction Hands Setup.prefab");
        if (rigPrefab)
        {
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            var o = rig.GetComponentInChildren<XROrigin>(true);
            if (o && o.Camera)
            {
                o.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
                Debug.Log("[ImmerseTheBay] Using official XREAL rig: XR Interaction Hands Setup");
                var off = o.CameraFloorOffsetObject ? o.CameraFloorOffsetObject : o.Camera.transform.parent.gameObject;
                return (o.gameObject, o, off, o.Camera);
            }
            Debug.LogWarning("[ImmerseTheBay] Official rig has no XROrigin/Camera; falling back to minimal rig");
            Object.DestroyImmediate(rig);
        }

        var originGo = new GameObject("XR Origin", typeof(XROrigin));
        var origin = originGo.GetComponent<XROrigin>();
        var offset = new GameObject("Camera Offset");
        offset.transform.SetParent(originGo.transform, false);
        var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(TrackedPoseDriver));
        camGo.tag = "MainCamera";
        camGo.transform.SetParent(offset.transform, false);
        var cam = camGo.GetComponent<Camera>();
        origin.Camera = cam;
        origin.CameraFloorOffsetObject = offset;
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
        return (originGo, origin, offset, cam);
    }

    /// <summary>
    /// glTFast picks its shaders at runtime with Shader.Find, so they must be in "Always Included Shaders"
    /// or runtime-loaded models render pink / invisible in a build.
    /// </summary>
    static void IncludeGltfShaders()
    {
        string[] names =
        {
            "Shader Graphs/glTF-pbrMetallicRoughness", "Shader Graphs/glTF-pbrSpecularGlossiness", "Shader Graphs/glTF-unlit",
        };
        var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
        if (!gs) return;
        var so = new SerializedObject(gs);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        foreach (var n in names)
        {
            var sh = Shader.Find(n);
            if (!sh) { Debug.LogWarning($"[ImmerseTheBay] glTFast shader '{n}' not found (package not imported yet?)"); continue; }
            bool present = false;
            for (int i = 0; i < arr.arraySize; i++)
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { present = true; break; }
            if (present) continue;
            arr.InsertArrayElementAtIndex(arr.arraySize);
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
        }
        so.ApplyModifiedProperties();
    }

    /// <summary>XREAL has no runtime image library, so image tracking uses this editor asset. Add images + physical size here.</summary>
    static UnityEngine.XR.ARSubsystems.XRReferenceImageLibrary EnsureReferenceImageLibrary()
    {
        const string path = "Assets/Settings/ReferenceImages.asset";
        var lib = AssetDatabase.LoadAssetAtPath<UnityEngine.XR.ARSubsystems.XRReferenceImageLibrary>(path);
        if (lib) return lib;
        Directory.CreateDirectory("Assets/Settings");
        lib = ScriptableObject.CreateInstance<UnityEngine.XR.ARSubsystems.XRReferenceImageLibrary>();
        AssetDatabase.CreateAsset(lib, path);
        return lib;
    }

    static Material SaveMat(Material m, string name)
    {
        string path = $"{MatDir}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing) return existing;
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Material TransparentUnlit(string name, Color c)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        m.SetFloat("_Surface", 1); // transparent
        m.SetFloat("_Blend", 0);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.SetColor("_BaseColor", c);
        return SaveMat(m, name);
    }

    static GameObject CreateMeshChunkPrefab()
    {
        const string path = "Assets/Prefabs/MeshChunk.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing) return existing;
        var go = new GameObject("MeshChunk", typeof(MeshFilter), typeof(MeshRenderer));
        go.GetComponent<MeshRenderer>().sharedMaterial = TransparentUnlit("SpatialMesh", new Color(0.2f, 0.8f, 1f, 0.25f));
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return prefab;
    }

    static GameObject CreatePlanePrefab()
    {
        const string path = "Assets/Prefabs/ARPlane.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing) return existing;
        var go = new GameObject("ARPlane", typeof(ARPlane), typeof(ARPlaneMeshVisualizer), typeof(MeshFilter), typeof(MeshRenderer), typeof(LineRenderer));
        go.GetComponent<MeshRenderer>().sharedMaterial = TransparentUnlit("Plane", new Color(1f, 0.6f, 0.2f, 0.2f));
        var lr = go.GetComponent<LineRenderer>();
        lr.widthMultiplier = 0.005f;
        lr.sharedMaterial = SaveMat(new Material(Shader.Find("Sprites/Default")), "WaveLine");
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return prefab;
    }
}
