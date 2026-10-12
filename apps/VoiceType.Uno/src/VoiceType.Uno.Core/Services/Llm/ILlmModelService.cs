namespace VoiceType.Uno.Services;

public enum LlmModelState { NotInstalled, Downloading, Installed, Active }

public interface ILlmModelService
{
    LlmModelInfo Active { get; }
    string ComputeBackend { get; }                                // "cpu" | "gpu"
    LlmModelState StateOf(LlmModelInfo model);
    double? DownloadPercent(LlmModelInfo model);
    event Action? Changed;                                        // any thread
    void StartDownload(LlmModelInfo model);
    void CancelDownload(LlmModelInfo model);
    Task SelectAsync(LlmModelInfo model, CancellationToken cancellationToken = default);
    Task SetComputeBackendAsync(string backend, CancellationToken cancellationToken = default);
    void Delete(LlmModelInfo model);                              // not the active one
}
