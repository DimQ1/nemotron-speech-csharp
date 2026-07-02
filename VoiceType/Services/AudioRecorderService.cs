using System.IO;
using System.Buffers;
using NAudio.Lame;
using NAudio.Wave;

namespace VoiceType.Services;

/// <summary>
/// Records audio samples directly to a temporary MP3 file using NAudio + LAME.
/// Avoids buffering the full session in memory or round-tripping through WAV.
/// </summary>
public sealed class AudioRecorderService : IDisposable
{
    private readonly int _sampleRate;
    private readonly object _sync = new();
    private LameMP3FileWriter? _mp3Writer;
    private string? _tempMp3Path;
    private bool _hasAudio;
    private bool _recording;

    public AudioRecorderService(int sampleRate = 16000) => _sampleRate = sampleRate;

    public void Start()
    {
        lock (_sync)
        {
            CleanupWriter(deleteTempFile: true);
            _tempMp3Path = Path.Combine(Path.GetTempPath(), $"VoiceType_{Guid.NewGuid():N}.mp3.tmp");
            _mp3Writer = new LameMP3FileWriter(_tempMp3Path, new WaveFormat(_sampleRate, 16, 1), LAMEPreset.STANDARD);
            _hasAudio = false;
            _recording = true;
        }
    }

    public void Append(float[] samples)
    {
        lock (_sync)
        {
            if (!_recording || _mp3Writer is null || samples.Length == 0)
                return;

            var pcmBytes = ArrayPool<byte>.Shared.Rent(samples.Length * sizeof(short));
            try
            {
                int offset = 0;
                foreach (var sample in samples)
                {
                    var pcm = ToPcm16(sample);
                    pcmBytes[offset++] = (byte)(pcm & 0xFF);
                    pcmBytes[offset++] = (byte)((pcm >> 8) & 0xFF);
                }

                _mp3Writer.Write(pcmBytes, 0, offset);
                _hasAudio = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pcmBytes);
            }
        }
    }

    public string? StopAndSave(string filePath)
    {
        lock (_sync)
        {
            _recording = false;
            if (!_hasAudio || _mp3Writer is null || string.IsNullOrEmpty(_tempMp3Path))
            {
                CleanupWriter(deleteTempFile: true);
                return null;
            }

            var dir = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(dir);

            var mp3Path = Path.ChangeExtension(filePath, ".mp3");

            try
            {
                CleanupWriter(deleteTempFile: false);
                File.Move(_tempMp3Path, mp3Path, overwrite: true);
                _tempMp3Path = null;
                _hasAudio = false;
                return mp3Path;
            }
            catch
            {
                CleanupWriter(deleteTempFile: true);
                try { File.Delete(mp3Path); } catch { }
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _recording = false;
            CleanupWriter(deleteTempFile: true);
            _hasAudio = false;
        }
    }

    private void CleanupWriter(bool deleteTempFile)
    {
        _mp3Writer?.Dispose();
        _mp3Writer = null;

        if (deleteTempFile && !string.IsNullOrEmpty(_tempMp3Path))
        {
            try { File.Delete(_tempMp3Path); } catch { }
        }

        if (deleteTempFile)
            _tempMp3Path = null;
    }

    private static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return clamped <= -1f
            ? short.MinValue
            : (short)(clamped * short.MaxValue);
    }
}
