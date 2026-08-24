using System.Runtime.InteropServices;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// Linux audio capture via ALSA (libasound) — the baseline audio API present on
/// every Ubuntu system, underneath PulseAudio/PipeWire compatibility layers.
/// Microphone mode captures the default PCM device; loopback ("WhatYouHear")
/// requires a PulseAudio/PipeWire monitor source and is not implemented yet.
/// </summary>
public sealed class AlsaAudioSourceFactory : IAudioSourceFactory
{
    public IAudioSource Create(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic => new AlsaAudioSource(sampleRate),
        _ => throw new NotSupportedException(
            $"Capture mode '{mode}' is not supported by the ALSA backend yet. " +
            "Loopback capture requires a PulseAudio/PipeWire monitor source.")
    };
}

/// <summary>
/// Captures 16-bit PCM mono audio from the default ALSA device ("default")
/// and pushes float32 batches into the shared <see cref="ConcurrentQueueWrapper"/>.
/// Runs the blocking capture loop on the caller thread, exactly like the NAudio
/// sources used by the WinUI app.
/// </summary>
internal sealed class AlsaAudioSource : IAudioSource
{
    private const string DeviceName = "default";
    private const int FramesPerChunk = 1024;

    private readonly int _sampleRate;
    private nint _pcm;
    private nint _hwParams;

    public AlsaAudioSource(int sampleRate) => _sampleRate = sampleRate;

    public int SourceSampleRate => _sampleRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var rc = snd_pcm_open(ref _pcm, DeviceName, SND_PCM_STREAM_CAPTURE, 0);
        ThrowIfError(rc, "snd_pcm_open(default)");

        _hwParams = Marshal.AllocHGlobal(snd_pcm_hw_params_sizeof());
        try
        {
            ThrowIfError(snd_pcm_hw_params_any(_pcm, _hwParams), "hw_params_any");
            ThrowIfError(snd_pcm_hw_params_set_access(_pcm, _hwParams, SND_PCM_ACCESS_RW_INTERLEAVED), "set_access");
            ThrowIfError(snd_pcm_hw_params_set_format(_pcm, _hwParams, SND_PCM_FORMAT_S16_LE), "set_format");
            ThrowIfError(snd_pcm_hw_params_set_channels(_pcm, _hwParams, 1), "set_channels");
            var rate = _sampleRate;
            ThrowIfError(snd_pcm_hw_params_set_rate_near(_pcm, _hwParams, ref rate, 0), "set_rate");
            ThrowIfError(snd_pcm_hw_params(_pcm, _hwParams), "hw_params");
            ThrowIfError(snd_pcm_prepare(_pcm), "snd_pcm_prepare");

            var pcm16 = new short[FramesPerChunk];
            while (state.IsRunning)
            {
                var frames = snd_pcm_readi(_pcm, pcm16, (nuint)FramesPerChunk);
                if (frames == -EPIPE)
                {
                    // Overrun: recover and continue
                    snd_pcm_prepare(_pcm);
                    continue;
                }
                if (frames < 0)
                    ThrowIfError((int)frames, "snd_pcm_readi");

                var floats = new float[frames];
                for (var i = 0; i < frames; i++)
                    floats[i] = pcm16[i] / 32768f;

                buffer.Enqueue(floats);
                signal.Set();
            }
        }
        finally
        {
            snd_pcm_close(_pcm);
            _pcm = 0;
            Marshal.FreeHGlobal(_hwParams);
            _hwParams = 0;
        }
    }

    public void Dispose()
    {
        if (_pcm != 0)
        {
            snd_pcm_close(_pcm);
            _pcm = 0;
        }
        if (_hwParams != 0)
        {
            Marshal.FreeHGlobal(_hwParams);
            _hwParams = 0;
        }
    }

    private const int EPIPE = 32;
    private const int SND_PCM_STREAM_CAPTURE = 1;
    private const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;
    private const int SND_PCM_FORMAT_S16_LE = 2;

    private static void ThrowIfError(int rc, string op)
    {
        if (rc < 0)
            throw new InvalidOperationException(
                $"ALSA {op} failed ({rc}): {Marshal.PtrToStringAnsi(snd_strerror(rc))}");
    }

    [DllImport("libasound.so.2")] private static extern int snd_pcm_open(ref nint pcm, string name, int stream, int mode);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_close(nint pcm);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_prepare(nint pcm);
    [DllImport("libasound.so.2")] private static extern long snd_pcm_readi(nint pcm, short[] buffer, nuint size);
    [DllImport("libasound.so.2")] private static extern nint snd_strerror(int errnum);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_sizeof();
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_any(nint pcm, nint p);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_set_access(nint pcm, nint p, int access);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_set_format(nint pcm, nint p, int format);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_set_channels(nint pcm, nint p, uint channels);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params_set_rate_near(nint pcm, nint p, ref int rate, int dir);
    [DllImport("libasound.so.2")] private static extern int snd_pcm_hw_params(nint pcm, nint p);
}
