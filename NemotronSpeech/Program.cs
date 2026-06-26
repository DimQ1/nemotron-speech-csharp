// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.ML.OnnxRuntime;
using NemotronSpeech;

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
    Console.WriteLine("  Use VAD: " + session.VadStatus);

    if (opts.IsLive)
    {
        IAudioSource source = opts.Mode switch
        {
            CaptureMode.Mic => new MicAudioSource(),
            CaptureMode.Loopback => new LoopbackAudioSource(session.SampleRate),
            CaptureMode.Mix => new MixAudioSource(session.SampleRate),
            _ => throw new InvalidOperationException()
        };

        var label = opts.Mode switch
        {
            CaptureMode.Mic => "Microphone",
            CaptureMode.Loopback => "System audio (loopback)",
            CaptureMode.Mix => "Microphone + System audio (mixed)",
            _ => ""
        };

        Transcriber.RunLive(source, label, session);
    }
    else
    {
        Transcriber.RunFile(opts.AudioFile!, session);
    }
}
catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Usage: NemotronSpeech <model_path> <audio_file|--mic|--loopback|--mix> [ep] [--language <code>]");
}

