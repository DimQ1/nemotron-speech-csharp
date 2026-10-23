using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Services.Audio;

/// <summary>
/// One running capture helper process (macOS: <c>voicetype-audio mic|system</c>, Packaging/macOS/voicetype-audio.swift).
/// Its stdout is the stream <see cref="HelperPcmDecoder"/> reads; it ends when its stdin is closed.
/// </summary>
public interface IPcmHelper : IDisposable
{
    /// <summary>The helper's stdout.</summary>
    Stream Output { get; }

    /// <summary>What the helper wrote to stderr so far (its error messages).</summary>
    string ErrorText { get; }

    /// <summary>Exit code once the helper has ended; null while it runs.</summary>
    int? ExitCode { get; }

    /// <summary>Waits for the helper to end and for its stderr to be read to the end.</summary>
    void WaitForExit(TimeSpan timeout);

    /// <summary>Asks the helper to stop (closes its stdin); <see cref="IDisposable.Dispose"/> kills it if it does not.</summary>
    void RequestStop();
}

/// <summary>Samples at one rate, as decoded from the helper stream.</summary>
public readonly record struct PcmChunk(int SampleRate, float[] Samples);

/// <summary>
/// Turns the helper's byte stream into samples, whatever the read sizes: a little-endian Int32 sample rate, then
/// little-endian Float32 mono samples. The bytes AD DE C0 7F (a NaN no device produces) followed by an Int32 announce a
/// new rate (the helper followed another device).
/// </summary>
public sealed class HelperPcmDecoder
{
    /// <summary>The rate-change marker as a little-endian UInt32.</summary>
    public const uint RateChangeMarker = 0x7FC0_DEAD;

    private readonly byte[] _pending = new byte[4];
    private int _pendingCount;
    private bool _expectRate = true;

    /// <summary>The current sample rate; null until the header arrived.</summary>
    public int? SampleRate { get; private set; }

    /// <summary>Decodes what <paramref name="data"/> completes: one chunk per rate (usually one, possibly none).</summary>
    public IReadOnlyList<PcmChunk> Push(ReadOnlySpan<byte> data)
    {
        var chunks = new List<PcmChunk>(1);
        var samples = new List<float>(data.Length / 4 + 1);
        foreach (var b in data)
        {
            _pending[_pendingCount++] = b;
            if (_pendingCount < 4)
                continue;
            _pendingCount = 0;

            if (_expectRate)
            {
                var rate = BinaryPrimitives.ReadInt32LittleEndian(_pending);
                if (rate <= 0 || rate > 768_000)
                    throw new InvalidDataException($"The audio helper reported an impossible sample rate ({rate}).");
                SampleRate = rate;
                _expectRate = false;
            }
            else if (BinaryPrimitives.ReadUInt32LittleEndian(_pending) == RateChangeMarker)
            {
                Flush(chunks, samples);
                _expectRate = true;
            }
            else
            {
                samples.Add(BinaryPrimitives.ReadSingleLittleEndian(_pending));
            }
        }
        Flush(chunks, samples);
        return chunks;
    }

    private void Flush(List<PcmChunk> chunks, List<float> samples)
    {
        if (samples.Count == 0)
            return;
        chunks.Add(new PcmChunk(SampleRate!.Value, samples.ToArray()));
        samples.Clear();
    }
}

/// <summary>
/// A live source made of one or two capture helper processes (macOS: microphone, system audio, or both). Each helper's
/// audio is converted to the recognizer rate (<see cref="StreamingResampler"/>); two are mixed like the other desktop
/// captures (<see cref="MultiSourceMixer"/>). A helper that ends before it delivered anything (a permission refused, no
/// device) fails the capture with the helper's own message, which says where to allow it. One that stops later (a device
/// gone) leaves a mix running on the other one; a single source fails.
/// </summary>
public sealed class HelperAudioSource : IAudioSource
{
    private readonly int _targetRate;
    private readonly IReadOnlyList<(string Name, Func<IPcmHelper> Start)> _helpers;

