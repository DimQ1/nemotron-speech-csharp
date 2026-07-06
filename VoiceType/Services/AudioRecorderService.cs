using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Lame;
using NAudio.Wave;

namespace VoiceType.Services;

/// <summary>
/// Records audio to MP3 on-the-fly via a background encoder thread.
/// Append only buffers float samples; PCM conversion and MP3 encoding run on a background thread.
/// </summary>
public sealed class AudioRecorderService : IDisposable
{
    private readonly int _sampleRate;
    private readonly object _sync = new();
    private Channel<float[]>? _channel;
    private Task? _encoderTask;
    private CancellationTokenSource? _cts;
    private string? _tempMp3Path;
    private LameMP3FileWriter? _mp3Writer;
    private Exception? _encoderException;
    private bool _hasAudio;
    private bool _recording;

    public AudioRecorderService(int sampleRate = 16000) => _sampleRate = sampleRate;

    public void Start()
    {
        lock (_sync)
        {
            Cleanup();
            _cts = new CancellationTokenSource();
            _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
            _tempMp3Path = Path.Combine(Path.GetTempPath(), $"VoiceType_{Guid.NewGuid():N}.mp3.tmp");
            _mp3Writer = new LameMP3FileWriter(_tempMp3Path, new WaveFormat(_sampleRate, 16, 1), LAMEPreset.STANDARD);
            _encoderException = null;
            _hasAudio = false;
            _recording = true;

            _encoderTask = Task.Run(() => EncodeLoop(_cts.Token));
        }
    }

    public async Task AppendAsync(float[] samples)
    {
        if (!_recording || _channel is null || samples.Length == 0)
            return;

        // Buffer samples — PCM conversion and MP3 encoding run on background thread
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

            // Wait for encoder to finish
            try { _encoderTask?.GetAwaiter().GetResult(); }
            catch { }

            CleanupEncoder();

            if (_encoderException is not null || !_hasAudio || string.IsNullOrEmpty(_tempMp3Path) || !File.Exists(_tempMp3Path))
            {
                Cleanup();
                return null;
            }

            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);

            var mp3Path = Path.ChangeExtension(filePath, ".mp3");

            try
            {
                File.Move(_tempMp3Path, mp3Path, overwrite: true);
                _tempMp3Path = null;
                _hasAudio = false;
                return mp3Path;
            }
            catch
            {
                try { File.Delete(mp3Path); } catch { }
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
            var writer = _mp3Writer;
            var reader = _channel?.Reader;
            if (writer is null || reader is null) return;

            await foreach (var samples in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var pcm = ConvertToPcm(samples);
                writer.Write(pcm, 0, pcm.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _encoderException = ex;
            Console.Error.WriteLine($"[VoiceType] Encoder error: {ex.Message}");
        }
    }

    private static byte[] ConvertToPcm(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        var shorts = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            var s = ToPcm16(samples[i]);
            shorts[i] = s;
        }
        return pcm;
    }

    private void CleanupEncoder()
    {
        _mp3Writer?.Dispose();
        _mp3Writer = null;
    }

    private void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _mp3Writer?.Dispose();
        _mp3Writer = null;
        _channel = null;
        _encoderTask = null;
        _encoderException = null;

        if (_tempMp3Path is not null)
        {
            try { File.Delete(_tempMp3Path); } catch { }
            _tempMp3Path = null;
        }

        _hasAudio = false;
    }

    private static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        // Round to nearest integer — avoids truncation bias (e.g. 16383.5 → 16384, not 16383)
        return (short)MathF.Round(clamped * short.MaxValue);
    }
}
