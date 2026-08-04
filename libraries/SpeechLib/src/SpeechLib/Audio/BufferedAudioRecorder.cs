using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace SpeechLib.Audio;

/// <summary>
/// Base implementation of <see cref="IAudioRecorder"/>: buffers float samples and
/// encodes them to 16-bit mono PCM on a background thread, delegating the actual
/// file writing to a provider-specific <see cref="IPcmSink"/>.
/// </summary>
public abstract class BufferedAudioRecorder : IAudioRecorder
{
    /// <summary>Provider-specific encoded-file writer (MP3, WAV, ...).</summary>
    protected interface IPcmSink : IDisposable
    {
        void Write(byte[] pcm);
    }

    private readonly int _sampleRate;
    private readonly object _sync = new();
    private Channel<float[]>? _channel;
    private Task? _encoderTask;
    private CancellationTokenSource? _cts;
    private string? _tempPath;
    private IPcmSink? _sink;
    private Exception? _encoderException;
    private bool _hasAudio;
    private bool _recording;

    protected BufferedAudioRecorder(int sampleRate) => _sampleRate = sampleRate;

    public abstract string FileExtension { get; }

    public void Start(string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        lock (_sync)
        {
            Cleanup();
            Directory.CreateDirectory(tempDirectory);
            _cts = new CancellationTokenSource();
            _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            _tempPath = Path.Combine(tempDirectory, $"recording_{Guid.NewGuid():N}.tmp");
            _sink = CreateSink(_tempPath, _sampleRate);
            _encoderException = null;
            _hasAudio = false;
            _recording = true;

            _encoderTask = Task.Run(() => EncodeLoop(_cts.Token));
        }
    }

    /// <summary>Creates the provider-specific sink writing to <paramref name="tempPath"/>.</summary>
    protected abstract IPcmSink CreateSink(string tempPath, int sampleRate);

    public async Task AppendAsync(float[] samples)
    {
        if (!_recording || _channel is null || samples.Length == 0)
            return;

        try
        {
            await _channel.Writer.WriteAsync(samples).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _hasAudio = true;
    }

    public string? StopAndSave(string filePath)
    {
        lock (_sync)
        {
            _recording = false;
            _channel?.Writer.Complete();

            try { _encoderTask?.GetAwaiter().GetResult(); }
            catch { }

            _sink?.Dispose();
            _sink = null;

            if (_encoderException is not null || !_hasAudio || string.IsNullOrEmpty(_tempPath) || !File.Exists(_tempPath))
            {
                Cleanup();
                return null;
            }

            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);

            var outPath = Path.ChangeExtension(filePath, FileExtension);

            try
            {
                File.Move(_tempPath, outPath, overwrite: true);
                _tempPath = null;
                _hasAudio = false;
                return outPath;
            }
            catch
            {
                try { File.Delete(outPath); } catch { }
                Cleanup();
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _recording = false;
            _channel?.Writer.TryComplete();
            _cts?.Cancel();
            Cleanup();
        }
    }

    private async Task EncodeLoop(CancellationToken ct)
    {
        try
        {
            var sink = _sink;
            var reader = _channel?.Reader;
            if (sink is null || reader is null) return;

            await foreach (var samples in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var pcm = ConvertToPcm(samples);
                sink.Write(pcm);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _encoderException = ex;
            Console.Error.WriteLine($"[SpeechLib] Audio recorder error: {ex.Message}");
        }
    }

    private void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _sink?.Dispose();
        _sink = null;
        _channel = null;
        _encoderTask = null;
        _encoderException = null;

        if (_tempPath is not null)
        {
            try { File.Delete(_tempPath); } catch { }
            _tempPath = null;
        }

        _hasAudio = false;
    }

    private static byte[] ConvertToPcm(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        var shorts = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int i = 0; i < samples.Length; i++)
            shorts[i] = ToPcm16(samples[i]);
        return pcm;
    }

    private static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        // Round to nearest integer — avoids truncation bias (e.g. 16383.5 → 16384, not 16383)
        return (short)MathF.Round(clamped * short.MaxValue);
    }
}
