using SpeechLib.Models;

namespace NemotronSpeech;

/// <summary>Parsed command-line options.</summary>
public sealed record AppOptions
{
    public string ModelPath { get; init; } = "";
    public string? AudioFile { get; init; }
    public string ExecutionProvider { get; init; } = "follow_config";
    public string LanguageArg { get; init; } = "";
    public bool UseVad { get; init; }
    public CaptureMode Mode { get; init; } = CaptureMode.File;

    public bool IsLive => Mode != CaptureMode.File;

    public static AppOptions Parse(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Usage: NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>]");

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
                case "--use_vad" when i + 1 < args.Length && args[i + 1] == "true":
                    opts = opts with { UseVad = true }; i++; break;
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
