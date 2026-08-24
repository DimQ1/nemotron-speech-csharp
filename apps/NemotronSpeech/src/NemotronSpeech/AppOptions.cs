using SpeechLib.Models;

namespace NemotronSpeech;

/// <summary>Parsed command-line options.</summary>
public sealed record AppOptions
{
    public const string Usage =
        "Usage: NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] " +
        "[--word-timestamps] [--translate <lang>] " +
        "[--translate-backend <http|native>] [--translate-url <url>] [--translate-model <name>] " +
        "[--litert-model-path <path>] [--litert-backend <cpu|gpu>] [--litert-num-threads <n>]";

    public string ModelPath { get; init; } = "";
    public string? AudioFile { get; init; }
    public string ExecutionProvider { get; init; } = "follow_config";
    public string LanguageArg { get; init; } = "";
    public bool UseVad { get; init; }
    public bool WordTimestamps { get; init; }
    public CaptureMode Mode { get; init; } = CaptureMode.File;

    /// <summary>Target language for on-the-fly translation (e.g. "bg", "de", "ru").</summary>
    public string? TargetLanguage { get; init; }

    /// <summary>Base URL of the LiteRT-LM translation server.</summary>
    public string TranslateUrl { get; init; } = "http://localhost:9379";

    /// <summary>Model name accepted by the translation server.</summary>
    public string TranslateModel { get; init; } = "gemma-4-E2B-it";

    /// <summary>Translation backend: <c>"http"</c> (default) or <c>"native"</c>.</summary>
    public string TranslateBackend { get; init; } = "http";

    /// <summary>Path to a <c>.litertlm</c> model for the native backend.</summary>
    public string? LiteRtModelPath { get; init; }

    /// <summary>Compute backend for the native LiteRT-LM engine: <c>"cpu"</c> or <c>"gpu"</c>.</summary>
    public string LiteRtBackend { get; init; } = "cpu";

    /// <summary>CPU thread count for the native LiteRT-LM engine (0 = library default).</summary>
    public int LiteRtNumThreads { get; init; }

    public bool IsLive => Mode != CaptureMode.File;

    public bool TranslationEnabled => !string.IsNullOrWhiteSpace(TargetLanguage);

    public static AppOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException(Usage);

        var opts = new AppOptions { ModelPath = args[0] };
        string? audioFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mic":  opts = opts with { Mode = CaptureMode.Mic }; break;
                case "--loopback": opts = opts with { Mode = CaptureMode.Loopback }; break;
                case "--mix":  opts = opts with { Mode = CaptureMode.Mix }; break;
                case "--language" or "-l" when i + 1 < args.Length:
                    opts = opts with { LanguageArg = args[++i] }; break;
                case "--use_vad":
                    if (i + 1 >= args.Length || !bool.TryParse(args[i + 1], out var useVad))
                        throw new ArgumentException("--use_vad expects true or false.");

                    opts = opts with { UseVad = useVad };
                    i++;
                    break;
                case "--word-timestamps":
                    opts = opts with { WordTimestamps = true }; break;
                case "--translate" or "-t" when i + 1 < args.Length:
                    opts = opts with { TargetLanguage = args[++i] }; break;
                case "--translate-url" when i + 1 < args.Length:
                    opts = opts with { TranslateUrl = args[++i] }; break;
                case "--translate-model" when i + 1 < args.Length:
                    opts = opts with { TranslateModel = args[++i] }; break;
                case "--translate-backend" when i + 1 < args.Length:
                    opts = opts with { TranslateBackend = args[++i] }; break;
                case "--litert-model-path" when i + 1 < args.Length:
                    opts = opts with { LiteRtModelPath = args[++i] }; break;
                case "--litert-backend" when i + 1 < args.Length:
                    opts = opts with { LiteRtBackend = args[++i] }; break;
                case "--litert-num-threads" when i + 1 < args.Length:
                    if (!int.TryParse(args[i + 1], out var numThreads) || numThreads < 0)
                        throw new ArgumentException("--litert-num-threads expects a non-negative integer.");

                    opts = opts with { LiteRtNumThreads = numThreads };
                    i++;
                    break;
                default:
                    // Recognise known EP names first, then fall back to audio file
                    if (args[i] is "cpu" or "cuda" or "dml" or "tensorrt" or "NvTensorRtRtx" or "follow_config")
                        opts = opts with { ExecutionProvider = args[i] };
                    else if (!args[i].StartsWith("--") && audioFile == null && args[i] != opts.ModelPath)
                        audioFile = args[i];
                    else if (args[i] != opts.ModelPath)
                        opts = opts with { ExecutionProvider = args[i] };
                    break;
            }
        }

        opts = opts with { AudioFile = audioFile };

        if (!opts.IsLive && string.IsNullOrEmpty(opts.AudioFile))
            throw new ArgumentException("Provide an audio file or use --mic / --loopback / --mix.");

        return opts;
    }
}
