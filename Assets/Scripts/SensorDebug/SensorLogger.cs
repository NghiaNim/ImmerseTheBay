using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace ImmerseTheBay.SensorDebug
{
    /// <summary>
    /// Captures on the main thread and writes CSV on a bounded background worker, flushing every second.
    /// Auto-attached to the collector in existing scenes. L or SetLogging toggles recording.
    /// </summary>
    [DisallowMultipleComponent]
    public class SensorLogger : MonoBehaviour
    {
        public SensorDataCollector collector;
        [Min(1)] public int sampleIntervalMs = 100;
        [Tooltip("Start recording when this component is enabled. Otherwise use L or SetLogging(true).")]
        public bool logOnEnable;

        public bool IsLogging { get; private set; }
        public string FilePath { get; private set; } = "";
        public long RowCount { get; private set; }
        public string LastError { get; private set; } = "";

        const int QueueCapacity = 1024;
        static readonly char[] s_CsvSpecialChars = { ',', '"', '\r', '\n' };
        static readonly CultureInfo s_Invariant = CultureInfo.InvariantCulture;
        const string FixedHeader = "timestamp_utc,elapsed_seconds," +
            "head_position_x_m,head_position_y_m,head_position_z_m," +
            "head_rotation_x,head_rotation_y,head_rotation_z,head_rotation_w," +
            "head_velocity_x_m_s,head_velocity_y_m_s,head_velocity_z_m_s," +
            "left_tracked,left_wrist_x_m,left_wrist_y_m,left_wrist_z_m," +
            "left_index_tip_x_m,left_index_tip_y_m,left_index_tip_z_m," +
            "left_thumb_tip_x_m,left_thumb_tip_y_m,left_thumb_tip_z_m,left_pinch_cm," +
            "right_tracked,right_wrist_x_m,right_wrist_y_m,right_wrist_z_m," +
            "right_index_tip_x_m,right_index_tip_y_m,right_index_tip_z_m," +
            "right_thumb_tip_x_m,right_thumb_tip_y_m,right_thumb_tip_z_m,right_pinch_cm," +
            "ar_session_state,plane_count,mesh_chunk_count,mesh_vertex_count,anchor_count";

        sealed class Session
        {
            public readonly BlockingCollection<string> rows = new(QueueCapacity);
            public readonly List<InputControl> controls = new();
            public string directory, stem, header;
            public Thread worker;
            public bool stopping;
            public long written;
        }

        readonly ConcurrentQueue<Action> m_MainThread = new();
        readonly StringBuilder m_Row = new(4096);
        Session m_Session;
        Func<string> m_Debug;
        bool m_WantsLogging, m_ResumeAfterPause;
        float m_NextSample, m_Started;

        void Awake()
        {
            if (!collector) collector = GetComponent<SensorDataCollector>();
            if (!collector) collector = FindAnyObjectByType<SensorDataCollector>();
            m_Debug = BuildDebug;
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (logOnEnable) SetLogging(true);
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            StopAndWait();
        }

        void OnApplicationQuit() => StopAndWait();

        void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                m_ResumeAfterPause = m_WantsLogging;
                StopAndWait(); // Android may suspend the process immediately after this callback.
            }
            else if (m_ResumeAfterPause)
            {
                m_ResumeAfterPause = false;
                SetLogging(true);
            }
        }

        /// <summary>Starts a new CSV, or asynchronously drains and closes the current recording.</summary>
        public void SetLogging(bool value)
        {
            m_WantsLogging = value && isActiveAndEnabled;
            if (!m_WantsLogging)
            {
                IsLogging = false;
                RequestStop();
            }
            else if (m_Session == null) StartSession();
        }

        void StartSession()
        {
            if (!collector)
            {
                LastError = "No SensorDataCollector found";
                m_WantsLogging = false;
                return;
            }
            var session = new Session
            {
                directory = Path.Combine(Application.persistentDataPath, "sensorlogs"),
                stem = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", s_Invariant),
            };
            foreach (var device in InputSystem.devices)
            {
                if (!(device is Sensor)) continue;
                if (!device.enabled) InputSystem.EnableDevice(device);
                // Include parents and leaf controls; the complete schema is frozen for this recording.
                foreach (var control in device.allControls) session.controls.Add(control);
            }
            session.controls.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
            m_Row.Clear().Append(FixedHeader);
            foreach (var control in session.controls) AppendCell(m_Row, "sensor:" + control.path);
            session.header = m_Row.ToString();
            FilePath = Path.Combine(session.directory, session.stem + ".csv");
            RowCount = 0;
            LastError = "";
            m_Started = m_NextSample = Time.unscaledTime;
            m_Session = session;
            IsLogging = true;
            session.worker = new Thread(() => WriteSession(session)) { IsBackground = true, Name = "Sensor CSV writer" };
            session.worker.Start();
        }

        void Update()
        {
            DrainCallbacks();
            if (m_Session != null) RowCount = Interlocked.Read(ref m_Session.written);
            var kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame) SetLogging(!m_WantsLogging);
            if (m_WantsLogging && m_Session == null) StartSession();
            if (!IsLogging || Time.unscaledTime < m_NextSample) return;
            // Never burst/catch up after a slow frame or pause.
            m_NextSample = Time.unscaledTime + Mathf.Max(1, sampleIntervalMs) / 1000f;
            if (!collector)
            {
                LastError = "SensorDataCollector was destroyed";
                SetLogging(false);
                return;
            }
            var row = BuildRow(collector.CaptureSample(), m_Session);
            if (!m_Session.rows.TryAdd(row))
            {
                LastError = "Writer queue full; sample skipped and recording stopped";
                SetLogging(false);
            }
        }

        string BuildRow(SensorDataCollector.Sample sample, Session session)
        {
            var sb = m_Row.Clear();
            sb.Append(DateTime.UtcNow.ToString("O", s_Invariant));
            AppendCell(sb, Number(Time.unscaledTime - m_Started));
            AppendVector(sb, sample.headPosition);
            AppendQuaternion(sb, sample.headRotation);
            AppendVector(sb, sample.headVelocity);
            AppendHand(sb, sample.left);
            AppendHand(sb, sample.right);
            AppendCell(sb, sample.sessionState);
            AppendCell(sb, sample.planeCount.ToString(s_Invariant));
            AppendCell(sb, sample.meshChunks.ToString(s_Invariant));
            AppendCell(sb, sample.meshVertices.ToString(s_Invariant));
            AppendCell(sb, sample.anchorCount.ToString(s_Invariant));
            foreach (var control in session.controls)
            {
                string value = "";
                if (control.device.added && control.device.enabled)
                {
                    try { value = FormatValue(control.ReadValueAsObject()); }
                    catch (Exception) { /* Unreadable controls keep an empty, schema-stable cell. */ }
                }
                AppendCell(sb, value);
            }
            return sb.ToString();
        }

        static void AppendHand(StringBuilder sb, SensorDataCollector.HandSample hand)
        {
            AppendCell(sb, hand.tracked ? "1" : "0");
            AppendVector(sb, hand.wrist);
            AppendVector(sb, hand.indexTip);
            AppendVector(sb, hand.thumbTip);
            AppendCell(sb, hand.pinchCm.HasValue ? Number(hand.pinchCm.Value) : "");
        }

        static void AppendVector(StringBuilder sb, Vector3? value)
        {
            AppendCell(sb, value.HasValue ? Number(value.Value.x) : "");
            AppendCell(sb, value.HasValue ? Number(value.Value.y) : "");
            AppendCell(sb, value.HasValue ? Number(value.Value.z) : "");
        }

        static void AppendQuaternion(StringBuilder sb, Quaternion? value)
        {
            AppendCell(sb, value.HasValue ? Number(value.Value.x) : "");
            AppendCell(sb, value.HasValue ? Number(value.Value.y) : "");
            AppendCell(sb, value.HasValue ? Number(value.Value.z) : "");
            AppendCell(sb, value.HasValue ? Number(value.Value.w) : "");
        }

        static string Number(float value) => value.ToString("R", s_Invariant);

        static string FormatValue(object value) => value switch
        {
            Vector2 v => Number(v.x) + " " + Number(v.y),
            Vector3 v => Number(v.x) + " " + Number(v.y) + " " + Number(v.z),
            Quaternion q => Number(q.x) + " " + Number(q.y) + " " + Number(q.z) + " " + Number(q.w),
            IFormattable number => number.ToString(null, s_Invariant),
            _ => value?.ToString() ?? "",
        };

        static void AppendCell(StringBuilder sb, string value)
        {
            sb.Append(',');
            if (string.IsNullOrEmpty(value)) return;
            if (value.IndexOfAny(s_CsvSpecialChars) < 0) sb.Append(value);
            else sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }

        // No Unity or Input System APIs may be called below on the worker thread.
        void WriteSession(Session session)
        {
            string error = null;
            try
            {
                Directory.CreateDirectory(session.directory);
                using var stream = OpenUniqueFile(session, out string path);
                m_MainThread.Enqueue(() => { if (m_Session == session) FilePath = path; });
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536);
                writer.WriteLine(session.header);
                writer.Flush(); // A newly started recording always has a visible header.
                var clock = Stopwatch.StartNew();
                while (!session.rows.IsCompleted)
                {
                    if (session.rows.TryTake(out var row, 100))
                    {
                        writer.WriteLine(row);
                        Interlocked.Increment(ref session.written);
                    }
                    if (clock.ElapsedMilliseconds >= 1000) { writer.Flush(); clock.Restart(); }
                }
                writer.Flush();
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            finally
            {
                string finalError = error;
                m_MainThread.Enqueue(() => FinishSession(session, finalError));
            }
        }

        static FileStream OpenUniqueFile(Session session, out string path)
        {
            // CreateNew also protects rapid stop/start in one second and recordings left by earlier runs.
            for (int suffix = 0; ; suffix++)
            {
                path = Path.Combine(session.directory, session.stem + (suffix == 0 ? "" : "_" + suffix.ToString(s_Invariant)) + ".csv");
                try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
                catch (IOException) when (File.Exists(path)) { }
            }
        }

        void RequestStop()
        {
            if (m_Session == null || m_Session.stopping) return;
            m_Session.stopping = true;
            m_Session.rows.CompleteAdding();
        }

        void FinishSession(Session session, string error)
        {
            RowCount = Interlocked.Read(ref session.written);
            session.rows.Dispose();
            if (m_Session != session) return;
            m_Session = null;
            IsLogging = false;
            if (error != null)
            {
                m_WantsLogging = false;
                LastError = error;
                Debug.LogWarning("[SensorLogger] " + error);
            }
        }

        void StopAndWait()
        {
            m_WantsLogging = false;
            IsLogging = false;
            RequestStop();
            m_Session?.worker.Join();
            DrainCallbacks();
        }

        void DrainCallbacks()
        {
            while (m_MainThread.TryDequeue(out var callback)) callback();
        }

        string BuildDebug() => $"<b>== SENSOR LOGGING ==</b>\n" +
            $"{(IsLogging ? "RECORDING" : m_Session != null ? "STOPPING" : "OFF")} | rows={RowCount} | interval={sampleIntervalMs}ms (L toggles)\n" +
            $"File: {(string.IsNullOrEmpty(FilePath) ? "-" : FilePath)}" +
            (string.IsNullOrEmpty(LastError) ? "" : "\nError: " + LastError);
    }
}
