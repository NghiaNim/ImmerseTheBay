using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace ImmerseTheBay.Visko
{
    public partial class ViskoSession
    {
        const ulong MaxOpeningBytes = 8 * 1024 * 1024;
        static readonly Regex AuthoredScene = new(@"\A(?:cafe|dog|date|store)-[a-z]{2,16}\z");
        static readonly Regex LessonKey = new(@"\A[a-z0-9][a-z0-9-]{0,79}\z");
        static readonly int OpeningTexture = Shader.PropertyToID("_OpeningTex");
        static readonly int OpeningBlend = Shader.PropertyToID("_OpeningBlend");

        sealed class OpeningClip
        {
            readonly string m_Lesson;
            readonly string m_Scene;
            public readonly string FilePath = Path.Combine(Application.temporaryCachePath, $"tenin-opening-{Guid.NewGuid():N}.mp4");
            public string SceneId => m_Scene;
            public bool IsReady { get; private set; }
            public bool Finished { get; private set; }
            UnityWebRequest m_Request;

            public OpeningClip(string lesson, string scene)
            {
                m_Lesson = lesson;
                m_Scene = scene;
            }

            public bool Matches(string lesson, string scene) => m_Lesson == lesson && m_Scene == scene;

            public void UpdateDownload(ViskoConfig config)
            {
                if (Finished) return;
                if (m_Request == null)
                {
                    var endpoint = $"{config.portalUrl.TrimEnd('/')}/api/agent/lessons/{m_Lesson}/scenes/{m_Scene}/opening";
                    m_Request = UnityWebRequest.Get(endpoint);
                    m_Request.downloadHandler.Dispose();
                    m_Request.downloadHandler = new DownloadHandlerFile(FilePath) { removeFileOnAbort = true };
                    m_Request.timeout = 8;
                    m_Request.redirectLimit = 0;
                    if (!string.IsNullOrEmpty(config.portalToken))
                        m_Request.SetRequestHeader("portal_token", config.portalToken);
                    m_Request.SendWebRequest();
                }

                ulong size = m_Request.downloadedBytes;
                if (!m_Request.isDone && size <= MaxOpeningBytes) return;
                IsReady = m_Request.result == UnityWebRequest.Result.Success &&
                    m_Request.responseCode == 200 && size > 0 && size <= MaxOpeningBytes;
                Finished = true;
                CloseRequest();
                if (!IsReady) DeleteFile();
            }

            public void Release()
            {
                CloseRequest();
                DeleteFile();
            }

            void CloseRequest()
            {
                if (m_Request == null) return;
                if (!m_Request.isDone) m_Request.Abort();
                m_Request.Dispose();
                m_Request = null;
            }

            void DeleteFile()
            {
                try { File.Delete(FilePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        OpeningClip m_SceneOpening, m_NextOpening, m_AlternateOpening;
        VideoPlayer m_OpeningPlayer;
        float m_OpeningFade, m_OpeningDecodeDeadline, m_OpeningSelectedAt;
        bool m_OpenFrameShown, m_OpeningPlayRequested;

        public static string CourseOpeningSceneId(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return null;
            string id = Path.GetFileNameWithoutExtension(imagePath);
            return AuthoredScene.IsMatch(id) && imagePath == $"lessons/images/{id}.jpg" ? id : null;
        }

        bool CanPlayOpening(string lesson, string scene) =>
            screen && screen.sharedMaterial && screen.sharedMaterial.HasProperty(OpeningTexture) &&
            !string.IsNullOrEmpty(lesson) && LessonKey.IsMatch(lesson) &&
            !string.IsNullOrEmpty(scene) && AuthoredScene.IsMatch(scene);

        /// <summary>Show the selected authored opening while the same live session prepares its first frame.</summary>
        public void UseSceneOpening(string lesson, string scene)
        {
            if (!CanPlayOpening(lesson, scene))
            {
                ClearSceneOpening();
                PrefetchSceneOpening(null, null);
                return;
            }
            if (m_SceneOpening?.Matches(lesson, scene) == true) return;
            ClearSceneOpening();
            if (m_NextOpening?.Matches(lesson, scene) == true)
            {
                m_SceneOpening = m_NextOpening;
                m_NextOpening = null;
            }
            else if (m_AlternateOpening?.Matches(lesson, scene) == true)
            {
                m_SceneOpening = m_AlternateOpening;
                m_AlternateOpening = null;
            }
            else
            {
                PrefetchSceneOpening(null, null);
                m_SceneOpening = new OpeningClip(lesson, scene);
            }
            m_OpeningSelectedAt = Time.realtimeSinceStartup;
            Debug.Log($"[Visko] Scene opening selected: {scene} (prefetched={m_SceneOpening.IsReady})");
            m_PreparingScene = true;
            m_AcceptSceneFrames = false;
            SetHasVideo(false);
            ApplySurfaceAspect();
        }

        public void ConfirmSceneOpening(string lesson, string scene)
        {
            if (m_SceneOpening != null && !m_SceneOpening.Matches(lesson, scene))
                ClearSceneOpening();
        }

        /// <summary>Download the next authored opening and, at a menu, its one alternate.</summary>
        public void PrefetchSceneOpening(string lesson, string scene, string alternate = null)
        {
            var oldNext = m_NextOpening;
            var oldAlternate = m_AlternateOpening;
            OpeningClip Find(string id) =>
                !CanPlayOpening(lesson, id) || m_SceneOpening?.Matches(lesson, id) == true ? null
                : oldNext?.Matches(lesson, id) == true ? oldNext
                : oldAlternate?.Matches(lesson, id) == true ? oldAlternate
                : new OpeningClip(lesson, id);
            m_NextOpening = Find(scene);
            m_AlternateOpening = alternate == scene ? null : Find(alternate);
            if (oldNext != m_NextOpening && oldNext != m_AlternateOpening) oldNext?.Release();
            if (oldAlternate != m_NextOpening && oldAlternate != m_AlternateOpening) oldAlternate?.Release();
        }

        public void ClearSceneOpening()
        {
            if (m_OpeningPlayer)
            {
                m_OpeningPlayer.errorReceived -= OnOpeningError;
                m_OpeningPlayer.Stop();
                Destroy(m_OpeningPlayer);
                m_OpeningPlayer = null;
            }
            if (screen && screen.sharedMaterial && screen.sharedMaterial.HasProperty(OpeningTexture))
            {
                var material = screen.material;
                material.SetFloat(OpeningBlend, 0f);
                material.SetTexture(OpeningTexture, Texture2D.blackTexture);
            }
            m_SceneOpening?.Release();
            m_SceneOpening = null;
            m_OpeningFade = 0f;
            m_OpenFrameShown = m_OpeningPlayRequested = false;
        }

        void PollOpeningDownload(OpeningClip clip)
        {
            if (clip == null) return;
            try { clip.UpdateDownload(config); }
            catch (Exception error)
            {
                Debug.LogWarning($"[Visko] Opening clip unavailable ({error.GetType().Name})");
                if (ReferenceEquals(clip, m_SceneOpening)) ClearSceneOpening();
                else
                {
                    clip.Release();
                    if (ReferenceEquals(clip, m_NextOpening)) m_NextOpening = null;
                    if (ReferenceEquals(clip, m_AlternateOpening)) m_AlternateOpening = null;
                }
            }
        }

        void UpdateSceneOpenings()
        {
            if (!IsConfigLoaded) return;
            PollOpeningDownload(m_SceneOpening);
            PollOpeningDownload(m_NextOpening);
            PollOpeningDownload(m_AlternateOpening);
            if (m_SceneOpening == null) return;

            if (HasLiveFrame && (!m_OpenFrameShown || m_LiveBlend >= 1f))
            {
                Debug.Log($"[Visko] {m_SceneOpening.SceneId} handoff to live after {(Time.realtimeSinceStartup - m_OpeningSelectedAt):F2}s ({(m_OpenFrameShown ? "clip" : "still")})");
                ClearSceneOpening();
                return;
            }
            if (!m_SceneOpening.IsReady) return;

            if (!m_OpeningPlayer)
            {
                m_OpeningPlayer = gameObject.AddComponent<VideoPlayer>();
                m_OpeningPlayer.playOnAwake = false;
                m_OpeningPlayer.source = VideoSource.Url;
                m_OpeningPlayer.renderMode = VideoRenderMode.APIOnly;
                m_OpeningPlayer.audioOutputMode = VideoAudioOutputMode.None;
                m_OpeningPlayer.waitForFirstFrame = true;
                m_OpeningPlayer.isLooping = false;
                m_OpeningPlayer.errorReceived += OnOpeningError;
                m_OpeningPlayer.url = new Uri(m_SceneOpening.FilePath).AbsoluteUri;
                m_OpeningDecodeDeadline = Time.realtimeSinceStartup + 5f;
                m_OpeningPlayer.Prepare();
                return;
            }
            if (!m_OpenFrameShown && Time.realtimeSinceStartup >= m_OpeningDecodeDeadline)
            {
                ClearSceneOpening();
                return;
            }
            if (!m_OpeningPlayRequested && m_OpeningPlayer.isPrepared)
            {
                m_OpeningPlayRequested = true;
                m_OpeningPlayer.Play();
            }
            if (!m_OpenFrameShown && m_OpeningPlayRequested && m_OpeningPlayer.frame >= 0 && m_OpeningPlayer.texture)
            {
                screen.material.SetTexture(OpeningTexture, m_OpeningPlayer.texture);
                m_OpenFrameShown = true;
                Debug.Log($"[Visko] Cached {m_SceneOpening.SceneId} first frame after {(Time.realtimeSinceStartup - m_OpeningSelectedAt):F2}s");
            }
            if (m_OpenFrameShown && m_OpeningFade < 1f)
            {
                m_OpeningFade = Mathf.MoveTowards(m_OpeningFade, 1f, Time.unscaledDeltaTime / 0.8f);
                screen.material.SetFloat(OpeningBlend, m_OpeningFade);
            }
        }

        void OnOpeningError(VideoPlayer player, string message) => m_Main.Enqueue(() =>
        {
            if (ReferenceEquals(player, m_OpeningPlayer)) ClearSceneOpening();
        });
    }
}
