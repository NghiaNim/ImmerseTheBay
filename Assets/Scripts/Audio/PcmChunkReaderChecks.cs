#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace ImmerseTheBay.Audio
{
    /// <summary>Deterministic checks without microphone hardware, Unity native calls or a test package.</summary>
    internal static class PcmChunkReaderChecks
    {
        public static void Run()
        {
            CheckContinuity(1);
            CheckContinuity(2);
            CheckReadFailure();
            CheckOverrun();
            CheckClipping();
        }

        internal static short Expected(int frame) => (short)(frame % 20001 - 10000);
        internal static float Sample(int frame)
        {
            short value = Expected(frame);
            return (float)value / (value < 0 ? 32768 : 32767);
        }

        static void CheckContinuity(int channels)
        {
            const int capacity = 257, total = 6037;
            var reader = new PcmChunkReader(capacity, channels, 1000, 40);
            var ring = new float[capacity * channels];
            var chunks = new List<short[]>();
            int[] steps = { 13, 1, 0, 101, 39, 98, 2, 67 };
            int written = 0, poll = 0;
            bool Read(float[] target, int offset)
            {
                for (int i = 0; i < target.Length; i++) target[i] = ring[(offset * channels + i) % ring.Length];
                return true;
            }
            while (written < total)
            {
                int step = Math.Min(steps[poll++ % steps.Length], total - written);
                for (int frame = written; frame < written + step; frame++)
                    for (int channel = 0; channel < channels; channel++)
                        ring[(frame % capacity) * channels + channel] = Sample(frame) +
                            (channels == 1 ? 0 : channel == 0 ? 0.125f : -0.125f);
                written += step;
                Require(reader.AdvanceTo(written % capacity), "Unexpected ring overrun");
                while (reader.TryReadChunk(Read, out var pcm))
                {
                    Require(pcm.Length == 40, "Wrong chunk length");
                    chunks.Add(pcm);
                }
            }
            while (reader.TryReadChunk(Read, out var tail, true)) chunks.Add(tail);
            int received = 0;
            // Check retained arrays after every read, detecting reuse as well as gaps/duplicates.
            foreach (var chunk in chunks)
                foreach (short sample in chunk) Require(sample == Expected(received++), "Non-contiguous PCM across wrap");
            Require(received == total && reader.PendingFrames == 0, "Missing samples or partial tail");
            Require(reader.AdvanceTo(written % capacity) && !reader.TryReadChunk(Read, out _), "Repeated cursor duplicated PCM");
            Require(reader.SampleRate == 1000, "Wrong sample rate");
        }

        static void CheckReadFailure()
        {
            var reader = new PcmChunkReader(100, 1, 1000, 40);
            reader.AdvanceTo(40);
            Require(!reader.TryReadChunk((_, __) => false, out _) && reader.PendingFrames == 40,
                "Failed native read consumed samples");
            Require(reader.TryReadChunk((buffer, offset) => { Array.Clear(buffer, 0, buffer.Length); return offset == 0; }, out _),
                "Failed read could not be retried");
        }

        static void CheckOverrun()
        {
            var reader = new PcmChunkReader(100, 1, 1000, 40);
            Require(reader.AdvanceTo(30), "Initial cursor failed");
            Require(!reader.AdvanceTo(10) && reader.PendingFrames == 0, "Overwrite did not resynchronise");
        }

        static void CheckClipping()
        {
            var reader = new PcmChunkReader(16, 1, 1000, 8);
            float[] values = { -2, -1, 0, 1, 2, float.NaN, float.NegativeInfinity, float.PositiveInfinity };
            short[] expected = { short.MinValue, short.MinValue, 0, short.MaxValue, short.MaxValue, 0, short.MinValue, short.MaxValue };
            reader.AdvanceTo(8);
            Require(reader.TryReadChunk((target, _) => { Array.Copy(values, target, values.Length); return true; }, out var pcm),
                "Missing clipping chunk");
            for (int i = 0; i < expected.Length; i++) Require(pcm[i] == expected[i], "PCM clipping/wrapping error");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
