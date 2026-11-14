using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace ImmerseTheBay.Tracking
{
    /// <summary>
    /// Recenters head tracking. Equivalent to XREAL's <c>XREALUtility.GetInputSubsystem()?.TryRecenter()</c>:
    /// the XREAL loader registers the running XRInputSubsystem, so the standard call reaches it.
    /// </summary>
    public class RecenterController : MonoBehaviour
    {
        static readonly List<XRInputSubsystem> s_List = new();
        string m_Last = "never";
        System.Func<string> m_Debug;

        void Awake() => m_Debug = () => $"<b>== RECENTER ==</b> last: {m_Last}";
        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        public bool Recenter()
        {
            SubsystemManager.GetSubsystems(s_List);
            foreach (var s in s_List)
            {
                if (!s.running) continue;
                bool ok = s.TryRecenter();
                m_Last = $"{(ok ? "OK" : "failed")} at {Time.realtimeSinceStartup:F0}s ({s.subsystemDescriptor.id})";
                return ok;
            }
            m_Last = "no running XRInputSubsystem";
            return false;
        }
    }
}
