// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.ML.OnnxRuntime;
using NemotronSpeech;
using SpeechLib;
using SpeechLib.Decorators;
using SpeechLib.LiteRT;
using SpeechLib.LiteRT.Native;
using SpeechLib.Models;
using SpeechLib.Translation;

namespace NemotronSpeech;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
            {
                Console.WriteLine(AppOptions.Usage);
                return 0;
            }

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

            // Wrap with decorators for metrics and logging
            using ITextTranslator? translator = CreateTranslator(opts);
            LiveTranslationCoordinator? coordinator = translator is not null
                ? new LiveTranslationCoordinator(translator, opts.TargetLanguage!)
                : null;

            IStreamingSpeechRecognizer recognizer = session;
            recognizer = new MetricsRecognizerDecorator(recognizer, "ModelSession");
            recognizer = new LoggingRecognizerDecorator(
                recognizer,
                coordinator is not null ? coordinator.LogRecognized : null);

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

                if (translator is not null && coordinator is not null)
                {
                    try
                    {
                        Transcriber.RunLive(source, label, recognizer, coordinator.OnTextDelta);
                    }
                    finally
                    {
                        await coordinator.FlushAsync();
                    }
                }
                else
                {
                    Transcriber.RunLive(source, label, recognizer);
                }
            }
            else
            {
                var transcript = Transcriber.RunFile(opts.AudioFile!, recognizer, opts.WordTimestamps, out _);

                if (translator is not null && !string.IsNullOrWhiteSpace(transcript))
                    await StreamTranslationAsync(translator, transcript, opts.TargetLanguage!);
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(AppOptions.Usage);
            return 1;
        }
    }

    private static ITextTranslator? CreateTranslator(AppOptions opts)
    {
        if (!opts.TranslationEnabled)
            return null;

        if (string.Equals(opts.TranslateBackend, "native", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(opts.LiteRtModelPath))
                throw new ArgumentException("--litert-model-path is required when --translate-backend=native.");

            Console.WriteLine($"  Translate: {opts.TargetLanguage} via native LiteRT-LM ({opts.LiteRtBackend}, model={opts.LiteRtModelPath})");
            return new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
            {
                ModelPath = opts.LiteRtModelPath,
                Backend = opts.LiteRtBackend,
                NumThreads = opts.LiteRtNumThreads,
            });
        }

        Console.WriteLine($"  Translate: {opts.TargetLanguage} via {opts.TranslateUrl} (model={opts.TranslateModel})");
        return new LiteRTLmTranslator(new LiteRTLmOptions
        {
            BaseUrl = opts.TranslateUrl,
            Model = opts.TranslateModel,
        });
    }

    private static async Task StreamTranslationAsync(ITextTranslator translator, string text, string targetLang)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"  Translation ({targetLang}):");

        try
        {
            await foreach (var token in translator.TranslateStreamAsync(text, targetLang))
                Console.Write(token);

            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [translate] {ex.Message}");
        }

        Console.WriteLine(new string('-', 60));
    }
}

/// <summary>
/// Buffers live transcription deltas, splits off complete sentences, and streams
/// each sentence's translation to the console in parallel with ASR decoding.
/// </summary>
internal sealed class LiveTranslationCoordinator
{
    private readonly ITextTranslator _translator;
    private readonly string _targetLang;
    private readonly StringBuilder _buffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly List<Task> _inflight = new();
    private int _consumed;

    public LiveTranslationCoordinator(ITextTranslator translator, string targetLang)
    {
        _translator = translator;
        _targetLang = targetLang;
    }

    public void OnTextDelta(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        _buffer.Append(delta);
        var sentences = SentenceSplitter.ExtractCompleteSentences(_buffer, ref _consumed);
        foreach (var sentence in sentences)
        {
            var task = TranslateSentenceAsync(sentence);
            lock (_inflight)
                _inflight.Add(task);
        }
    }

    /// <summary>Serialized write of a recognized-text log line (shares the translation write lock).</summary>
    public void LogRecognized(string message)
    {
        _writeLock.Wait();
        try
        {
            Console.WriteLine($"[Recognizer] {message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FlushAsync()
    {
        var tail = _buffer.ToString(_consumed, _buffer.Length - _consumed).Trim();
        if (tail.Length > 0)
        {
            var task = TranslateSentenceAsync(tail);
            lock (_inflight)
                _inflight.Add(task);
        }

        Task[] pending;
        lock (_inflight)
            pending = _inflight.ToArray();

        await Task.WhenAll(pending);
    }

    private async Task TranslateSentenceAsync(string sentence)
    {
        await _writeLock.WaitAsync();
        try
        {
            Console.WriteLine();
            Console.WriteLine($"  [{_targetLang}] ");
            try
            {
                await foreach (var token in _translator.TranslateStreamAsync(sentence, _targetLang))
                    Console.Write(token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [translate] {ex.Message}");
            }
            Console.WriteLine();
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

