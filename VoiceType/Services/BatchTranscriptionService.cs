using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NemotronSpeech;
using SpeechLib;
using SpeechLib.Models;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Runs batch audio file transcription with optional speaker diarization.
/// Model loaded ONCE via <see cref="SharedModelHost"/>, per-job sessions share it.
/// </summary>
public sealed class BatchTranscriptionService : IDisposable
{
    private readonly SemaphoreSlim _parallelGate;
    private readonly SemaphoreSlim _diarizationGate = new(1, 1);
    private readonly string _outputDir;
    private readonly string _exportFormat;
    private readonly bool _wordTimestamps;
    private readonly bool _enableDiarization;
    private readonly string? _diarizationModelPath;
    private CancellationTokenSource? _cts;
    private SharedModelHost? _modelHost;
    private SortformerDiarizationService? _diarizationEngine;
    private bool _isDisposed;
    private int _totalJobs;
    private int _completedJobs;

    public BatchTranscriptionService(
        string asrModelPath, string executionProvider, string? langId, bool useVad,
        bool enableDiarization, string? diarizationModelPath,
        string outputDir, string exportFormat, int parallelism = 2)
    {
        _enableDiarization = enableDiarization;
        _diarizationModelPath = diarizationModelPath;
        _outputDir = outputDir;
        _exportFormat = exportFormat;
        _wordTimestamps = true;
        _parallelGate = new SemaphoreSlim(Math.Max(1, parallelism));
        _modelHost = new SharedModelHost(asrModelPath, executionProvider, langId, useVad);
    }

    public async Task ProcessAllAsync(
        IReadOnlyList<AudioFileJob> jobs,
        IProgress<(AudioFileJob? job, int totalPct)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (jobs.Count == 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _totalJobs = jobs.Count;
        _completedJobs = 0;

        // Offload init to thread pool
        await Task.Run(() =>
        {
            foreach (var job in jobs)
            {
                if (token.IsCancellationRequested) break;
                job.Status = "Queued";
                job.ProgressPercent = 0;
                TryScanDuration(job);
            }

            if (_enableDiarization && !string.IsNullOrEmpty(_diarizationModelPath))
            {
                try { _diarizationEngine = new SortformerDiarizationService(_diarizationModelPath); }
                catch (Exception ex) { Console.Error.WriteLine($"[Batch] Diarization: {ex.Message}"); }
            }
        }, token).ConfigureAwait(false);

        if (token.IsCancellationRequested) return;
        ReportOverall(progress, -1);

        var tasks = jobs.Select(job => ProcessOneAsync(job, progress, token));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void Cancel() => _cts?.Cancel();

    private async Task ProcessOneAsync(
        AudioFileJob job, IProgress<(AudioFileJob? job, int totalPct)>? progress, CancellationToken token)
    {
        await _parallelGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (token.IsCancellationRequested || _modelHost is null) return;

            job.Status = "Processing";
            job.ProgressPercent = 5;
            progress?.Report((job, -1));

            using var recognizer = _modelHost.CreateSession();
            List<DiarizationSegment>? segments = null;

            if (_diarizationEngine is not null)
            {
                await _diarizationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var audio = SpeechLib.Audio.AudioUtils.LoadFile(job.FilePath, recognizer.SampleRate);
                    segments = _diarizationEngine.Diarize(audio);
                }
                finally { _diarizationGate.Release(); }
            }

            job.ProgressPercent = 30;
            progress?.Report((job, -1));

            var text = Transcriber.RunFile(job.FilePath, recognizer,
                wordTimestamps: _wordTimestamps, out var wordTimings, diarization: null);

            job.ProgressPercent = 80;
            progress?.Report((job, -1));

            job.PlainText = text;
            job.WordTimings = wordTimings;

            if (segments is { Count: > 0 } && wordTimings.Count > 0)
            {
                job.SpeakerUtterances = DiarizationMergeService.Merge(wordTimings, segments);
                job.DiarizedText = string.Join("\n",
                    job.SpeakerUtterances.Select(u =>
                        $"[{DiarizedUtterance.FormatTimestamp(u.StartSeconds)}] {u.SpeakerId}: {u.Text}"));
            }

            SubtitleExporter.Export(job, _outputDir, _exportFormat);

            job.Status = "Done";
            job.ProgressPercent = 100;
            Interlocked.Increment(ref _completedJobs);
            int pct = _completedJobs * 100 / _totalJobs;
            progress?.Report((job, pct));
        }
        catch (OperationCanceledException)
        {
            job.Status = "Queued";
        }
        catch (Exception ex)
        {
            job.Status = "Error";
            job.ErrorMessage = ex.Message;
            Interlocked.Increment(ref _completedJobs);
            progress?.Report((job, _completedJobs * 100 / _totalJobs));
        }
        finally { _parallelGate.Release(); }
    }

    private static void ReportOverall(IProgress<(AudioFileJob? job, int totalPct)>? progress, int pct)
        => progress?.Report((null, pct));

    private static void TryScanDuration(AudioFileJob job)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(job.FilePath);
            job.DurationSeconds = reader.TotalTime.TotalSeconds;
            job.DurationDisplay = SubtitleExporter.FormatDuration(job.DurationSeconds);
        }
        catch { job.DurationDisplay = "?"; }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts?.Cancel(); _cts?.Dispose();
        _diarizationEngine?.Dispose();
        _modelHost?.Dispose();
        _parallelGate.Dispose();
        _diarizationGate.Dispose();
    }
}
