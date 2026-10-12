// Android playback via AudioTrack (stream mode, PCM float). Only compiled for the
// net10.0-android head; desktop/Windows heads use WASAPI or PulseAudio.
#if __ANDROID__

using Android.Media;

namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// Android output: <see cref="AudioTrack"/> in stream mode, mono PCM float at the recording
/// rate (the platform mixer resamples), ~100 ms buffer, media usage / speech content.
/// A writer thread renders 20 ms chunks with non-blocking writes (so stop never waits on a
/// paused track). Played frames come from <see cref="AudioTrack.PlaybackHeadPosition"/>.
/// </summary>
internal sealed class AudioTrackPcmOutput : IPcmOutput
{
    private const int BufferMilliseconds = 100;
    private const int ChunkMilliseconds = 20;

    private readonly object _sync = new();
    private AudioTrack? _track;
    private Thread? _writer;
    private volatile bool _stopping;

    public event Action<Exception>? Failed;

    public void Start(int sampleRate, PcmRender render)
    {
        Stop();
        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Mono, Encoding.PcmFloat);
        if (minBytes <= 0)
            throw new InvalidOperationException($"AudioTrack does not support {sampleRate} Hz float output.");
        int bufferBytes = Math.Max(minBytes, sampleRate * sizeof(float) * BufferMilliseconds / 1000);

        var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)!
            .SetAudioFormat(new AudioFormat.Builder()
                .SetEncoding(Encoding.PcmFloat)!
                .SetSampleRate(sampleRate)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!)!
            .SetBufferSizeInBytes(bufferBytes)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .Build()!;
        if (track.State != AudioTrackState.Initialized)
        {
            track.Release();
            track.Dispose();
            throw new InvalidOperationException("AudioTrack failed to initialize.");
        }

        track.Play();
        lock (_sync)
        {
            _track = track;
            _stopping = false;
        }
        _writer = new Thread(() => WriteLoop(track, sampleRate, render))
        {
            IsBackground = true,
            Name = "VoiceType playback (AudioTrack)",
        };
        _writer.Start();
    }

    public void Stop()
    {
        var writer = _writer;
        if (writer is null) return;
        _stopping = true;
        writer.Join(TimeSpan.FromSeconds(1));
        _writer = null;
        AudioTrack? track;
        lock (_sync)
        {
            track = _track;
            _track = null;
        }
        if (track is null) return;
        try
        {
            track.Pause();
            track.Flush();
            track.Stop();
        }
        catch (Java.Lang.IllegalStateException) { }
        track.Release();
        track.Dispose();
    }

    public long PlayedFrames
    {
        get
        {
            lock (_sync)
                return _track is null ? 0 : (uint)_track.PlaybackHeadPosition; // unsigned 32-bit frame counter
        }
    }

    public void Dispose() => Stop();

    private void WriteLoop(AudioTrack track, int sampleRate, PcmRender render)
    {
        var chunk = new float[sampleRate * ChunkMilliseconds / 1000];
        try
        {
            while (!_stopping)
            {
                render(chunk);
                int offset = 0;
                while (offset < chunk.Length && !_stopping)
                {
                    int written = track.Write(chunk, offset, chunk.Length - offset, WriteMode.NonBlocking);
                    if (written < 0)
                        throw new InvalidOperationException($"AudioTrack write failed ({written}).");
                    offset += written;
                    if (written == 0) Thread.Sleep(5); // buffer full
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stopping) Failed?.Invoke(ex);
        }
    }
}

#endif
