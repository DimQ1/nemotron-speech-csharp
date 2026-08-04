using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>
/// WAV recorder backed by NAudio 2 (no LAME dependency).
/// Produces uncompressed PCM .wav output.
/// </summary>
public sealed class NAudio2AudioRecorder : BufferedAudioRecorder
{
    public NAudio2AudioRecorder(int sampleRate) : base(sampleRate) { }

    public override string FileExtension => ".wav";

    protected override IPcmSink CreateSink(string tempPath, int sampleRate) =>
        new WaveSink(tempPath, sampleRate);

    private sealed class WaveSink : IPcmSink
    {
        private readonly WaveFileWriter _writer;

        public WaveSink(string tempPath, int sampleRate) =>
            _writer = new WaveFileWriter(tempPath, new WaveFormat(sampleRate, 16, 1));

        public void Write(byte[] pcm) => _writer.Write(pcm, 0, pcm.Length);

        public void Dispose() => _writer.Dispose();
    }
}

/// <summary>Creates <see cref="NAudio2AudioRecorder"/> instances.</summary>
public sealed class NAudio2AudioRecorderFactory : IAudioRecorderFactory
{
    public IAudioRecorder Create(int sampleRate) => new NAudio2AudioRecorder(sampleRate);
}
