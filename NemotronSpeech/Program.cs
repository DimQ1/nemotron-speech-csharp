// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.ML.OnnxRuntime;
using NemotronSpeech;
using SpeechLib;
using SpeechLib.Models;

try
{
    var opts = AppOptions.Parse(args);

    // Show available providers and active EP
    var availableProviders = OrtEnv.Instance().GetAvailableProviders();
    Console.WriteLine($"  EP requested: {opts.ExecutionProvider}");
    Console.WriteLine($"  Available:    {string.Join(", ", availableProviders)}");

    var langId = LanguageMapper.Resolve(opts.LanguageArg);
    if (langId is not null)
        Console.WriteLine($"  Language: {opts.LanguageArg} -> lang_id={langId}");

    using var session = new ModelSession(opts.ModelPath, opts.ExecutionProvider, langId, opts.UseVad);
    if (session.IsSingleLanguage)
        Console.WriteLine("  Model: single-language (no lang_id needed)");
    Console.WriteLine("  Use VAD: " + session.VadStatus);

    IDiarizationService? diarization = null;
    if (opts.DiarizationModel is not null)
    {
        Console.WriteLine($"  Diarization: {opts.DiarizationModel}");
        diarization = new SortformerDiarizationService(opts.DiarizationModel);
    }
    using var diarizationDisposable = diarization;

    if (opts.IsLive)
    {
        if (opts.WordTimestamps)
            Console.WriteLine("  Note: --word-timestamps is ignored in live mode (file mode only).");

        var source = Transcriber.CreateAudioSource(opts.Mode, session.SampleRate);

        var label = opts.Mode switch
        {
            CaptureMode.Mic => "Microphone",
            CaptureMode.Loopback => "System audio (loopback)",
            CaptureMode.Mix => "Microphone + System audio (mixed)",
            _ => ""
        };

        Transcriber.RunLive(source, label, session, diarization);
    }
    else
    {
        Transcriber.RunFile(opts.AudioFile!, session, opts.WordTimestamps, out _, diarization);
    }
}
catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Usage: NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>] [--word-timestamps] [--diarization <model_path>]");
}

