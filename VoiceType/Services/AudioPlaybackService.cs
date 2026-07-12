using System;
using System.IO;
using NAudio.Wave;

namespace VoiceType.Services;

/// <summary>
/// Wraps NAudio for simple audio playback with position tracking.
/// Supports WAV, MP3, FLAC, M4A (AAC), OGG (Vorbis) via NAudio.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private bool _isDisposed;

    /// <summary>Fires every ~100ms while playing.</summary>
    public event Action<TimeSpan>? PositionChanged;

    /// <summary>Fires when playback reaches the end naturally.</summary>
    public event Action? PlaybackEnded;

    public TimeSpan Position
    {
        get => _reader is not null ? _reader.CurrentTime : TimeSpan.Zero;
        set
        {
            if (_reader is not null)
                _reader.CurrentTime = value;
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public bool HasAudio => _reader is not null;

    public void Open(string filePath)
    {
        Close();

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found", filePath);

        _reader = new AudioFileReader(filePath);
        _output = new WaveOutEvent();
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_reader);
    }

    public void Play()
    {
        if (_output is null) return;
        _output.Play();
    }

    public void Pause()
    {
        _output?.Pause();
    }

    public void Stop()
    {
        _output?.Stop();
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.Zero;
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is not null)
            _reader.CurrentTime = position;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Natural end (not user stop)
        if (_reader is not null && _reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(50))
        {
            PlaybackEnded?.Invoke();
        }
    }

    public void Close()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;

        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Close();
    }
}
