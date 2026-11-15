#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace ImmerseTheBay.Learning.Editor
{
    /// <summary>Offline checks; no devices, portal, speech, or billable Reactor session.</summary>
    public static class LearningContractChecks
    {
        [MenuItem("Tools/Learning/Check agent contract")]
        public static void Run()
        {
            string context = LearningContract.ContextJson(new LearningContextSnapshot());
            Require(context.Contains("\"gesture\":null"), "Absent gesture must be null, not empty.");
            Require(context.Contains("\"pointedAt\":null"), "Absent point must be null, not empty.");
            Require(context.Contains("\"glassesKey\":null"), "Absent key must be null.");
            string special = "日本語\n\t\"\\\u0001";
            string quoted = LearningContract.Quote(special);
            Require(JsonUtility.FromJson<StringEnvelope>("{\"value\":" + quoted + "}").value == special,
                "Japanese and JSON escapes must round-trip.");
            string turn = LearningContract.TurnJson("session-1", "ja-home-001", 3, special, new LearningContextSnapshot());
            Require(JsonUtility.FromJson<TurnEnvelope>(turn).transcript == special, "Transcript must round-trip.");
            Require(JsonUtility.FromJson<TurnEnvelope>(turn).beat == 3, "Flattened cursor must be preserved.");

            const string target = "\"overlay\":{\"jp\":\"次の文\",\"kana\":\"つぎのぶん\",\"romaji\":\"tsugi no bun\",\"zh\":\"下一句\",\"en\":\"Next sentence\",\"hintLevel\":0}";
            string next = "{\"visko\":[]," + target + ",\"feedback\":null,\"advance\":\"next_line\"}";
            var response = LearningContract.ParseResponse(next);
            Require(response.overlay.jp == "次の文" && response.feedback == null, "Next target and null feedback must survive parsing.");
            var mirror = LearningContract.ParseResponse(LearningContract.SyncJson(next, "session-1", "ja-home-001", 4), false);
            Require(mirror.beat == 4 && mirror.overlay.jp == "次の文", "Sync cursor is already advanced; preserve it verbatim.");
            var done = LearningContract.ParseResponse("{\"visko\":[{\"name\":\"pause\",\"data\":{}}],\"overlay\":null,\"feedback\":null,\"advance\":\"scene_done\"}");
            Require(done.overlay == null, "Completion must clear the subtitle.");

            string nativeStart = "{\"visko\":[{\"name\":\"set_resolution\",\"data\":{\"resolution\":\"native\"}},{\"name\":\"start\",\"data\":{}}]," + target + ",\"feedback\":null,\"advance\":\"stay\"}";
            LearningContract.ParseResponse(nativeStart);
            Reject(() => LearningContract.ParseResponse(nativeStart.Replace("native", "1080p")), "Device must reject unapproved resolution fallback.");
            Reject(() => LearningContract.ParseResponse(nativeStart.Replace("set_resolution", "pause")), "Start without immediate native resolution must fail.");
            Reject(() => LearningContract.ParseResponse(next.Replace("next_line", "skip_scene")), "Unknown advance must fail.");
            Reject(() => LearningContract.ParseResponse(next.Replace("[]", "[{\"name\":\"delete_session\",\"data\":{}}]")), "Unknown commands must fail before execution.");
            Reject(() => LearningContract.TurnJson("session-1", "ja-home-001", 0, new string('a', 1001), null), "Oversized speech must fail locally.");
            string branch = LearningContract.TurnJson("session-1", "ja-home-001", 0, "", null, "request-1", "run-1", 2, "sandwich");
            Require(branch.Contains("\"expectedRevision\":2") && branch.Contains("\"intent\":\"sandwich\""), "Branch must carry replay metadata and intent.");
            string control = LearningContract.TurnJson("session-1", "ja-home-001", 0, "", null, "request-2", "run-1", 3, null, "return_to_main");
            Require(control.Contains("\"action\":\"return_to_main\""), "Return must be an explicit forward action.");
            Reject(() => LearningContract.TurnJson("s", "l", 0, "spoken", null, action: "return_to_main"), "Control actions cannot contain speech.");
            response.operation = new LearningOperation { requestId = "request-1", runId = "run-1", revision = 3, replayed = true };
            LearningContract.ValidateOperation(response, "request-1", "run-1", 2);
            Reject(() => LearningContract.ValidateOperation(response, "other", "run-1", 2), "Wrong request must never execute.");
            Reject(() => LearningContract.ValidateOperation(response, "request-1", "old-run", 2), "Stale run must never execute.");
            Reject(() => LearningContract.ValidateOperation(response, "request-1", "run-1", 3), "Repeated revision must never execute.");
            Debug.Log("[Learning] Agent contract checks passed.");
        }

        [Serializable] sealed class StringEnvelope { public string value; }
        [Serializable] sealed class TurnEnvelope { public string transcript; public int beat; }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (FormatException) { return; }
            throw new Exception(message);
        }
    }
}
#endif
