using System;

namespace ImmerseTheBay.Audio
{
    /// <summary>
    /// Reads complete PCM chunks from a looping, interleaved float buffer. Positions/counts are sample
    /// frames (one sample per channel). Poll at least once per ring duration; an overwritten ring
    /// cannot be recovered from a modulo write cursor. Partial chunks remain pending between polls.
    /// </summary>
    internal sealed class PcmChunkReader
    {
        readonly int m_Capacity, m_Channels, m_ChunkFrames;
        float[] m_Scratch;
        int m_LastWrite, m_Read, m_Pending;

        public int SampleRate { get; }
        public int PendingFrames => m_Pending;

        public PcmChunkReader(int capacity, int channels, int sampleRate, int chunkMs)
        {
            if (capacity <= 0 || channels <= 0 || sampleRate <= 0) throw new ArgumentOutOfRangeException();
            m_Capacity = capacity;
            m_Channels = channels;
            SampleRate = sampleRate;
            m_ChunkFrames = (int)Math.Max(1, Math.Min(capacity, (long)sampleRate * Math.Max(1, chunkMs) / 1000));
        }

        /// <returns>False when unread samples have been overwritten; resynchronises at the writer.</returns>
        public bool AdvanceTo(int writePosition)
        {
            if (writePosition < 0 || writePosition >= m_Capacity) throw new ArgumentOutOfRangeException(nameof(writePosition));
            int delta = (writePosition - m_LastWrite + m_Capacity) % m_Capacity;
            m_LastWrite = writePosition;
            if (m_Pending + delta > m_Capacity) { Resync(writePosition); return false; }
            m_Pending += delta;
            return true;
        }

        public void Resync(int writePosition)
        {
            m_LastWrite = m_Read = writePosition;
            m_Pending = 0;
        }

        /// <summary>
        /// readData must copy interleaved samples at the frame offset and wrap at the ring end,
        /// as AudioClip.GetData does. Returns a new owned array per chunk, safe for async consumers.
        /// A final short chunk can be flushed with allowPartial when stopping capture.
        /// </summary>
        public bool TryReadChunk(Func<float[], int, bool> readData, out short[] pcm, bool allowPartial = false)
        {
            pcm = null;
            if (m_Pending == 0 || (!allowPartial && m_Pending < m_ChunkFrames)) return false;
            int frames = Math.Min(m_Pending, m_ChunkFrames);
            int count = frames * m_Channels;
            if (m_Scratch == null || m_Scratch.Length != count) m_Scratch = new float[count];
            if (!readData(m_Scratch, m_Read)) return false;
            pcm = new short[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                double mono = 0;
                for (int channel = 0; channel < m_Channels; channel++) mono += m_Scratch[frame * m_Channels + channel];
                mono /= m_Channels;
                if (double.IsNaN(mono)) mono = 0;
                mono = Math.Max(-1, Math.Min(1, mono));
                pcm[frame] = (short)Math.Round(mono * (mono < 0 ? 32768 : 32767), MidpointRounding.AwayFromZero);
            }
            // Commit before invoking consumers so a failed subscriber cannot cause duplicate chunks.
            m_Read = (m_Read + frames) % m_Capacity;
            m_Pending -= frames;
            return true;
        }
    }
}
