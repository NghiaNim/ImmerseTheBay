#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ImmerseTheBay.Learning.Editor
{
    /// <summary>Renders an unsaved, device-free scene. Never changes the shipped scene.</summary>
    public static class LearningVisualChecks
    {
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Preview Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 0, -2);
            camera.orthographic = true; camera.orthographicSize = 0.54f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.035f, 0.03f);
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.transform.localScale = new Vector3(1.6f, 0.9f, 1);
            var shader = Shader.Find("ImmerseTheBay/ImmersiveVisko");
            if (!shader) throw new Exception("Immersive shader missing");
            var material = new Material(shader);
            var photo = new Texture2D(2, 2);
            if (!photo.LoadImage(File.ReadAllBytes("Assets/StreamingAssets/lessons/images/konbini-checkout-original.png"))) throw new Exception("Reference photograph could not decode");
            material.SetTexture("_EnvironmentTex", photo);
            material.SetFloat("_HasVideo", 0);
            screen.GetComponent<Renderer>().sharedMaterial = material;
            var overlay = new GameObject("Learning Preview").AddComponent<LearningOverlay>();
            overlay.screen = screen.GetComponent<Renderer>(); overlay.headCamera = camera;
            overlay.Show(new LearningOverlayData { jp = "すみません、水を一本買いたいのですが。", kana = "すみません、みずをいっぽんかいたいのですが。", en = "Excuse me, I would like a bottle of water.", zh = "不好意思，我想买一瓶水。" }, null, false);
            overlay.SetConversationState(true, false, "Listening · 日本語 / English");
            overlay.SetRoute(true, "水を買う");
            overlay.SetClerkCaption("はい、お水ですね。");
            overlay.SendMessage("LateUpdate");
            Canvas.ForceUpdateCanvases();
            var target = new RenderTexture(1440, 810, 24);
            camera.targetTexture = target; camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = target;
            var image = new Texture2D(1440, 810, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1440, 810), 0, 0); image.Apply();
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes("Logs/unity-conversation-preview.png", image.EncodeToPNG());
            RenderTexture.active = previous; camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(image); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            Debug.Log("[Learning] Visual preview saved: Logs/unity-conversation-preview.png (reference image; not live media).");
        }
    }
}
#endif
