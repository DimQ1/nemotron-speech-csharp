using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SpeechLib.ModelDownload;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>A user-facing message about a finished download.</summary>
public sealed record DownloadNotice(DownloadJob Job, bool Success, string Title, string Message);

/// <summary>
/// Application-wide download center: one <see cref="ModelDownloadManager"/> for the
/// whole app (downloads keep running when the manager window is closed), request
/// builders for catalog and translation models, and a notification for every model
/// that finishes — a Windows toast, an in-app notice, and a
/// <see cref="ModelDownloadedMessage"/> for recognition models.
/// </summary>
public sealed class DownloadCenter : IDisposable
{
    public const string TranslationKey = "translation-model";

    private readonly ISystemTelemetryAccessor? _telemetry;
    private readonly List<DownloadNotice> _notices = new();
    private readonly object _noticeGate = new();
    private bool _toastsRegistered;
    private bool _toastsUnavailable;

    public DownloadCenter(ModelDownloadManager manager, ISystemTelemetryAccessor? telemetry = null)
    {
        Manager = manager;
        _telemetry = telemetry;
        Manager.JobFinished += OnJobFinished;
    }

    public ModelDownloadManager Manager { get; }

    /// <summary>Raised (on a worker thread) for every finished download.</summary>
    public event Action<DownloadNotice>? NoticeRaised;

    /// <summary>Notices since start, newest last.</summary>
    public IReadOnlyList<DownloadNotice> Notices
    {
        get { lock (_noticeGate) return _notices.ToList(); }
    }

    public DownloadJob EnqueueCatalogModel(ModelDescriptor model, string modelsRoot)
    {
        var request = ModelDownloadRequest.ForCatalogModel(model, modelsRoot) with
        {
            AfterDownload = dir => Qwen3StreamingProfile.TryConfigure(model.RepoId, dir),
        };
        return Manager.Enqueue(request);
    }

    public DownloadJob EnqueueTranslationModel()
    {
        AppPaths.EnsureTranslationModelsDir();
        return Manager.Enqueue(new ModelDownloadRequest(
            TranslationKey,
            "Translation model · Gemma 4 E2B",
            TranslationModelInfo.RepoId,
            AppPaths.TranslationModelsDir)
        {
            SingleFile = TranslationModelInfo.FileName,
            Kind = ModelDownloadKind.Translation,
        });
    }

    /// <summary>True when the catalog model is already installed under <paramref name="modelsRoot"/>.</summary>
    public static bool IsInstalled(ModelDescriptor model, string modelsRoot) =>
        ModelFolderScanner.IsModelDirectory(Path.Combine(modelsRoot, model.SubfolderName));

    private void OnJobFinished(DownloadJob job)
    {
        if (job.State == DownloadJobState.Cancelled)
            return;

        var success = job.State == DownloadJobState.Completed;
        var notice = success
            ? new DownloadNotice(job, true, "Download complete", $"{job.Title} is ready to use.")
            : new DownloadNotice(job, false, "Download failed", $"{job.Title}: {job.Error}");

        lock (_noticeGate)
            _notices.Add(notice);

        try
        {
            if (success)
                _telemetry?.Telemetry?.LogInfo("Download", $"{job.Title} downloaded to {job.ResultPath}");
            else
                _telemetry?.Telemetry?.LogWarning("Download", $"{job.Title} failed: {job.Error}");
        }
        catch
        {
            // Telemetry must never break a download notification.
        }

        ShowToast(notice);
        NoticeRaised?.Invoke(notice);

        if (success && job.Request.Kind == ModelDownloadKind.Recognition && job.ResultPath is not null)
        {
            WeakReferenceMessenger.Default.Send(new ModelDownloadedMessage(
                Path.GetDirectoryName(job.ResultPath) ?? job.ResultPath,
                job.ResultPath));
        }
    }

    /// <summary>
    /// Windows notification for a finished download. Best effort: when app
    /// notifications are unavailable (not registered, disabled by policy) the in-app
    /// notice still appears.
    /// </summary>
    private void ShowToast(DownloadNotice notice)
    {
        if (_toastsUnavailable)
            return;

        try
        {
            if (!_toastsRegistered)
            {
                AppNotificationManager.Default.Register();
                _toastsRegistered = true;
            }

            var toast = new AppNotificationBuilder()
                .AddText(notice.Title)
                .AddText(notice.Message)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch
        {
            _toastsUnavailable = true;
        }
    }

    public void Dispose()
    {
        Manager.JobFinished -= OnJobFinished;
        if (_toastsRegistered)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
        }
        Manager.Dispose();
    }
}

/// <summary>Resolves the app telemetry lazily (it is created after DI in App).</summary>
public interface ISystemTelemetryAccessor
{
    Interfaces.ISystemTelemetry? Telemetry { get; }
}

internal sealed class AppTelemetryAccessor : ISystemTelemetryAccessor
{
    public Interfaces.ISystemTelemetry? Telemetry => App.Telemetry;
}

/// <summary>Writes the block-streaming manifest the Qwen3 streaming recognizer needs after download.</summary>
internal static class Qwen3StreamingProfile
{
    public static void TryConfigure(string repoId, string modelDir)
    {
        if (!string.Equals(repoId, "andrewleech/qwen3-asr-1.7b-onnx", StringComparison.OrdinalIgnoreCase)
            || !Qwen3ModelDetector.IsQwen3AsrModel(modelDir))
            return;

        var encoderFile = File.Exists(Path.Combine(modelDir, "encoder_stream.onnx"))
            ? "encoder_stream.onnx"
            : "encoder.int4.onnx";
        var manifest = """
        {
          "model_type": "qwen3_asr_onnx_streaming",
          "base_model_dir": ".",
          "encoder_file": "__ENCODER_FILE__",
          "block_seconds": 2.0,
          "chunk_seconds": 2.0,
          "window_seconds": 16.0,
          "streaming_mode": "block_encoder",
          "skip_silent_blocks": true,
          "decode_every_blocks": 1,
          "emit_first_block": false
        }
        """.Replace("__ENCODER_FILE__", encoderFile, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(modelDir, "streaming_config.json"), manifest + Environment.NewLine);
    }
}
