using UnityEngine;

namespace ImmerseTheBay.Audio
{
    /// <summary>
    /// Draws a waveform line plus spectrum bars in 3D for either an AudioSource (music / video sound) or the microphone.
    /// </summary>
    public class AudioVisualizer : MonoBehaviour
    {
        public enum Input { AudioSource, Microphone }

        public Input input = Input.AudioSource;
        public AudioSource audioSource;
        public MicrophoneCapture microphone;

        [Header("Layout (local space, metres)")]
        public float width = 1.0f;
        public float waveHeight = 0.15f;
        public float barMaxHeight = 0.3f;
        [Range(8, 128)] public int barCount = 48;
        public Material lineMaterial;
        public Material barMaterial;
        public Color waveColor = new(0.3f, 1f, 0.9f);
        public Color barColor = new(1f, 0.45f, 0.8f);
        [Range(0f, 0.99f)] public float smoothing = 0.75f;

        const int N = 1024; // samples per frame (power of two for the FFT)
        readonly float[] m_Samples = new float[N];
        readonly float[] m_Spectrum = new float[N];
        readonly float[] m_Im = new float[N];
        float[] m_BarValues;
        Transform[] m_Bars;
        LineRenderer m_Line;

        void Start()
        {
                        m_Line = new GameObject("Waveform").AddComponent<LineRenderer>();
            m_Line.transform.SetParent(transform, false);
            m_Line.useWorldSpace = false;
            m_Line.positionCount = 256;
            m_Line.widthMultiplier = 0.004f;
            m_Line.material = lineMaterial ? lineMaterial : new Material(Shader.Find("Sprites/Default"));
            m_Line.startColor = m_Line.endColor = waveColor;

            var barMat = barMaterial ? new Material(barMaterial) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            barMat.color = barColor;
            if (barMat.HasProperty("_BaseColor")) barMat.SetColor("_BaseColor", barColor);
            m_Bars = new Transform[barCount];
            m_BarValues = new float[barCount];
            float step = width / barCount;
            for (int i = 0; i < barCount; i++)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(bar.GetComponent<Collider>());
                bar.GetComponent<MeshRenderer>().sharedMaterial = barMat;
                bar.transform.SetParent(transform, false);
                bar.transform.localPosition = new Vector3(-width / 2 + step * (i + 0.5f), -waveHeight - 0.02f, 0);
                bar.transform.localScale = new Vector3(step * 0.7f, 0.001f, 0.01f);
                m_Bars[i] = bar.transform;
            }
        }

        void Update()
        {
            bool ok = Fill();
            if (!ok) System.Array.Clear(m_Samples, 0, N);

            // Waveform
            int pts = m_Line.positionCount;
            for (int i = 0; i < pts; i++)
            {
                float s = m_Samples[i * N / pts];
                m_Line.SetPosition(i, new Vector3(-width / 2 + width * i / (pts - 1), s * waveHeight * 3f, 0));
            }

            // Spectrum bars (log-spaced bins up to Nyquist/2)
            int half = N / 2;
            for (int b = 0; b < barCount; b++)
            {
                int lo = Mathf.Max(1, Mathf.FloorToInt(Mathf.Pow(half, (float)b / barCount)));
                int hi = Mathf.Max(lo + 1, Mathf.FloorToInt(Mathf.Pow(half, (float)(b + 1) / barCount)));
                float v = 0;
                for (int k = lo; k < hi && k < half; k++) v = Mathf.Max(v, m_Spectrum[k]);
                v = Mathf.Clamp01(Mathf.Log10(1 + v * 400f) / 2f);
                m_BarValues[b] = Mathf.Max(v, m_BarValues[b] * smoothing);
                var t = m_Bars[b];
                float h = Mathf.Max(0.001f, m_BarValues[b] * barMaxHeight);
                t.localScale = new Vector3(t.localScale.x, h, t.localScale.z);
                t.localPosition = new Vector3(t.localPosition.x, -waveHeight - 0.02f - barMaxHeight + h / 2, 0);
            }
        }

        bool Fill()
        {
            if (input == Input.AudioSource)
            {
                if (!audioSource || !audioSource.isPlaying) return false;
                audioSource.GetOutputData(m_Samples, 0);
                audioSource.GetSpectrumData(m_Spectrum, 0, FFTWindow.BlackmanHarris);
                return true;
            }
            if (!microphone || !microphone.GetLatestSamples(m_Samples)) return false;
            // Spectrum via our own FFT (mic is not played through an AudioSource)
            for (int i = 0; i < N; i++)
            {
                float w = 0.5f - 0.5f * Mathf.Cos(2 * Mathf.PI * i / (N - 1)); // Hann
                m_Spectrum[i] = m_Samples[i] * w;
                m_Im[i] = 0;
            }
            Fft(m_Spectrum, m_Im);
            for (int i = 0; i < N / 2; i++)
                m_Spectrum[i] = Mathf.Sqrt(m_Spectrum[i] * m_Spectrum[i] + m_Im[i] * m_Im[i]) / (N / 2);
            return true;
        }

        /// <summary>In-place iterative radix-2 FFT.</summary>
        static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2 * Mathf.PI / len;
                float wr = Mathf.Cos(ang), wi = Mathf.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
        }
    }
}
