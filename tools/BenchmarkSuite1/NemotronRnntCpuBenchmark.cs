using BenchmarkDotNet.Attributes;
using SpeechLib.Audio;
using SpeechLib.Nemotron;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VSDiagnostics;

namespace NemotronSpeech;

/// <summary>
/// Real-time factor of <see cref="NemotronRnntRecognizer"/> (plain ONNX Runtime) per model variant: the sample file is
/// streamed in chunk-sized pieces, then flushed. The transducer decodes greedily, so there is no beam or penalty to vary.
/// </summary>
[CPUUsageDiagnoser]
[MemoryDiagnoser]
public class NemotronRnntCpuBenchmark
{
    private NemotronRnntRecognizer? _session;
    private float[][] _chunks = [];
    private double _audioDurationSeconds;
    private double _lastElapsedSeconds;
    private double _lastRtf;
    private string _lastTranscript = string.Empty;

    [Params("int4-c056", "int4-c112", "fp32-c056", "fp32-c112")]
    public string ModelVariant { get; set; } = "int4-c056";

    [GlobalSetup]
    public void GlobalSetup()
    {
        using var probe = CreateSession();
        var repoRoot = FindRepoRoot();
        var audioPath = Path.Combine(repoRoot, "Test-Audio", "sample-0.mp3");
        var samples = AudioUtils.LoadFile(audioPath, probe.SampleRate);
        _audioDurationSeconds = (double)samples.Length / probe.SampleRate;
        _chunks = SplitIntoChunks(samples, probe.ChunkSamples);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _session = CreateSession();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _session?.Dispose();
        _session = null;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        var resultsDirectory = Path.Combine(FindRepoRoot(), "build", "benchmark-results");
        Directory.CreateDirectory(resultsDirectory);
        var resultPath = Path.Combine(resultsDirectory, $"{ModelVariant}.txt");
        File.WriteAllText(
            resultPath,
            $"model_variant={ModelVariant}{Environment.NewLine}" +
            $"audio_duration_seconds={_audioDurationSeconds:F6}{Environment.NewLine}" +
            $"elapsed_seconds={_lastElapsedSeconds:F6}{Environment.NewLine}" +
            $"rtf={_lastRtf:F6}{Environment.NewLine}" +
            $"transcript={_lastTranscript}{Environment.NewLine}");
    }

    [Benchmark]
    public double TranscribeSampleRtf()
    {
        ArgumentNullException.ThrowIfNull(_session);
        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var part = _session.ProcessAudio(chunk);
            if (!string.IsNullOrEmpty(part))
                text.Append(part);
        }

        var final = _session.Flush();
        if (!string.IsNullOrEmpty(final))
            text.Append(final);
        stopwatch.Stop();
        _lastElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        _lastRtf = _lastElapsedSeconds / _audioDurationSeconds;
        _lastTranscript = text.ToString();
        return _lastRtf;
    }

    private NemotronRnntRecognizer CreateSession()
    {
        var modelPath = Path.Combine(FindRepoRoot(), "models", "asr", $"nemotron-3.5-asr-streaming-0.6b-onnx-{ModelVariant}-cpu");
        return new NemotronRnntRecognizer(modelPath, "cpu", null, useVad: false);
    }

    private static float[][] SplitIntoChunks(float[] samples, int chunkSize)
    {
        if (samples.Length == 0)
            return[new float[chunkSize]];
        var chunkCount = (samples.Length + chunkSize - 1) / chunkSize;
        var chunks = new float[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = new float[chunkSize];
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, samples.Length - offset);
            Array.Copy(samples, offset, chunk, 0, length);
            chunks[i] = chunk;
        }

        return chunks;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "models", "asr")) && Directory.Exists(Path.Combine(dir.FullName, "Test-Audio")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing models/asr/ and Test-Audio was not found.");
    }
}
