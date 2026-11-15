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
        public LearningReadingGroup[] readingGroups;
        public int hintLevel;
    }

    [Serializable]
    public sealed class LearningReadingGroup
    {
        public string jp, kana, romaji;
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

    [Serializable] public sealed class LearningOperation
    { public string requestId, runId; public int revision; public bool replayed; }
    [Serializable] public sealed class LearningRoute
    { public string kind, label, nodeId; public int step, total, returnBeat; }
    [Serializable] public sealed class LearningAttempt
    {
        public string id, targetId, jp, text, outcome, nextTargetId;
        public int score;
        public LearningCorrection correction;
    }
    [Serializable] public sealed class LearningCorrection
    {
        public int start, end;
        public string jp;

        public bool Matches(string sentence)
        {
            if (string.IsNullOrEmpty(sentence) || string.IsNullOrWhiteSpace(jp) ||
                start < 0 || end <= start || end > sentence.Length || end - start != jp.Length ||
                string.CompareOrdinal(sentence, start, jp, 0, jp.Length) != 0)
                return false;
            var boundaries = StringInfo.ParseCombiningCharacters(sentence);
            return Array.BinarySearch(boundaries, start) >= 0 &&
                (end == sentence.Length || Array.BinarySearch(boundaries, end) >= 0);
        }
    }
    [Serializable] public sealed class LearningProgress
    { public int mainTotal, mainStep, mainCompleted; }
    [Serializable] public sealed class LearningSpeech
    { public string role, language, text, english, romaji; }
    [Serializable] public sealed class LearningSnapshot
    { public string runId; public int revision, beat; public LearningResponse response; }
    [Serializable] public sealed class LearningVoiceSession
    { public string url, token, version, runId; public int revision; }

    [Serializable]
    public sealed class LearningResponse
    {
        [Serializable]
        public sealed class Scene
        { public string id, startImage; }

        public LearningCommand[] visko;
        public Scene scene;
        public string nextSceneId;
        public LearningOverlayData overlay;
        public LearningFeedback feedback;
        public LearningAttempt attempt;
        public LearningProgress progress;
        public string advance;
        public LearningOperation operation;
        public LearningRoute route;
        public LearningSpeech speech;
        public string[] focusWords;
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

        public static string TurnJson(string session, string lesson, int beat, string transcript, LearningContextSnapshot context, string requestId = null, string runId = null, int revision = 0, string intent = null, string action = null)
        {
            if (transcript != null && transcript.Length > 1000)
                throw new ArgumentException("Speech result is too long; please use one sentence.");
            if (intent != null && intent.Length > 1000) throw new ArgumentException("Intent is too long.");
            if (action != null && action != "return_to_main" && action != "idle_hint") throw new ArgumentException("Invalid lesson action.");
            if (action != null && (!string.IsNullOrEmpty(transcript) || !string.IsNullOrEmpty(intent))) throw new ArgumentException("Action cannot contain speech or intent.");
            string operation = requestId == null ? "" : ",\"operation\":{\"requestId\":" + Quote(requestId) +
                ",\"expectedRevision\":" + revision.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(runId) ? "" : ",\"runId\":" + Quote(runId)) + "}";
            return "{\"sessionId\":" + Quote(session) + ",\"lessonId\":" + Quote(lesson) +
                ",\"beat\":" + beat.ToString(CultureInfo.InvariantCulture) +
                ",\"transcript\":" + Quote(transcript ?? "") + ",\"context\":" + ContextJson(context) + operation + (intent == null ? "" : ",\"intent\":" + Quote(intent)) + (action == null ? "" : ",\"action\":" + Quote(action)) + "}";
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
            if (ViskoSession.JsonObjectField(json, "attempt") == null) response.attempt = null;
            if (ViskoSession.JsonObjectField(json, "progress") == null) response.progress = null;
            if (response.attempt != null)
            {
                var attempt = response.attempt;
                if (string.IsNullOrEmpty(attempt.id) || string.IsNullOrEmpty(attempt.targetId) ||
                    string.IsNullOrEmpty(attempt.jp) || attempt.score < 0 || attempt.score > 100 ||
                    (attempt.outcome != "pass" && attempt.outcome != "retry" && attempt.outcome != "assisted" && attempt.outcome != "skip"))
                    throw new FormatException("Invalid lesson attempt");
                if (attempt.outcome != "retry" || attempt.correction?.Matches(attempt.jp) != true)
                    attempt.correction = null;
            }
            if (response.progress != null)
            {
                var progress = response.progress;
                if (progress.mainTotal < 1 || progress.mainStep < 1 || progress.mainStep > progress.mainTotal ||
                    progress.mainCompleted < 0 || progress.mainCompleted > progress.mainTotal)
                    throw new FormatException("Invalid lesson target count");
            }
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

        public static void ValidateOperation(LearningResponse response, string requestId, string runId, int revision)
        {
            var op = response.operation;
            if (op == null || op.requestId != requestId || string.IsNullOrEmpty(op.runId) ||
                op.revision != revision + 1 || (!string.IsNullOrEmpty(runId) && op.runId != runId))
                throw new FormatException("Stale or invalid lesson operation. Restart safely.");
        }

        public static string AttemptLabel(LearningAttempt attempt)
        {
            if (attempt == null) return "";
            switch (attempt.outcome)
            {
                case "skip": return "Skipped";
                case "assisted": return $"Good job with a hint · {attempt.score}% match";
                case "pass": return $"Good job · {attempt.score}% match";
                default: return $"Please try again · {attempt.score}% match";
            }
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
