using NAudio.Lame;
using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>MP3 recorder backed by NAudio 3 + LAME encoder.</summary>
public sealed class NAudio3AudioRecorder : BufferedAudioRecorder
{
    public NAudio3AudioRecorder(int sampleRate) : base(sampleRate) { }

    public override string FileExtension => ".mp3";

    protected override IPcmSink CreateSink(string tempPath, int sampleRate) =>
        new LameSink(tempPath, sampleRate);

    private sealed class LameSink : IPcmSink
    {
        private readonly LameMP3FileWriter _writer;

        public LameSink(string tempPath, int sampleRate) =>
            _writer = new LameMP3FileWriter(tempPath, new WaveFormat(sampleRate, 16, 1), LAMEPreset.STANDARD);

        public void Write(byte[] pcm) => _writer.Write(pcm, 0, pcm.Length);

        public void Dispose() => _writer.Dispose();
    }
}

/// <summary>Creates <see cref="NAudio3AudioRecorder"/> instances.</summary>
public sealed class NAudio3AudioRecorderFactory : IAudioRecorderFactory
{
    public IAudioRecorder Create(int sampleRate) => new NAudio3AudioRecorder(sampleRate);
}
