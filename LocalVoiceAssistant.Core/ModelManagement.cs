using System.Net;
using System.Text.Json;

namespace LocalVoiceAssistant.Maui.Core;

public sealed record LvaModelDefinition(
    string RepoId,
    string DisplayName,
    string Description,
    long EstimatedSizeBytes);

public sealed record ModelDownloadProgress(
    string Status,
    string CurrentFile,
    double FileProgress,
    double OverallProgress,
    int CompletedFiles,
    int TotalFiles);

public sealed record ModelBootstrapResult(
    bool IsReady,
    string ModelPath,
    string RepoId,
    string Status,
    string? ErrorMessage = null);

public interface IModelBootstrapService : IDisposable
{
    Task<ModelBootstrapResult> EnsureModelAsync(
        string modelsRoot,
        LvaModelDefinition model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);

    bool IsModelReady(string modelPath);
}

public sealed class HuggingFaceModelBootstrapService : IModelBootstrapService
{
    public static readonly LvaModelDefinition RecommendedModel = new(
        "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu",
        "CPU (INT4, opset24, 0.56s)",
        "Быстрая локальная модель с низкой задержкой",
        749_000_000);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _baseUrl;

    public HuggingFaceModelBootstrapService(
        HttpClient? httpClient = null,
        string baseUrl = "https://huggingface.co")
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _disposeHttpClient = httpClient is null;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public bool IsModelReady(string modelPath)
    {
        if (!Directory.Exists(modelPath))
            return false;

        var configPath = Path.Combine(modelPath, "genai_config.json");
        return File.Exists(configPath) && new FileInfo(configPath).Length > 0;
    }

    public async Task<ModelBootstrapResult> EnsureModelAsync(
        string modelsRoot,
        LvaModelDefinition model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(model);

        var modelFolder = Path.Combine(modelsRoot, GetRepositoryName(model.RepoId));
        if (IsModelReady(modelFolder))
        {
            progress?.Report(new ModelDownloadProgress(
                "Модель уже загружена",
                string.Empty,
                1,
                1,
                1,
                1));
            return new ModelBootstrapResult(
                true,
                modelFolder,
                model.RepoId,
                "Локальная модель готова.");
        }

        Directory.CreateDirectory(modelsRoot);
        var files = await FetchRepositoryFilesAsync(model.RepoId, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
            throw new InvalidOperationException($"В репозитории Hugging Face «{model.RepoId}» не найдено файлов модели.");

        var downloadedFiles = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = GetSafeDestination(modelFolder, file.RelativePath);
            await DownloadFileAsync(
                model.RepoId,
                file,
                destination,
                downloadedFiles,
                files.Count,
                progress,
                cancellationToken).ConfigureAwait(false);
            downloadedFiles++;
        }

        if (!IsModelReady(modelFolder))
            throw new InvalidDataException(
                $"Загрузка завершилась, но {Path.Combine(modelFolder, "genai_config.json")} отсутствует или пуст.");

        progress?.Report(new ModelDownloadProgress(
            "Модель загружена и проверена",
            string.Empty,
            1,
            1,
            files.Count,
            files.Count));

        return new ModelBootstrapResult(
            true,
            modelFolder,
            model.RepoId,
            "Модель загружена и готова к запуску.");
    }

    private async Task<List<RepositoryFile>> FetchRepositoryFilesAsync(
        string repoId,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{_baseUrl}/api/models/{Uri.EscapeDataString(repoId).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, repoId).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("siblings", out var siblings))
            return [];

        var files = new List<RepositoryFile>();
        foreach (var sibling in siblings.EnumerateArray())
        {
            if (!sibling.TryGetProperty("rfilename", out var nameElement))
                continue;

            var relativePath = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith(".", StringComparison.Ordinal))
                continue;

            var size = sibling.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;
            files.Add(new RepositoryFile(relativePath, size));
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    private async Task DownloadFileAsync(
        string repoId,
        RepositoryFile file,
        string destination,
        int completedFiles,
        int totalFiles,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Не удалось определить каталог для файла {file.RelativePath}.");
        Directory.CreateDirectory(directory);

        var temporaryPath = destination + ".download";
        var endpoint = CreateResolveUrl(repoId, file.RelativePath);
        progress?.Report(new ModelDownloadProgress(
            $"Загрузка {completedFiles + 1}/{totalFiles}",
            file.RelativePath,
            0,
            CalculateOverallProgress(completedFiles, 0, totalFiles),
            completedFiles,
            totalFiles));

        try
        {
            using var response = await _httpClient.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, file.RelativePath).ConfigureAwait(false);

            var contentLength = response.Content.Headers.ContentLength.GetValueOrDefault(file.SizeBytes);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81_920,
                useAsync: true))
            {
                var buffer = new byte[81_920];
                long downloadedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloadedBytes += read;

                    var fileProgress = contentLength > 0
                        ? Math.Clamp((double)downloadedBytes / contentLength, 0, 1)
                        : 0;
                    progress?.Report(new ModelDownloadProgress(
                        $"Загрузка {completedFiles + 1}/{totalFiles}",
                        file.RelativePath,
                        fileProgress,
                        CalculateOverallProgress(completedFiles, fileProgress, totalFiles),
                        completedFiles,
                        totalFiles));
                }
            }

            File.Move(temporaryPath, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static double CalculateOverallProgress(int completedFiles, double fileProgress, int totalFiles) =>
        totalFiles <= 0
            ? 0
            : Math.Clamp((completedFiles + fileProgress) / totalFiles, 0, 1);

    private string CreateResolveUrl(string repoId, string relativePath)
    {
        var escapedPath = string.Join(
            '/',
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return $"{_baseUrl}/{repoId}/resolve/main/{escapedPath}";
    }

    private static string GetRepositoryName(string repoId)
    {
        var separator = repoId.LastIndexOf('/');
        var name = separator >= 0 ? repoId[(separator + 1)..] : repoId;
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Репозиторий модели должен иметь имя.", nameof(repoId));
        return name;
    }

    private static string GetSafeDestination(string modelFolder, string relativePath)
    {
        var root = Path.GetFullPath(modelFolder);
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(destination, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Недопустимый путь файла модели: {relativePath}");
        }

        return destination;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string resource)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(detail)
            ? response.ReasonPhrase
            : detail[..Math.Min(detail.Length, 300)];
        throw new HttpRequestException(
            $"Hugging Face отклонил запрос для «{resource}»: {(int)response.StatusCode} {message}",
            null,
            response.StatusCode);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            MaxConnectionsPerServer = 8
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "LocalVoiceAssistant/1.0 (SpeechLib)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }

    private sealed record RepositoryFile(string RelativePath, long SizeBytes);
}
