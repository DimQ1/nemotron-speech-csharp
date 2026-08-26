namespace VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Services;

public interface IModelDownloaderService : IDisposable
{
    event Action<DownloadProgress>? ProgressChanged;
    event Action<string>? StatusChanged;
    event Action<bool, string>? Completed;
    bool IsDownloading { get; }
    Task<List<HfFolder>> FetchRepoFolders(string repoId, CancellationToken ct = default);
    Task DownloadFromHuggingFace(string repoId, List<HfFolder> folders, string targetRoot);
    Task DownloadModelRepo(string repoId, string subfolder, string targetRoot, string? QuantizationFolder = null, CancellationToken ct = default);
    /// <summary>
    /// Downloads a single file from a Hugging Face repo (resolve URL) directly to
    /// <paramref name="destPath"/>. Raises <see cref="ProgressChanged"/>/<see cref="StatusChanged"/>/
    /// <see cref="Completed"/> events, mirroring the multi-file download path.
    /// </summary>
    Task DownloadHuggingFaceFile(string repoId, string fileName, string destPath, CancellationToken ct = default);
    void Cancel();
}
