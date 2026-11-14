using System;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace ImmerseTheBay.Anchors
{
    /// <summary>Converts AR Foundation <see cref="SerializableGuid"/> to / from a 32-char hex string for PlayerPrefs.</summary>
    public static class GuidCodec
    {
        public static string Encode(SerializableGuid g)
        {
            var bytes = g.guid.ToByteArray();
            ulong low = BitConverter.ToUInt64(bytes, 0), high = BitConverter.ToUInt64(bytes, 8);
            if (new SerializableGuid(low, high).guid != g.guid)
                Debug.LogWarning("[GuidCodec] SerializableGuid round-trip mismatch; saved anchors may not reload");
            return $"{low:X16}{high:X16}";
        }

        public static bool TryDecode(string s, out SerializableGuid g)
        {
            g = default;
            if (string.IsNullOrEmpty(s) || s.Length != 32) return false;
            try
            {
                g = new SerializableGuid(Convert.ToUInt64(s.Substring(0, 16), 16), Convert.ToUInt64(s.Substring(16), 16));
                return true;
            }
            catch (FormatException) { return false; }
        }
    }
}