    /// <param name="targetRate">The rate the recognizer wants.</param>
    /// <param name="helpers">The helpers to run, by name (for messages): one for a single source, two for a mix.</param>
    public HelperAudioSource(int targetRate, IReadOnlyList<(string Name, Func<IPcmHelper> Start)> helpers)
    {
        if (helpers.Count == 0)
            throw new ArgumentException("At least one helper is needed.", nameof(helpers));
        _targetRate = targetRate;
        _helpers = helpers;
    }

    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var streams = new List<HelperStream>();
        try
        {
            foreach (var (name, start) in _helpers)
                streams.Add(new HelperStream(name, start(), _targetRate, signal));

            MultiSourceMixer? mixer = null;
            if (streams.Count > 1)
            {
                // The helpers deliver every ~10-100 ms (device buffer sizes); wait up to 200 ms for a late one.
                mixer = new MultiSourceMixer(maxSkewSamples: _targetRate / 4, idleMixes: 10);
                foreach (var s in streams)
                    s.MixerSource = mixer.AddSource();
            }

            while (state.IsRunning)
            {
                state.Wait(20);

                foreach (var s in streams)
                {
                    while (s.Samples.TryDequeue(out var samples))
                    {
                        if (mixer is null)
                            Publish(buffer, signal, samples);
                        else if (!s.Dropped)
                            mixer.Push(s.MixerSource, samples);
                    }
                }

                foreach (var s in streams)
                {
                    if (!s.Failed || s.Dropped)
                        continue;
                    s.Dropped = true;
                    if (!s.Delivered || streams.All(x => x.Dropped))
                        throw new InvalidOperationException(s.FailureMessage());
                    mixer?.RemoveSource(s.MixerSource);
                    Trace.WriteLine($"[VoiceType.Uno] {s.FailureMessage()} Capture goes on with the other source.");
                }

                if (mixer is not null)
                    Publish(buffer, signal, mixer.Mix());
            }
        }
        finally
        {
            foreach (var s in streams)
                s.RequestStop();
            foreach (var s in streams)
                s.Dispose();
        }
    }

    /// <summary>Nothing to free: the helpers live inside <see cref="Start"/> and end with it.</summary>
    public void Dispose()
    {
    }

    private static void Publish(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, float[] samples)
    {
        if (samples.Length == 0)
            return;
        buffer.Enqueue(samples);
        signal.Set();
    }

    /// <summary>One helper: a reader thread that decodes and resamples its stdout into <see cref="Samples"/>.</summary>
    private sealed class HelperStream : IDisposable
    {
        private readonly IPcmHelper _helper;
        private readonly int _targetRate;
        private readonly ManualResetEventSlim _signal;
        private readonly Thread _reader;
        private volatile bool _failed;
        private volatile bool _delivered;
        private volatile bool _stopping;
        private volatile string? _readError;

        public HelperStream(string name, IPcmHelper helper, int targetRate, ManualResetEventSlim signal)
        {
            Name = name;
            _helper = helper;
            _targetRate = targetRate;
            _signal = signal;
            _reader = new Thread(Read) { IsBackground = true, Name = $"VoiceType audio helper ({name})" };
            _reader.Start();
        }

        public string Name { get; }
        public ConcurrentQueue<float[]> Samples { get; } = new();
        public int MixerSource { get; set; }
        public bool Dropped { get; set; }

        /// <summary>The helper ended (or sent something unreadable) while capture was meant to run.</summary>
        public bool Failed => _failed;

        /// <summary>The helper has delivered samples.</summary>
        public bool Delivered => _delivered;

        /// <summary>The helper's last stderr line (its own explanation), else what went wrong reading it.</summary>
        public string FailureMessage()
        {
            var last = _helper.ErrorText.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return last ?? _readError
                ?? $"The {Name} capture helper stopped (exit code {_helper.ExitCode?.ToString() ?? "unknown"}).";
        }

        public void RequestStop()
        {
            _stopping = true;
            try
            {
                _helper.RequestStop();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // already gone
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _helper.Dispose();
            _reader.Join(TimeSpan.FromSeconds(2));
        }

        private void Read()
        {
            var decoder = new HelperPcmDecoder();
            StreamingResampler? resampler = null;
            var bytes = new byte[16384];
            try
            {
                int read;
                while ((read = _helper.Output.Read(bytes, 0, bytes.Length)) > 0)
                {
                    foreach (var chunk in decoder.Push(bytes.AsSpan(0, read)))
                    {
                        if (resampler is null || resampler.FromRate != chunk.SampleRate)
                            resampler = new StreamingResampler(chunk.SampleRate, _targetRate);
                        var samples = Resample(resampler, chunk.Samples);
                        if (samples.Length == 0)
                            continue;
                        Samples.Enqueue(samples);
                        _delivered = true;
                    }
                    _signal.Set();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                _readError = ex.Message;
            }

            // The stream ended: expected when capture is stopping, a failure otherwise (permission refused, device gone).
            if (_stopping)
                return;
            _helper.WaitForExit(TimeSpan.FromSeconds(2));
            _failed = true;
            _signal.Set();
        }

        private static float[] Resample(StreamingResampler resampler, float[] input)
        {
            if (resampler.IsPassThrough)
                return input;
            var output = new float[resampler.MaxOutputCount(input.Length)];
            var produced = resampler.Process(input, output);
            return produced == output.Length ? output : output.AsSpan(0, produced).ToArray();
        }
    }
}

/// <summary>Runs the macOS capture helper as a child process (so the app bundle's permissions cover it).</summary>
public sealed class ProcessPcmHelper : IPcmHelper
{
    private readonly Process _process;
    private readonly System.Text.StringBuilder _errors = new();

    public ProcessPcmHelper(string helperPath, string mode)
    {
        var info = new ProcessStartInfo(helperPath, mode)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process = new Process { StartInfo = info };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_errors)
                _errors.AppendLine(e.Data);
            Trace.WriteLine($"[voicetype-audio {mode}] {e.Data}");
        };
        _process.Start();
        _process.BeginErrorReadLine();
    }

    public Stream Output => _process.StandardOutput.BaseStream;

    public string ErrorText
    {
        get
        {
            lock (_errors)
                return _errors.ToString();
        }
    }

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public void WaitForExit(TimeSpan timeout)
    {
        // The parameterless overload also waits until stderr is read to the end, so the error line is in ErrorText.
        if (_process.WaitForExit(timeout))
            _process.WaitForExit();
    }

    public void RequestStop()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the helper has already exited
        }
    }

    public void Dispose()
    {
        if (!_process.WaitForExit(TimeSpan.FromSeconds(1.5)))
            _process.Kill();
        _process.Dispose();
    }
}

