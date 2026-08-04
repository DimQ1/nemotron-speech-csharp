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
    Task DownloadModelRepo(string repoId, string subfolder, string targetRoot, CancellationToken ct = default);
    void Cancel();
}
