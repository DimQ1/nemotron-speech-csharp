using System.Diagnostics;

namespace SpeechLib.Decorators;

/// <summary>
/// Decorator that adds latency and token-count metrics to any IStreamingSpeechRecognizer.
/// Logs per-chunk processing time and cumulative statistics.
/// </summary>
public sealed class MetricsRecognizerDecorator : IStreamingSpeechRecognizer
{
    private readonly IStreamingSpeechRecognizer _inner;
    private readonly string _label;
    private readonly List<double> _latencies = new();
    private int _totalTokens;

    public MetricsRecognizerDecorator(IStreamingSpeechRecognizer inner, string label = "Recognizer")
    {
        _inner = inner;
        _label = label;
    }

    public int SampleRate => _inner.SampleRate;
    public int ChunkSamples => _inner.ChunkSamples;
    public int LastTokenCount => _inner.LastTokenCount;

    /// <summary>Get the inner recognizer (for unwrapping decorators).</summary>
    public IStreamingSpeechRecognizer GetInner() => _inner;

    /// <summary>Average latency per chunk in milliseconds.</summary>
    public double AverageLatencyMs => _latencies.Count > 0 ? _latencies.Average() : 0;

    /// <summary>Total tokens processed across all chunks.</summary>
    public int TotalTokens => _totalTokens;

    public string? ProcessAudio(float[] chunk)
    {
        var sw = Stopwatch.StartNew();
        var result = _inner.ProcessAudio(chunk);
        sw.Stop();

        _latencies.Add(sw.Elapsed.TotalMilliseconds);
        if (result is not null)
            _totalTokens += _inner.LastTokenCount;

        return result;
    }

    public string? Flush()
    {
        var sw = Stopwatch.StartNew();
        var result = _inner.Flush();
        sw.Stop();

        if (result is not null)
            _totalTokens += _inner.LastTokenCount;

        Console.WriteLine($"[{_label}] Avg latency: {AverageLatencyMs:F1}ms, Total tokens: {TotalTokens}");
        return result;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Decorator that adds structured logging to any IStreamingSpeechRecognizer.
/// Logs each ProcessAudio call with text length and timing.
/// </summary>
public sealed class LoggingRecognizerDecorator : IStreamingSpeechRecognizer
{
    private readonly IStreamingSpeechRecognizer _inner;
    private readonly Action<string> _log;
    private int _callCount;

    public LoggingRecognizerDecorator(IStreamingSpeechRecognizer inner, Action<string>? log = null)
    {
        _inner = inner;
        _log = log ?? (msg => Console.WriteLine($"[Recognizer] {msg}"));
    }

    public int SampleRate => _inner.SampleRate;
    public int ChunkSamples => _inner.ChunkSamples;
    public int LastTokenCount => _inner.LastTokenCount;

    public string? ProcessAudio(float[] chunk)
    {
        _callCount++;
        var result = _inner.ProcessAudio(chunk);
        if (result is not null)
            _log($"#{_callCount}: \"{result}\" ({result.Length} chars, {_inner.LastTokenCount} tokens)");
        return result;
    }

    public string? Flush()
    {
        var result = _inner.Flush();
        if (result is not null)
            _log($"Flush: \"{result}\" ({result.Length} chars)");
        return result;
    }

    public void Dispose() => _inner.Dispose();
}
