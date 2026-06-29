using System.IO;
using NAudio.Lame;
using NAudio.Wave;

namespace VoiceType.Services;

/// <summary>
/// Records audio samples to MP3 file using NAudio + LAME.
/// Samples are accumulated in memory and written on stop.
/// </summary>
public sealed class AudioRecorderService : IDisposable
{
    private readonly List<float> _buffer = new();
    private readonly int _sampleRate;
    private bool _recording;

    public AudioRecorderService(int sampleRate = 16000) => _sampleRate = sampleRate;

    public void Start() { _buffer.Clear(); _recording = true; }

    public void Append(float[] samples)
    {
        if (_recording) _buffer.AddRange(samples);
    }

    public string? StopAndSave(string filePath)
    {
        _recording = false;
        if (_buffer.Count == 0) return null;

        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        var wavPath = Path.ChangeExtension(filePath, ".wav");
        var mp3Path = Path.ChangeExtension(filePath, ".mp3");

        try
        {
            // Write WAV first
            var samples = _buffer.ToArray();
            using var writer = new WaveFileWriter(wavPath, new WaveFormat(_sampleRate, 16, 1));
            foreach (var s in samples)
            {
                var clamped = Math.Clamp(s, -1f, 1f);
                writer.WriteSample(clamped);
            }

            // Convert to MP3
            using var reader = new AudioFileReader(wavPath);
            using var mp3Writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, LAMEPreset.STANDARD);
            reader.CopyTo(mp3Writer);

            // Clean up WAV
            File.Delete(wavPath);

            return mp3Path;
        }
        catch
        {
            try { File.Delete(wavPath); } catch { }
            return null;
        }
    }

    public void Dispose() { _recording = false; _buffer.Clear(); }
}
