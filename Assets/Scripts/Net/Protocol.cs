using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ImmerseTheBay.Net
{
    [Serializable]
    public class RealtimeEnvelope
    {
        public string type;
        public string id;
        public long ts;
        public string room;
        public string from;
        // Raw object JSON preserves arbitrary application fields, arrays and nested values.
        [NonSerialized] public string DataJson;
        public T ReadData<T>() => JsonUtility.FromJson<T>(DataJson);
    }

    [Serializable] public class HelloData { public string clientId; public string deviceModel; public string appVersion; }
    [Serializable] public class WelcomeData { public long serverTime; public string clientId; public string[] members; }
    [Serializable] public class MembershipData { public string clientId; public string[] members; }
    [Serializable] public class PongData { public long clientTs; public string replyTo; }
    [Serializable] public class RealtimeError { public string code; public string message; public string replyTo; }

    public sealed class StateMessage
    {
        public RealtimeEnvelope Envelope { get; internal set; }
        public string Key { get; internal set; }
        public string Json { get; internal set; }
        public T Read<T>() => JsonUtility.FromJson<T>(Json);
    }

    public sealed class EventMessage
    {
        public RealtimeEnvelope Envelope { get; internal set; }
        public string Name { get; internal set; }
        public string Json { get; internal set; }
        public T Read<T>() => JsonUtility.FromJson<T>(Json);
    }

    public enum BinaryKind : byte { Pcm16Mono = 1, Jpeg = 2, Reserved = 3 }

    public sealed class BinaryMessage
    {
        public BinaryKind Kind { get; internal set; }
        public uint SampleRateOrWidth { get; internal set; }
        public byte[] Payload { get; internal set; }
    }

    /// <summary>No reflection-based generics, dictionaries in JsonUtility, or extra Unity packages.</summary>
    public static class Protocol
    {
        public const int MaxMessageBytes = 1024 * 1024;
        public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static string Write(string type, string dataJson, string room = null, string from = null,
            string id = null, long? ts = null)
        {
            ObjectFields(dataJson); // Validate raw JSON before embedding it.
            var json = "{\"type\":" + Quote(type) + ",\"id\":" + Quote(id ?? Guid.NewGuid().ToString()) +
                ",\"ts\":" + (ts ?? NowMs).ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(room) ? "" : ",\"room\":" + Quote(room)) +
                (string.IsNullOrEmpty(from) ? "" : ",\"from\":" + Quote(from)) + ",\"data\":" + dataJson + "}";
            if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) throw new ArgumentException("Message exceeds 1 MiB");
            return json;
        }

        public static RealtimeEnvelope Read(string json)
        {
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) throw new FormatException("Invalid message size");
            var fields = ObjectFields(json);
            var envelope = JsonUtility.FromJson<RealtimeEnvelope>(json);
            if (envelope == null || string.IsNullOrEmpty(envelope.type) || !Guid.TryParse(envelope.id, out _) ||
                !fields.TryGetValue("ts", out var rawTs) ||
                !long.TryParse(rawTs, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
                timestamp < 0 || timestamp > 9007199254740991L || !fields.TryGetValue("data", out var data))
                throw new FormatException("Invalid realtime envelope");
            ObjectFields(data);
            envelope.ts = timestamp;
            envelope.DataJson = data;
            return envelope;
        }

        public static string SerializeObject(object data)
        {
            if (data == null) return "{}";
            var json = JsonUtility.ToJson(data);
            ObjectFields(json);
            return json;
        }

        public static string NamedValue(string field, string name, string valueJson)
        {
            ValidateName(name);
            ValidateJson(valueJson);
            return "{" + Quote(field) + ":" + Quote(name) + ",\"value\":" + valueJson + "}";
        }

        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
                throw new ArgumentException("Names must contain 1-256 characters", nameof(name));
        }

        public static byte[] EncodeBinary(BinaryKind kind, uint sampleRateOrWidth, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var bytes = new byte[5 + payload.Length];
            bytes[0] = (byte)kind;
            for (int i = 0; i < 4; i++) bytes[i + 1] = (byte)(sampleRateOrWidth >> (8 * i));
            Buffer.BlockCopy(payload, 0, bytes, 5, payload.Length);
            DecodeBinary(bytes); // Share validation with the receive path.
            return bytes;
        }

        public static BinaryMessage DecodeBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 6 || bytes.Length > MaxMessageBytes || bytes[0] < 1 || bytes[0] > 3)
                throw new FormatException("Invalid binary frame");
            uint parameter = (uint)bytes[1] | ((uint)bytes[2] << 8) | ((uint)bytes[3] << 16) | ((uint)bytes[4] << 24);
            if ((bytes[0] != 3 && parameter == 0) || (bytes[0] == 1 && (bytes.Length - 5) % 2 != 0))
                throw new FormatException("Invalid binary parameter or PCM16 alignment");
            var payload = new byte[bytes.Length - 5];
            Buffer.BlockCopy(bytes, 5, payload, 0, payload.Length);
            return new BinaryMessage { Kind = (BinaryKind)bytes[0], SampleRateOrWidth = parameter, Payload = payload };
        }

        [Serializable] class JsonString { public string value; }
        public static string Quote(string value)
        {
            // JsonUtility supplies correct control-character, Unicode and quote escaping.
            var wrapper = JsonUtility.ToJson(new JsonString { value = value ?? "" });
            return wrapper.Substring(9, wrapper.Length - 10); // {"value":<string>}
        }

        public static string StringField(Dictionary<string, string> fields, string key)
        {
            if (!fields.TryGetValue(key, out var raw) || !raw.StartsWith("\""))
                throw new FormatException("Missing string field: " + key);
            return JsonUtility.FromJson<JsonString>("{\"value\":" + raw + "}").value;
        }

        /// <summary>Small JSON validator/slicer. Values remain raw; nested application data is never reserialized.</summary>
        public static Dictionary<string, string> ObjectFields(string json)
        {
            var reader = new JsonReader(json);
            var fields = reader.Object(0);
            reader.White();
            if (!reader.End) throw new FormatException("Trailing JSON text");
            return fields;
        }

        public static void ValidateJson(string json)
        {
            var reader = new JsonReader(json);
            reader.Value(0);
            reader.White();
            if (!reader.End) throw new FormatException("Trailing JSON text");
        }

        sealed class JsonReader
        {
            readonly string m_Json;
            int m_Pos;
            public bool End => m_Pos == m_Json.Length;
            public JsonReader(string json) { m_Json = json ?? throw new FormatException("Missing JSON"); }
            public void White() { while (!End && " \t\r\n".IndexOf(m_Json[m_Pos]) >= 0) m_Pos++; }
            bool Take(char c) { White(); if (End || m_Json[m_Pos] != c) return false; m_Pos++; return true; }
            void Need(char c) { if (!Take(c)) throw new FormatException("Expected '" + c + "'"); }
            string SliceString()
            {
                White();
                int start = m_Pos;
                Need('"');
                while (!End)
                {
                    char c = m_Json[m_Pos++];
                    if (c == '"') return m_Json.Substring(start, m_Pos - start);
                    if (c < 32) throw new FormatException("Control character in JSON string");
                    if (c != '\\') continue;
                    if (End) break;
                    c = m_Json[m_Pos++];
                    if (c == 'u')
                    {
                        for (int i = 0; i < 4; i++)
                            if (End || !Uri.IsHexDigit(m_Json[m_Pos++])) throw new FormatException("Invalid Unicode escape");
                    }
                    else if ("\"\\/bfnrt".IndexOf(c) < 0) throw new FormatException("Invalid JSON escape");
                }
                throw new FormatException("Unterminated JSON string");
            }
            public Dictionary<string, string> Object(int depth)
            {
                if (depth > 64) throw new FormatException("JSON nesting exceeds 64");
                Need('{');
                var fields = new Dictionary<string, string>();
                if (Take('}')) return fields;
                do
                {
                    var key = JsonUtility.FromJson<JsonString>("{\"value\":" + SliceString() + "}").value;
                    Need(':');
                    White();
                    int start = m_Pos;
                    Value(depth + 1);
                    if (fields.ContainsKey(key)) throw new FormatException("Duplicate JSON key");
                    fields.Add(key, m_Json.Substring(start, m_Pos - start));
                } while (Take(','));
                Need('}');
                return fields;
            }
            public void Value(int depth)
            {
                if (depth > 64) throw new FormatException("JSON nesting exceeds 64");
                White();
                if (End) throw new FormatException("Missing JSON value");
                char c = m_Json[m_Pos];
                if (c == '{') { Object(depth); return; }
                if (c == '[')
                {
                    m_Pos++;
                    if (Take(']')) return;
                    do { Value(depth + 1); } while (Take(','));
                    Need(']');
                    return;
                }
                if (c == '"') { SliceString(); return; }
                int start = m_Pos;
                while (!End && ",]} \t\r\n".IndexOf(m_Json[m_Pos]) < 0) m_Pos++;
                var token = m_Json.Substring(start, m_Pos - start);
                if (token == "true" || token == "false" || token == "null") return;
                // JSON's number grammar excludes NaN, Infinity, leading zeroes, and leading '+'.
                if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$"))
                    throw new FormatException("Invalid JSON value");
            }
        }
    }
}
