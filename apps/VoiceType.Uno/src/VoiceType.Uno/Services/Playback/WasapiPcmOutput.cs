using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceType.Uno.Services.Playback;

/// <summary>
/// Windows output (WinUI head and Skia desktop on Windows): NAudio 3 <see cref="WasapiOut"/>,
/// shared mode, event-driven, 60 ms buffer. NAudio.Wasapi is already linked through
/// SpeechLib.Audio.NAudio3 (capture), and unlike WaveOutEvent it needs no NAudio.WinMM.
/// The played-frame count comes from the WASAPI audio clock (<see cref="WasapiOut.GetPosition"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiPcmOutput : IPcmOutput
{
    private const int LatencyMilliseconds = 60;

    private readonly object _sync = new();
    private WasapiOut? _device;
    private int _sampleRate;

    public event Action<Exception>? Failed;

    public void Start(int sampleRate, PcmRender render)
    {
        Stop();
        var device = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, LatencyMilliseconds);
        try
        {
            device.Init(new RenderWaveProvider(sampleRate, render));
            device.PlaybackStopped += OnPlaybackStopped;
            device.Play();
        }
        catch
        {
            device.Dispose();
            throw;
        }
        lock (_sync)
        {
            _device = device;
            _sampleRate = sampleRate;
        }
    }

    public void Stop()
    {
        WasapiOut? device;
        lock (_sync)
        {
            device = _device;
            _device = null;
        }
        if (device is null) return;
        device.PlaybackStopped -= OnPlaybackStopped;
        device.Stop();   // ends the playback thread (no further render calls)
        device.Dispose();
    }

    public long PlayedFrames
    {
        get
        {
            lock (_sync)
            {
                if (_device is null) return 0;
                // Bytes in the device format (after any shared-mode conversion) → seconds → our frames.
                var format = _device.OutputWaveFormat;
                double seconds = (double)_device.GetPosition() / format.AverageBytesPerSecond;
                return (long)(seconds * _sampleRate);
            }
        }
    }

    public void Dispose() => Stop();

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Only unexpected stops reach here (Stop() unsubscribes first).
        Failed?.Invoke(e.Exception ?? new InvalidOperationException("Audio output stopped."));
    }

    /// <summary>Mono IEEE float at the recording rate, rendered on WASAPI's thread.</summary>
    private sealed class RenderWaveProvider(int sampleRate, PcmRender render) : IWaveProvider
    {
        private float[] _scratch = new float[4096];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(Span<byte> buffer)
        {
            int frames = buffer.Length / sizeof(float);
            if (_scratch.Length < frames) _scratch = new float[frames];
            var span = _scratch.AsSpan(0, frames);
            render(span); // always fills: silence after the end keeps the clock running
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(span).CopyTo(buffer);
            return frames * sizeof(float);
        }
    }
}
