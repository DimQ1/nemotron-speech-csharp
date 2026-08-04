namespace LVA.Nlu.Asr;

/// <summary>Latency mode of the ASR engine (TZ §4.2).</summary>
public enum AsrLatencyMode
{
    Ms80 = 80,
    Ms160 = 160,
    Ms560 = 560,
    Ms1120 = 1120
}

public sealed class PartialTranscriptEventArgs : EventArgs
{
    public required string Text { get; init; }
}

public sealed class FinalTranscriptEventArgs : EventArgs
{
    public required string Text { get; init; }
}

/// <summary>Streaming ASR abstraction (TZ §4.2). Implemented by the Nemotron adapter.</summary>
public interface IStreamingAsr : IDisposable
{
    /// <summary>Feed 16 kHz mono float audio.</summary>
    void AcceptWaveform(ReadOnlySpan<float> samples);

    /// <summary>Raised on each partial hypothesis (every 80–560 ms depending on mode).</summary>
    event EventHandler<PartialTranscriptEventArgs>? PartialResult;

    /// <summary>Raised when the VAD segment ends with the final transcript.</summary>
    event EventHandler<FinalTranscriptEventArgs>? FinalResult;

    /// <summary>Start a new dialog (reset model caches).</summary>
    void Reset();

    AsrLatencyMode LatencyMode { get; set; }
}
