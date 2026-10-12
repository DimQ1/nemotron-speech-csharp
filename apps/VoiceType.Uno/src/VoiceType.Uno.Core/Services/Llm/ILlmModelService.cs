namespace VoiceType.Uno.Services;

public enum LlmModelState { NotInstalled, Downloading, Installed, Active }

public interface ILlmModelService
{
    LlmModelInfo Active { get; }                                  // the model the shared host currently uses
    string ComputeBackend { get; }                                // "cpu" | "gpu"
    LlmModelState StateOf(LlmModelInfo model);
    double? DownloadPercent(LlmModelInfo model);                  // null when not downloading
    event Action? Changed;                                        // any state/active/backend change (any thread)
    void StartDownload(LlmModelInfo model);                       // enqueue in the shared ModelDownloadManager; becomes Installed
    void CancelDownload(LlmModelInfo model);
    Task SelectAsync(LlmModelInfo model, CancellationToken cancellationToken = default);   // switch the shared engine to it (reload), persist in settings
    Task SetComputeBackendAsync(string backend, CancellationToken cancellationToken = default);   // "cpu"|"gpu"; reload, persist
    void Delete(LlmModelInfo model);                              // not the active one
}
