namespace VoiceType.Uno.Services;

/// <summary>
/// Download speed of one model, derived from the percent the service reports (the service
/// does not expose bytes): bytes = percent × size, smoothed over samples. Feeds
/// <see cref="ModelsPageFormatter.DescribeRate"/> for "3.1 MB/s · 7 min".
/// </summary>
public sealed class LlmUiDownloadRate
{
    private const double Smoothing = 0.3;

    private double _lastPercent = -1;
    private DateTime _lastAt;
    private double _bytesPerSecond;

    public double BytesPerSecond => _bytesPerSecond;

    public void Reset()
    {
        _lastPercent = -1;
        _bytesPerSecond = 0;
    }

    /// <summary>Records a sample; returns the smoothed speed in bytes per second (0 until two samples).</summary>
    public double Update(double percent, long totalBytes, DateTime now)
    {
        if (percent < _lastPercent || _lastPercent < 0)
        {
            // First sample or a restarted download.
            _bytesPerSecond = 0;
            _lastPercent = percent;
            _lastAt = now;
            return 0;
        }

        var seconds = (now - _lastAt).TotalSeconds;
        if (seconds < 0.5 || totalBytes <= 0)
            return _bytesPerSecond;

        var instant = (percent - _lastPercent) / 100d * totalBytes / seconds;
        _bytesPerSecond = _bytesPerSecond <= 0 ? instant : _bytesPerSecond + Smoothing * (instant - _bytesPerSecond);
        _lastPercent = percent;
        _lastAt = now;
        return _bytesPerSecond;
    }
}
