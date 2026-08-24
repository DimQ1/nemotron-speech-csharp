using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceType.Uno.Services;

/// <summary>
/// Downloads the recommended model without blocking the UNO UI thread.
/// </summary>
public sealed class ModelDownloadService : IDisposable
{
    public const string RecommendedRepoId =
        "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu";

    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _downloadCts;

    public ModelDownloadService()
    {
        // Accept-Encoding: gzip, deflate, br; the handler transparently decompresses.
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoiceType.Uno", "1.0"));
    }

    public bool IsDownloading { get; private set; }

    public event Action<ModelDownloadProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;

    public async Task<string> DownloadRecommendedAsync(string targetRoot, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
            throw new InvalidOperationException("A model download is already in progress.");

        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsDownloading = true;
        var subfolder = RecommendedRepoId[(RecommendedRepoId.LastIndexOf('/') + 1)..];
        var modelRoot = Path.GetFullPath(Path.Combine(targetRoot, subfolder));

        try
        {
            Directory.CreateDirectory(modelRoot);
            StatusChanged?.Invoke($"Fetching {RecommendedRepoId}...");
            var files = await FetchFilesAsync(RecommendedRepoId, _downloadCts.Token).ConfigureAwait(false);
            if (files.Count == 0)
                throw new InvalidOperationException("The model repository did not contain any files.");

            var totalBytes = files.Sum(file => file.SizeBytes);
            long downloadedBytes = 0;
            for (var index = 0; index < files.Count; index++)
            {
                _downloadCts.Token.ThrowIfCancellationRequested();
                var file = files[index];
                StatusChanged?.Invoke($"Downloading {file.RelativePath}...");
                var destination = GetSafeDestination(modelRoot, file.RelativePath);
                var fileBytes = await DownloadFileAsync(
                    RecommendedRepoId,
                    file,
                    destination,
                    downloadedBytes,
                    totalBytes,
                    index,
                    files.Count,
                    _downloadCts.Token).ConfigureAwait(false);
                downloadedBytes += fileBytes;
            }

            ProgressChanged?.Invoke(new ModelDownloadProgress(
                100,
                string.Empty,
                files.Count,
                files.Count,
                downloadedBytes,
                totalBytes));
            StatusChanged?.Invoke("Model download complete.");
            return modelRoot;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    public void Cancel() => _downloadCts?.Cancel();

    /// <summary>
    /// Downloads the LiteRT-LM translation model (single .litertlm file, ~2.6 GB)
    /// for the in-process native translation backend. Returns the model file path.
    /// </summary>
    public async Task<string> DownloadTranslationModelAsync(CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
            throw new InvalidOperationException("A model download is already in progress.");

        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsDownloading = true;

        try
        {
            var destination = TranslationModelInfo.LocalModelPath;
            StatusChanged?.Invoke($"Fetching {TranslationModelInfo.RepoId}...");

            var files = await FetchFilesAsync(TranslationModelInfo.RepoId, _downloadCts.Token).ConfigureAwait(false);
            var modelFile = files.FirstOrDefault(f =>
                string.Equals(f.RelativePath, TranslationModelInfo.FileName, StringComparison.Ordinal));
            if (modelFile.RelativePath.Length == 0)
                throw new InvalidOperationException(
                    $"{TranslationModelInfo.FileName} was not found in {TranslationModelInfo.RepoId}.");

            await DownloadFileAsync(
                TranslationModelInfo.RepoId,
                modelFile,
                destination,
                downloadedBytes: 0,
                totalBytes: modelFile.SizeBytes,
                fileIndex: 0,
                totalFiles: 1,
                _downloadCts.Token).ConfigureAwait(false);

            StatusChanged?.Invoke("Translation model download complete.");
            return destination;
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    public void Dispose()
    {
        Cancel();
        _httpClient.Dispose();
        _downloadCts?.Dispose();
    }

    private async Task<List<RemoteFile>> FetchFilesAsync(string repoId, CancellationToken cancellationToken)
    {
        var endpoint = $"https://huggingface.co/api/models/{repoId}";
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("siblings", out var siblings))
            return [];

        return siblings.EnumerateArray()
            .Select(file => new RemoteFile(
                file.GetProperty("rfilename").GetString() ?? string.Empty,
                file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : 0))
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath)
                && !file.RelativePath.StartsWith(".", StringComparison.Ordinal))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<long> DownloadFileAsync(
        string repoId,
        RemoteFile file,
        string destination,
        long downloadedBytes,
        long totalBytes,
        int fileIndex,
        int totalFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryPath = destination + ".part";
        var endpoint = $"https://huggingface.co/{repoId}/resolve/main/{Uri.EscapeDataString(file.RelativePath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long fileBytes;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            fileBytes = 0;
            var stopwatch = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                fileBytes += read;
                if (stopwatch.ElapsedMilliseconds >= 100)
                {
                    PublishProgress(file.RelativePath, fileIndex, totalFiles, downloadedBytes + fileBytes, totalBytes);
                    stopwatch.Restart();
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        downloadedBytes += fileBytes;
        File.Move(temporaryPath, destination, overwrite: true);
        PublishProgress(file.RelativePath, fileIndex + 1, totalFiles, downloadedBytes, totalBytes);
        return fileBytes;
    }

    private void PublishProgress(string file, int completedFiles, int totalFiles, long downloadedBytes, long totalBytes)
    {
        var progress = totalBytes > 0
            ? downloadedBytes * 100d / totalBytes
            : completedFiles * 100d / totalFiles;
        ProgressChanged?.Invoke(new ModelDownloadProgress(
            progress,
            file,
            completedFiles,
            totalFiles,
            downloadedBytes,
            totalBytes));
    }

    private static string GetSafeDestination(string modelRoot, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(modelRoot, relativePath));
        var rootWithSeparator = modelRoot.EndsWith(Path.DirectorySeparatorChar)
            ? modelRoot
            : modelRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe model file path: {relativePath}");

        return destination;
    }

    private readonly record struct RemoteFile(string RelativePath, long SizeBytes);
}

public readonly record struct ModelDownloadProgress(
    double OverallProgress,
    string CurrentFile,
    int DownloadedFiles,
    int TotalFiles,
    long DownloadedBytes,
    long TotalBytes);
