using System.Diagnostics;
using DiarizationTest.Models;
using NAudio.Wave;

namespace DiarizationTest;

/// <summary>
/// DiarizationTest — CLI for evaluating ONNX Sortformer diarization models.
/// 
/// Usage:
///   dotnet run -- --model fp32 --audio path/to/file.wav --provider dml
///   dotnet run -- --model int8 --mode batch --metrics --provider cpu
/// </summary>
class Program
{
    private const int SampleRate = 16000;
    private const int ChunkSeconds = 5;
    private const int NumSpeakers = 4;

    static async Task<int> Main(string[] args)
    {
        var cli = ParseArgs(args);

        if (cli.ShowHelp || string.IsNullOrEmpty(cli.Model))
        {
            PrintHelp();
            return 0;
        }

        // Resolve model path
        string modelPath = ResolveModelPath(cli.Model);
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"ERROR: Model not found: {modelPath}");
            Console.WriteLine("Run the Python conversion scripts first:");
            Console.WriteLine("  cd DiarizationConverter");
            Console.WriteLine("  python scripts/download_model.py");
            Console.WriteLine("  python scripts/export_fp32.py");
            Console.WriteLine("  python scripts/quantize_int8.py");
            Console.WriteLine("  python scripts/quantize_int4.py");
            return 1;
        }

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"Diarization Test — {cli.Model.ToUpper()} model");
        Console.WriteLine($"Model: {modelPath}");
        Console.WriteLine($"Mode:  {(cli.Mode == "batch" ? "batch" : "single")}");
        Console.WriteLine($"Provider: {cli.Provider.ToUpper()}");
        Console.WriteLine($"{new string('=', 60)}\n");

        if (cli.Mode == "single")
        {
            if (string.IsNullOrEmpty(cli.Audio))
            {
                Console.WriteLine("ERROR: --audio <path> required in single mode");
                return 1;
            }
            RunSingleFile(modelPath, cli.Audio, cli.Provider, cli.Metrics);
        }
        else
        {
            RunBatch(modelPath, cli.Dataset, cli.Provider, cli.Metrics);
        }

        return 0;
    }

    private static CliArgs ParseArgs(string[] args)
    {
        var cli = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" or "-m":
                    if (i + 1 < args.Length) cli.Model = args[++i];
                    break;
                case "--audio" or "-a":
                    if (i + 1 < args.Length) cli.Audio = args[++i];
                    break;
                case "--mode":
                    if (i + 1 < args.Length) cli.Mode = args[++i];
                    break;
                case "--dataset":
                    if (i + 1 < args.Length) cli.Dataset = args[++i];
                    break;
                case "--metrics":
                    cli.Metrics = true;
                    break;
                case "--provider" or "-p":
                    if (i + 1 < args.Length) cli.Provider = args[++i];
                    break;
                case "--help" or "-h":
                    cli.ShowHelp = true;
                    break;
            }
        }
        return cli;
    }

    private static string ResolveModelPath(string model)
    {
        string modelName = model.ToLower() switch
        {
            "fp32" => "sortformer_fp32",
            "int8" => "sortformer_int8",
            "int4" => "sortformer_int4",
            _ => model,
        };

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "models", modelName, "sortformer.onnx"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "models", modelName, "sortformer.onnx"),
            Path.Combine("models", modelName, "sortformer.onnx"),
        };

        foreach (var path in candidates)
        {
            var resolved = Path.GetFullPath(path);
            if (File.Exists(resolved))
                return resolved;
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static void RunSingleFile(string modelPath, string audioPath, string provider, bool printMetrics)
    {
        Console.WriteLine($"Processing: {audioPath}\n");

        float[] audio = LoadAudio(audioPath);
        Console.WriteLine($"  Audio: {audio.Length / (double)SampleRate:F1}s, {SampleRate}Hz mono");

        using var inference = new SortformerInference(modelPath, provider, SampleRate, NumSpeakers);
        var sw = Stopwatch.StartNew();
        var segments = inference.Diarize(audio);
        sw.Stop();

        Console.WriteLine($"\n  Done in {sw.Elapsed.TotalSeconds:F2}s ({provider.ToUpper()})");
        Console.WriteLine($"  Segments: {segments.Count}\n");

        foreach (var seg in segments)
        {
            Console.WriteLine($"  {seg.SpeakerId}: [{seg.StartSeconds:F2}s \u2013 {seg.EndSeconds:F2}s] " +
                              $"({seg.EndSeconds - seg.StartSeconds:F2}s)");
        }
    }

    private static void RunBatch(string modelPath, string datasetDir, string provider, bool printMetrics)
    {
        var audioDir = Path.Combine(datasetDir, "audio");
        var rttmDir = Path.Combine(datasetDir, "rttm");

        if (!Directory.Exists(audioDir))
        {
            Console.WriteLine($"ERROR: Dataset audio directory not found: {audioDir}");
            Console.WriteLine("Run: python scripts/download_dataset.py");
            return;
        }

        var audioFiles = Directory.GetFiles(audioDir, "*.wav")
            .OrderBy(f => f)
            .ToList();

        if (audioFiles.Count == 0)
        {
            Console.WriteLine("ERROR: No WAV files found in dataset.");
            return;
        }

        Console.WriteLine($"Dataset: {audioFiles.Count} files\n");

        using var inference = new SortformerInference(modelPath, provider, SampleRate, NumSpeakers);

        int successCount = 0;
        double sumDer = 0, sumRtf = 0;
        int totalSegments = 0;
        double totalAudioSec = 0;
        double totalProcSec = 0;

        foreach (var audioFile in audioFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(audioFile);
            Console.Write($"  {Path.GetFileName(audioFile)}... ");

            try
            {
                float[] audio = LoadAudio(audioFile);
                var sw = Stopwatch.StartNew();
                var segments = inference.Diarize(audio);
                sw.Stop();

                double rtf = sw.Elapsed.TotalSeconds / (audio.Length / (double)SampleRate);
                totalProcSec += sw.Elapsed.TotalSeconds;
                totalAudioSec += audio.Length / (double)SampleRate;
                totalSegments += segments.Count;

                if (printMetrics)
                {
                    var rttmPath = Path.Combine(rttmDir, $"{fileName}.rttm");
                    if (File.Exists(rttmPath))
                    {
                        var gt = DiarizationEvaluator.ParseRttm(rttmPath);
                        var der = DiarizationEvaluator.CalculateDer(segments, gt);
                        Console.WriteLine($"RTF={rtf:F3}, {der}");
                        sumDer += der.DerPercent;
                        sumRtf += rtf;
                        successCount++;
                    }
                    else
                    {
                        Console.WriteLine($"RTF={rtf:F3}, {segments.Count} segments (no RTTM)");
                    }
                }
                else
                {
                    Console.WriteLine($"RTF={rtf:F3}, {segments.Count} segments");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
            }
        }

        // Summary
        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine("Batch Summary");
        Console.WriteLine($"{new string('=', 60)}");
        Console.WriteLine($"  Files processed: {audioFiles.Count}");
        Console.WriteLine($"  Total audio:     {totalAudioSec:F1}s");
        Console.WriteLine($"  Total proc time: {totalProcSec:F1}s");

        if (successCount > 0)
        {
            Console.WriteLine($"  Average DER: {sumDer / successCount:F2}%");
            Console.WriteLine($"  Average RTF: {sumRtf / successCount:F3}");
        }
        Console.WriteLine($"  Total segments:  {totalSegments}");
    }

    /// <summary>
    /// Load audio file as float32 mono 16kHz PCM.
    /// </summary>
    private static float[] LoadAudio(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();

        if (ext == ".wav")
        {
            using var reader = new WaveFileReader(filePath);
            return ReadWaveToFloat(reader);
        }
        else if (ext == ".mp3")
        {
            using var reader = new Mp3FileReader(filePath);
            var pcmStream = WaveFormatConversionStream.CreatePcmStream(reader);
            return ReadWaveToFloat(pcmStream);
        }
        else
        {
            throw new NotSupportedException($"Audio format not supported: {ext}");
        }
    }

    private static float[] ReadWaveToFloat(WaveStream reader)
    {
        // Resample to 16kHz mono if needed
        var provider = reader.ToSampleProvider();

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            using var resampler = new MediaFoundationResampler(reader, SampleRate);
            provider = resampler.ToSampleProvider();
        }

        // Convert to mono if stereo
        if (provider.WaveFormat.Channels > 1)
        {
            provider = provider.ToMono();
        }

        var samples = new List<float>();
        var buffer = new float[SampleRate]; // 1 second buffer
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = new float[read];
            Array.Copy(buffer, chunk, read);
            samples.AddRange(chunk);
        }

        return samples.ToArray();
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
Sortformer Diarization ONNX Test Suite
=======================================

Usage:
  dotnet run -- --model <fp32|int8|int4> [options]

Options:
  -m, --model <fp32|int8|int4>   Model precision (required)
  -a, --audio <path>              Single audio file path
  -p, --provider <cpu|dml>         Execution provider (default: cpu)
      --mode <single|batch>       Processing mode (default: single)
      --dataset <path>            Dataset directory (default: ../dataset)
      --metrics                   Print DER metrics against RTTM ground truth
  -h, --help                     Show this help

Examples:
  dotnet run -- --model fp32 --audio test.wav --provider dml
  dotnet run -- --model int8 --mode batch --metrics --provider cpu
  dotnet run -- --model fp32 --mode batch --metrics --provider dml
");
    }

    private sealed class CliArgs
    {
        public string Model { get; set; } = "";
        public string? Audio { get; set; }
        public string Mode { get; set; } = "single";
        public string Dataset { get; set; } = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dataset");
        public string Provider { get; set; } = "cpu";
        public bool Metrics { get; set; }
        public bool ShowHelp { get; set; }
    }
}