/// <summary>
/// macOS capture: the microphone, what the Mac plays (Core Audio process tap, macOS 14.2+) or both, through the
/// <c>voicetype-audio</c> helper that VoiceType.app carries next to its executable (Packaging/macOS/build-app.sh).
/// </summary>
public sealed class MacAudioSourceFactory : IAudioSourceFactory
{
    /// <summary>Environment variable that points at another helper binary (development).</summary>
    public const string HelperPathVariable = "VOICETYPE_MAC_AUDIO_HELPER";

    public IAudioSource Create(CaptureMode mode, int sampleRate)
    {
        var helper = FindHelper()
            ?? throw new InvalidOperationException("The audio helper voicetype-audio is missing from VoiceType.app (Contents/MacOS).");
        (string, Func<IPcmHelper>) Helper(string name) => (name, () => new ProcessPcmHelper(helper, name));
        return mode switch
        {
            CaptureMode.Mic => new HelperAudioSource(sampleRate, [Helper("mic")]),
            CaptureMode.Loopback => new HelperAudioSource(sampleRate, [Helper("system")]),
            CaptureMode.Mix => new HelperAudioSource(sampleRate, [Helper("mic"), Helper("system")]),
            _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source."),
        };
    }

    /// <summary>The helper named by <see cref="HelperPathVariable"/>, else the one next to the app executable.</summary>
    public static string? FindHelper()
    {
        var overridden = Environment.GetEnvironmentVariable(HelperPathVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return File.Exists(overridden) ? overridden : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "voicetype-audio");
        return File.Exists(bundled) ? bundled : null;
    }
}
