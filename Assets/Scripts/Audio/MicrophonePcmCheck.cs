#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ImmerseTheBay.Audio
{
    /// <summary>Runs the production event path against a synthetic looping clip, without opening a mic.</summary>
    internal static class MicrophonePcmCheck
    {
        [MenuItem("Tools/XREAL/Check Microphone PCM Continuity")]
        public static void Run()
        {
            PcmChunkReaderChecks.Run();
            const int capacity = 257, rate = 8000, total = 4000, channels = 2;
            var go = new GameObject("PCM continuity check") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false); // Never invoke microphone startup or request permission.
            var capture = go.AddComponent<MicrophoneCapture>();
            capture.startOnEnable = false;
            capture.chunkMs = 5; // 40 frames per chunk, deliberately not a divisor of the ring size.
            var clip = AudioClip.Create("Synthetic PCM ring", capacity, channels, rate, false);
            try
            {
                var ring = new float[capacity * channels];
                var chunks = new List<short[]>();
                int mainThread = Thread.CurrentThread.ManagedThreadId;
                string eventError = null;
                capture.OnPcmChunk += (pcm, sampleRate) =>
                {
                    // Do not throw in a subscriber: capture deliberately isolates subscriber errors.
                    if (sampleRate != rate || pcm.Length != 40 || Thread.CurrentThread.ManagedThreadId != mainThread)
                        eventError = "Wrong chunk length, sample rate or callback thread";
                    chunks.Add(pcm);
                };
                capture.ConfigurePcm(clip);
                int[] steps = { 17, 0, 93, 39, 7, 101, 2 };
                int written = 0, poll = 0;
                while (written < total)
                {
                    int step = Math.Min(steps[poll++ % steps.Length], total - written);
                    for (int frame = written; frame < written + step; frame++)
                    {
                        ring[(frame % capacity) * channels] = PcmChunkReaderChecks.Sample(frame) + 0.125f;
                        ring[(frame % capacity) * channels + 1] = PcmChunkReaderChecks.Sample(frame) - 0.125f;
                    }
                    written += step;
                    PcmChunkReaderChecks.Require(clip.SetData(ring, 0), "Synthetic clip write failed");
                    capture.DrainPcm(written % capacity);
                    capture.DrainPcm(written % capacity); // A second poll must emit nothing.
                }
                int received = 0;
                foreach (var chunk in chunks)
                    foreach (short sample in chunk)
                        PcmChunkReaderChecks.Require(sample == PcmChunkReaderChecks.Expected(received++), "Event lost or duplicated samples");
                PcmChunkReaderChecks.Require(eventError == null, eventError);
                PcmChunkReaderChecks.Require(received == total && chunks.Count == total / 40, "Event count mismatch");
                Debug.Log("PCM checks passed: 4000 contiguous stereo-to-mono frames through OnPcmChunk across 15 wraps; " +
                    "reader checks cover mono/stereo, partial tails, failed reads, overwrites and clipping.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }
    }
}
#endif
