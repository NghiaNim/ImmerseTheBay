using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using ImmerseTheBay.Visko;

namespace ImmerseTheBay.Learning
{
    [Serializable]
    public sealed class LearningOverlayData
    {
        public string jp, kana, romaji, zh, en;
        public int hintLevel;
    }

    [Serializable]
    public sealed class LearningFeedback
    {
        public float score;
        public string[] notes;
    }

    [Serializable]
    public sealed class LearningCommandData { public string prompt, resolution; }

    [Serializable]
    public sealed class LearningCommand
    {
        public string name;
        public LearningCommandData data;

        // Serialize only the exact fields allowed for this command, not a union of nullable fields.
        public string Arguments()
        {
            switch (name)
            {
                case "set_prompt":
                case "set_audio_prompt":
                    if (data == null || string.IsNullOrWhiteSpace(data.prompt))
                        throw new FormatException("Agent prompt is missing.");
                    return "{\"prompt\":" + LearningContract.Quote(data.prompt) + "}";
                case "set_resolution":
                    if (data == null || data.resolution != "native")
                        throw new FormatException("Learning requires native resolution.");
                    return "{\"resolution\":\"native\"}";
                case "start": case "pause": case "resume": case "reset": return "{}";
                default: throw new FormatException("Unsupported learning command.");
            }
        }
    }

    [Serializable]
    public sealed class LearningResponse
    {
        public LearningCommand[] visko;
        public LearningOverlayData overlay;
        public LearningFeedback feedback;
        public string advance;
        // Present only on /sync events. Their cursor is already advanced by the sender.
        public string type, sessionId, lessonId;
        public int beat;
    }

    /// <summary>Closed portal contract. Optional context strings must be JSON null, never empty enums.</summary>
    public static class LearningContract
    {
        public static string Quote(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string ContextJson(LearningContextSnapshot c)
        {
            if (c == null) return "{}";
            var sb = new StringBuilder("{\"gesture\":").Append(Quote(EmptyToNull(c.gesture)))
                .Append(",\"pointedAt\":").Append(Quote(EmptyToNull(c.pointedAt)))
                .Append(",\"glassesKey\":").Append(Quote(EmptyToNull(c.glassesKey)))
                .Append(",\"gaze\":").Append(Quote(string.IsNullOrEmpty(c.gaze) ? "room" : c.gaze))
                .Append(",\"rms\":").Append((float.IsNaN(c.rms) ? 0 : Mathf.Clamp01(c.rms)).ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"planes\":{\"horizontalAtDeskHeight\":")
                .Append(c.planes != null && c.planes.horizontalAtDeskHeight ? "true" : "false")
                .Append("},\"roomLabels\":[");
            if (c.roomLabels != null)
                for (int i = 0; i < c.roomLabels.Length && i < 20; i++)
                {
                    if (i != 0) sb.Append(',');
                    sb.Append(Quote(c.roomLabels[i]));
                }
            return sb.Append("]}").ToString();
        }

        public static string TurnJson(string session, string lesson, int beat, string transcript, LearningContextSnapshot context)
        {
            if (transcript != null && transcript.Length > 1000)
                throw new ArgumentException("Speech result is too long; please use one sentence.");
            return "{\"sessionId\":" + Quote(session) + ",\"lessonId\":" + Quote(lesson) +
                ",\"beat\":" + beat.ToString(CultureInfo.InvariantCulture) +
                ",\"transcript\":" + Quote(transcript ?? "") + ",\"context\":" + ContextJson(context) + "}";
        }

        public static LearningResponse ParseResponse(string json, bool commandsRequired = true)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) throw new FormatException("Invalid learning response size.");
            var response = JsonUtility.FromJson<LearningResponse>(json);
            if (response == null || (response.advance != "stay" && response.advance != "next_line" && response.advance != "scene_done"))
                throw new FormatException("Invalid learning progression.");
            // JsonUtility may instantiate nested serializable classes for JSON null on some Unity versions.
            if (ViskoSession.JsonObjectField(json, "overlay") == null) response.overlay = null;
            if (ViskoSession.JsonObjectField(json, "feedback") == null) response.feedback = null;
            if (response.advance != "scene_done" && (response.overlay == null || string.IsNullOrEmpty(response.overlay.jp)))
                throw new FormatException("Missing learning subtitle.");
            if (response.advance == "scene_done" && response.overlay != null)
                throw new FormatException("Completed lesson must not contain a target line.");
            if (commandsRequired)
            {
                if (response.visko == null || response.visko.Length > 20) throw new FormatException("Invalid learning commands.");
                for (int i = 0; i < response.visko.Length; i++)
                {
                    var command = response.visko[i] ?? throw new FormatException("Empty learning command.");
                    command.Arguments(); // Validate the entire batch before executing any command.
                    if (command.name == "start" && (i == 0 || response.visko[i - 1].name != "set_resolution"))
                        throw new FormatException("Native resolution must immediately precede start.");
                }
            }
            return response;
        }

        public static string SyncJson(string responseJson, string session, string lesson, int beat)
        {
            string body = responseJson.Trim();
            return "{\"type\":\"learn\",\"sessionId\":" + Quote(session) + ",\"lessonId\":" + Quote(lesson) +
                ",\"beat\":" + beat.ToString(CultureInfo.InvariantCulture) + "," + body.Substring(1);
        }

        static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
