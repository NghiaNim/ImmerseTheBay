using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEditor.SceneManagement;
using System.Linq;
using Unity.XR.XREAL;
using UnityEngine.XR.Hands.Samples.VisualizerSample;
public static class DevReferenceBuild {
 public static void Build() {
  PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.immersethebay.app");
  PlayerSettings.productName = "ImmerseTheBay Dev";
  PlayerSettings.Android.useCustomKeystore = false;
  const string reference = "Assets/Samples/XR Interaction Toolkit/2.6.5/Starter Assets/DemoScene.unity";
  const string devScene = "Assets/ImmerseTheBayHandsDemo.unity";
  var scene = EditorSceneManager.OpenScene(reference);
  var rig = scene.GetRootGameObjects().Single(root =>
      PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root).EndsWith("/XR Interaction Setup.prefab"));
  var hands = AssetDatabase.LoadAssetAtPath<GameObject>(
      "Packages/com.xreal.xr/Runtime/Prefabs/XR Interaction Hands Setup.prefab");
  if (!hands) throw new System.InvalidOperationException("Official XREAL hands rig missing.");
  PrefabUtility.ReplacePrefabAssetOfPrefabInstance(rig, hands,
      new PrefabReplacingSettings { objectMatchMode = ObjectMatchMode.ByHierarchy }, InteractionMode.AutomatedAction);
  foreach (var visualizer in rig.GetComponentsInChildren<HandVisualizer>(true))
  {
   visualizer.drawMeshes = true;
   visualizer.debugDrawJoints = false;
   visualizer.velocityType = HandVisualizer.VelocityType.None;
   PrefabUtility.RecordPrefabInstancePropertyModifications(visualizer);
  }
  foreach (var root in scene.GetRootGameObjects())
   if (root != rig && !root.GetComponent<Light>() &&
       !root.GetComponent<UnityEngine.XR.ARFoundation.ARSession>()) root.SetActive(false);
  if (!Object.FindAnyObjectByType<UnityEngine.XR.ARFoundation.ARSession>())
   new GameObject("AR Session", typeof(UnityEngine.XR.ARFoundation.ARSession));
  var xrOrigin = rig.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>();
  xrOrigin.CameraFloorOffsetObject.AddComponent<ImmerseTheBay.Tracking.HandInput>();
  var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
  screen.name = "Live practice video";
  screen.transform.position = new Vector3(0, -100, 0);
  screen.transform.localScale = new Vector3(1.05f, 0.59f, 1);
  Object.DestroyImmediate(screen.GetComponent<Collider>());
  var renderer = screen.GetComponent<Renderer>();
  renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/ImmerseIntegration/ImmersiveVisko.mat");

  var session = new GameObject("Live practice");
  session.SetActive(false);
  var video = session.AddComponent<ImmerseTheBay.Visko.ViskoSession>();
  video.screen = renderer;
  var context = session.AddComponent<ImmerseTheBay.Learning.LearningContext>();
  context.headCamera = xrOrigin.Camera;
  var overlay = session.AddComponent<ImmerseTheBay.Learning.LearningOverlay>();
  overlay.screen = renderer;
  overlay.headCamera = xrOrigin.Camera;
  var lesson = session.AddComponent<ImmerseTheBay.Learning.LearningAgentClient>();
  lesson.visko = video;
  lesson.context = context;
  lesson.overlay = overlay;
  // Hands-free answers: waits for speech, no recognizer tones or 3-second windows (AndroidSpeechToText is the fallback).
  lesson.speechProvider = session.AddComponent<ImmerseTheBay.Learning.PortalSpeechToText>();
  lesson.lessonId = "ja-cafe-demo-001";
  lesson.startImage = "lessons/images/cafe-order.jpg";
  var controls = session.AddComponent<ImmerseTheBay.Learning.DevLessonControls>();
  controls.video = video;
  controls.lesson = lesson;
  controls.head = xrOrigin.Camera;
  controls.planes = xrOrigin.gameObject.AddComponent<UnityEngine.XR.ARFoundation.ARPlaneManager>();
  controls.planes.requestedDetectionMode = UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Horizontal;
  controls.raycasts = xrOrigin.gameObject.AddComponent<UnityEngine.XR.ARFoundation.ARRaycastManager>();
  context.planeManager = controls.planes;
  renderer.enabled = false;
  session.SetActive(true);
  if (!EditorSceneManager.SaveScene(scene, devScene, true))
      throw new System.InvalidOperationException("Could not save dev scene.");
  var settings = AssetDatabase.LoadAssetAtPath<XREALSettings>("Assets/XR/Settings/XREALSettings.asset");
  settings.InitialInputSource = InputSource.ControllerAndHands;
  EditorUtility.SetDirty(settings);
  AssetDatabase.SaveAssets();
  var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
   scenes = new[] { devScene },
   locationPathName = "../official-hellomr/ImmerseTheBay-XRI-live-dev.apk",
   target = BuildTarget.Android, options = BuildOptions.None
  });
  Debug.Log("XRI_DEV_BUILD_RESULT=" + result.summary.result + " ERRORS=" + result.summary.totalErrors);
  EditorApplication.Exit(result.summary.result == BuildResult.Succeeded ? 0 : 1);
 }
}
