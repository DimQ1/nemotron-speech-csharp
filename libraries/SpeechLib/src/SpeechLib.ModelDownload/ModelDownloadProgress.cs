namespace SpeechLib.ModelDownload;

/// <summary>Aggregated download progress reported by a model downloader.</summary>
public readonly record struct ModelDownloadProgress(
    double OverallProgress,
    string CurrentFile,
    int DownloadedFiles,
    int TotalFiles,
    long DownloadedBytes,
    long TotalBytes);
