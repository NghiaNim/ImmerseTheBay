using System;

namespace ImmerseTheBay.Learning
{
    /// <summary>
    /// One recognition request, including stop-to-result processing. Native adapters own microphone
    /// exclusivity and restore prior capture state on final/error/disposal. Callbacks may be off-thread.
    /// </summary>
    public interface ISpeechToText : IDisposable
    {
        event Action<string> PartialResult;
        event Action<string> FinalResult;
        event Action<string> Error;
        bool IsListening { get; }
        void StartListening();
        void StopListening();
    }
}
