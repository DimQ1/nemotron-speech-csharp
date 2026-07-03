using CommonUtils;
using NemotronSpeech;
using SpeechLib;
using System;
using System.IO;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Runs offline transcription for existing audio files using the current VoiceType engine settings.
/// </summary>
public sealed class FileTranscriptionService
{
    public string Transcribe(string audioPath, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(audioPath);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("Audio file not found.", audioPath);

        var configuration = RecognitionService.CreateRecognizerConfiguration(settings);
        var searchOptions = new GeneratorParamsArgs
        {
            num_beams = configuration.NumBeams,
            do_sample = false,
            repetition_penalty = configuration.RepetitionPenalty
        };

        using var recognizer = new ModelSession(
            configuration.ModelPath,
            configuration.ExecutionProvider,
            configuration.LanguageId,
            configuration.UseVad,
            searchOptions);

        var raw = Transcriber.RunFile(audioPath, recognizer);
        var text = PostProcessingPipeline.Process(
            raw,
            settings.PostProcessingRules,
            settings.PostProcessingEnabled);

        SaveSession(audioPath, settings, text);
        return text;
    }

    private static void SaveSession(string audioPath, AppSettings settings, string text)
    {
        if (!settings.SaveSessions)
            return;

        var session = SessionManager.CreateSession(settings.Language, "Nemotron", "File");
        session.EndedAt = DateTime.Now;
        session.RecognizedText = text;
        session.AudioFilePath = audioPath;
        session.IsComplete = true;
        SessionManager.SaveSession(session);
    }
}
