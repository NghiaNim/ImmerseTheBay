using System;
using UnityEngine;

namespace ImmerseTheBay.Learning
{
    /// <summary>Explicit transcript injection for editor rehearsal; does not pretend to recognize audio.</summary>
    public sealed class EditorSpeechToText : MonoBehaviour, ISpeechToText
    {
        [TextArea] public string testTranscript = "";
        public event Action<string> PartialResult;
        public event Action<string> FinalResult;
        public event Action<string> Error;
        public bool IsListening { get; private set; }

        public void StartListening()
        {
#if UNITY_EDITOR
            IsListening = true;
#else
            Error?.Invoke("Assign the Android speech provider on this device.");
#endif
        }

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            Error?.Invoke("Editor recognition cancelled.");
        }

        [ContextMenu("Preview test transcript")]
        public void PreviewTestTranscript()
        {
            if (IsListening) PartialResult?.Invoke(testTranscript ?? "");
        }

        [ContextMenu("Submit test transcript")]
        public void SubmitTestTranscript() => Submit(testTranscript);

        public void Submit(string transcript)
        {
            if (!IsListening) return;
            IsListening = false;
            FinalResult?.Invoke(transcript ?? "");
        }

        public void Dispose() => IsListening = false;
        void OnDisable() => Dispose();
    }
}
