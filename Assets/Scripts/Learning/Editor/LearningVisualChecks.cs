#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

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
            overlay.SetKanaVisible(false);
            overlay.SetTranslationVisible(true);
            var lesson = new LearningOverlayData
            {
                jp = "すみません、水を一本買いたいのですが。",
                romaji = "Sumimasen, mizu o ippon kaitai no desu ga.",
                kana = "すみません、みずをいっぽんかいたいのですが。",
                en = "Excuse me, I would like a bottle of water."
            };
            overlay.Show(lesson, null, false, new[] { "水" });
            Capture(camera, overlay, "unity-lesson-typography");
            RequireVisible(overlay, "Japanese", "Romaji", "Translation");
            float normalGlyphScale = overlay.Panel.localScale.x;

            overlay.SetKanaVisible(true);
            overlay.SetClerkCaption("はい、お水ですね。", "Yes, a bottle of water.");
            overlay.Show(new LearningOverlayData
            {
                jp = "すみません、このお弁当を温めていただけますか。お箸もお願いします。",
                romaji = "Sumimasen, kono obento o atatamete itadakemasu ka. Ohashi mo onegaishimasu.",
                kana = lesson.kana,
                en = "Excuse me, could you heat up this bento for me? Chopsticks too, please."
            }, null, false, new[] { "お弁当", "お箸" });
            Capture(camera, overlay, "unity-wrapped-typography");
            RequireVisible(overlay, "Japanese", "Romaji", "Translation");

            overlay.Show(new LearningOverlayData
            {
                jp = string.Concat(System.Linq.Enumerable.Repeat("水をお願いします。", 60)),
                romaji = lesson.romaji, en = lesson.en
            }, null, false, new[] { "水" });
            Capture(camera, overlay, "unity-long-typography");
            RequireVisible(overlay, "Japanese", "Romaji", "Translation");
            Require(overlay.Panel.Find("Japanese").GetComponent<Text>().text.EndsWith("…"),
                "Oversized Japanese needs a visible ellipsis.");

            overlay.SetConversationState(true, false, "Listening · 日本語 / English");
            overlay.SetRoute(true, "水を買う");
            overlay.SetClerkCaption("はい、お水ですね。", "Yes, a bottle of water.");
            overlay.SetLearnerCaption("ありがとうございます。", "Thank you very much.");
            Capture(camera, overlay, "unity-conversation-preview");
            RequireVisible(overlay, "Clerk", "Learner");
            Require(Mathf.Approximately(overlay.Panel.localScale.x, normalGlyphScale),
                "Conversation and history must not change glyph scale.");

            screen.transform.localScale = new Vector3(2.1f, 0.9f, 1);
            Capture(camera, overlay, "unity-wide-typography");
            Require(Mathf.Approximately(overlay.Panel.localScale.x, normalGlyphScale),
                "Wider video at the same height must not inflate glyphs.");
            screen.transform.localScale = new Vector3(0.9f, 0.9f, 1);
            Capture(camera, overlay, "unity-square-typography");
            Require(overlay.Panel.rect.width * overlay.Panel.localScale.x <= 0.9f * 0.88f + 0.001f,
                "Narrow video must constrain the caption width.");
            Debug.Log("[Learning] Typography checks passed; previews in Logs. Device readability remains unverified.");
        }

        static void Capture(Camera camera, LearningOverlay overlay, string name)
        {
            overlay.SendMessage("LateUpdate");
            Canvas.ForceUpdateCanvases();
            CheckLayout(overlay);
            var target = new RenderTexture(1440, 810, 24);
            camera.targetTexture = target; camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = target;
            var image = new Texture2D(1440, 810, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1440, 810), 0, 0); image.Apply();
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes("Logs/" + name + ".png", image.EncodeToPNG());
            RenderTexture.active = previous; camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(image); target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }

        static void CheckLayout(LearningOverlay overlay)
        {
            float previousBottom = 0;
            var names = new[] { "Clerk", "Learner", "Japanese", "Romaji", "Translation", "Kana", "Feedback" };
            var sizes = new[] { 40, 40, 60, 40, 36, 36, 25 };
            for (int i = 0; i < names.Length; i++)
            {
                var text = overlay.Panel.Find(names[i]).GetComponent<Text>();
                Require(text.fontSize == sizes[i] && !text.resizeTextForBestFit,
                    names[i] + " must keep its readable font size.");
                if (!text.gameObject.activeSelf) continue;
                var rect = (RectTransform)text.transform;
                float top = -rect.offsetMax.y;
                float bottom = -rect.offsetMin.y;
                Require(top >= previousBottom - 0.01f, names[i] + " overlaps another caption.");
                Require(bottom <= overlay.Panel.rect.height + 0.01f, names[i] + " exceeds backdrop.");
                Require(text.preferredHeight + 6 <= rect.rect.height + 0.1f,
                    names[i] + " clips vertically.");
                previousBottom = bottom;
            }
            var scale = overlay.Panel.localScale;
            Require(Mathf.Approximately(scale.x, scale.y) && Mathf.Approximately(scale.y, scale.z),
                "Glyphs must use uniform world scale.");
        }

        static void RequireVisible(LearningOverlay overlay, params string[] names)
        {
            foreach (string name in names)
                Require(overlay.Panel.Find(name).gameObject.activeSelf, name + " must remain visible.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
#endif
