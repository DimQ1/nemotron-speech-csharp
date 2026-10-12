using System.Diagnostics;
using SpeechLib.AudioStorage;
using VoiceType.Uno.Services.Playback;

// Plays a recording through the real Windows output and checks PositionChanged against the
// wall clock. You cannot judge the sound from here; the device callback counters show that
// WASAPI actually pulled audio.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only (WASAPI output).");
    return 2;
}
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: PlaybackProbe <wav16k> [<wav16k> ...]");
    return 2;
}

// 1. Build a ~20+ s Ogg Opus recording from the input WAVs (as the app stores it).
var samples = args.SelectMany(a => RecordingAudioReader.ReadMono(a)).ToArray();
var dir = Path.Combine(Path.GetTempPath(), "playback-probe");
Directory.CreateDirectory(dir);
var writer = RecordingAudioWriterFactory.Create(Path.Combine(dir, "probe"), RecordingAudioFormat.Standard, queued: false);
for (int i = 0; i < samples.Length; i += 1600)
    writer.Append(samples.AsSpan(i, Math.Min(1600, samples.Length - i)));
var stored = await writer.CompleteAsync();
await writer.DisposeAsync();
Console.WriteLine($"Stored {stored.Path}: {stored.DurationSeconds:F2} s, {stored.Bytes / 1024.0:F1} KB ({stored.Format})");

// 2. Player over a counting decorator of the WASAPI output.
var counting = new CountingOutput(new WasapiPcmOutput());
await using var player = new PcmAudioPlayer(() => counting);
var clock = Stopwatch.StartNew();
var log = new List<(double Wall, double Position)>();
double endedAt = -1;
player.PositionChanged += p => { lock (log) log.Add((clock.Elapsed.TotalSeconds, p.TotalSeconds)); };
player.Ended += () => endedAt = clock.Elapsed.TotalSeconds;
player.PlayingChanged += playing => Console.WriteLine($"  [{clock.Elapsed.TotalSeconds,7:F3}] PlayingChanged({playing})");

var load = Stopwatch.StartNew();
await player.LoadAsync(stored.Path);
Console.WriteLine($"Loaded in {load.ElapsedMilliseconds} ms, Duration {player.Duration.TotalSeconds:F3} s");

int failures = 0;

// Measures one steady stretch: reported position vs wall clock × speed.
async Task Phase(string name, double speed, double seconds, Action? start = null)
{
    lock (log) log.Clear();
    player.Speed = speed;
    start?.Invoke();
    double t0 = clock.Elapsed.TotalSeconds;
    var startPosition = player.Position.TotalSeconds;
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    (double Wall, double Position)[] points;
    lock (log) points = log.ToArray();
    var endPosition = player.Position.TotalSeconds;
    double wall = clock.Elapsed.TotalSeconds - t0;

    // Skip the first 300 ms (device start-up latency), then fit position = a + speed·wall.
    var steady = points.Where(p => p.Wall - t0 > 0.3).ToArray();
    double maxError = 0, meanError = 0, rate = double.NaN;
    if (steady.Length > 2)
    {
        var first = steady[0];
        var errors = steady.Select(p => (p.Position - first.Position) - (p.Wall - first.Wall) * speed).ToArray();
        maxError = errors.Max(Math.Abs) * 1000;
        meanError = errors.Average() * 1000;
        var last = steady[^1];
        rate = (last.Position - first.Position) / (last.Wall - first.Wall);
    }
    double hz = points.Length / wall;
    bool ok = steady.Length > 2 && Math.Abs(rate - speed) < 0.02 * speed && maxError < 40;
    if (!ok) failures++;
    Console.WriteLine(
        $"{(ok ? "OK  " : "FAIL")} {name,-28} speed {speed:0.00}  pos {startPosition,6:F2}→{endPosition,6:F2} s in {wall:F2} s wall | " +
        $"rate {rate:F4}× | |err| max {maxError:F1} ms, mean {meanError:+0.0;-0.0} ms | {hz:F1} Hz events | " +
        $"device: {counting.Calls} render calls, {counting.Frames / 16000.0:F2} s pulled");
}

await Phase("play from 0", 1.0, 3.0, player.Play);
await Phase("seek to 10 s while playing", 1.0, 2.0, () => player.Seek(TimeSpan.FromSeconds(10)));
await Phase("speed 2.0 (live change)", 2.0, 3.0);
await Phase("speed 0.75 (live change)", 0.75, 2.0);
await Phase("speed 1.5", 1.5, 2.0);

// Pause: position must hold still.
player.Speed = 1.0;
player.Pause();
var paused = player.Position;
await Task.Delay(800);
bool pauseOk = player.Position == paused && !player.IsPlaying;
if (!pauseOk) failures++;
Console.WriteLine($"{(pauseOk ? "OK  " : "FAIL")} pause holds position at {paused.TotalSeconds:F3} s (after 0.8 s: {player.Position.TotalSeconds:F3})");

// Seek while paused, then resume.
player.Seek(TimeSpan.FromSeconds(3));
await Phase("seek 3 s paused, resume", 1.0, 1.5, player.Play);

// Ended: seek 1.5 s before the end, expect Ended ≈ 1.5 s later.
var tail = player.Duration - TimeSpan.FromSeconds(1.5);
double seekAt = clock.Elapsed.TotalSeconds;
player.Seek(tail);
for (int i = 0; i < 60 && endedAt < 0; i++) await Task.Delay(50);
double endedAfter = endedAt - seekAt;
bool endedOk = endedAt > 0 && Math.Abs(endedAfter - 1.5) < 0.15 && player.Position == player.Duration && !player.IsPlaying;
if (!endedOk) failures++;
Console.WriteLine($"{(endedOk ? "OK  " : "FAIL")} Ended {endedAfter:F3} s after seeking to end − 1.5 s; Position {player.Position.TotalSeconds:F3} / {player.Duration.TotalSeconds:F3}");

// Play after the end restarts from 0.
player.Play();
await Task.Delay(400);
bool replayOk = player.IsPlaying && player.Position.TotalSeconds is > 0.1 and < 0.6;
if (!replayOk) failures++;
Console.WriteLine($"{(replayOk ? "OK  " : "FAIL")} Play after Ended restarts: {player.Position.TotalSeconds:F3} s");
player.Pause();

Console.WriteLine($"Device: {counting.Starts} starts, {counting.Calls} render calls, {counting.Frames / 16000.0:F2} s pulled");
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

/// <summary>Counts what the device actually pulled.</summary>
internal sealed class CountingOutput(IPcmOutput inner) : IPcmOutput
{
    public long Calls, Frames;
    public int Starts;

    public event Action<Exception>? Failed
    {
        add => inner.Failed += value;
        remove => inner.Failed -= value;
    }

    public long PlayedFrames => inner.PlayedFrames;

    public void Start(int sampleRate, PcmRender render)
    {
        Starts++;

        inner.Start(sampleRate, buffer =>
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref Frames, buffer.Length);
            return render(buffer);
        });
    }

    public void Stop() => inner.Stop();

    public void Dispose() => inner.Dispose();
}
