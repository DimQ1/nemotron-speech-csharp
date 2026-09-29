using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SpeechLib.ModelDownload;

/// <summary>A file in a Hugging Face repo and where it lands locally.</summary>
/// <param name="RepoPath">Path inside the repo, e.g. <c>int4/encoder-model.onnx</c>.</param>
/// <param name="LocalPath">Path relative to the model folder, e.g. <c>encoder-model.onnx</c>.</param>
/// <param name="SizeBytes">Size reported by the Hub (0 when unknown).</param>
public sealed record RemoteFile(string RepoPath, string LocalPath, long SizeBytes);

/// <summary>
/// Minimal Hugging Face Hub client: lists a repo's files with their sizes and
/// downloads single files with byte progress, resume and atomic replace.
/// Stateless and thread-safe, so several downloads can share one instance.
/// </summary>
public sealed class HuggingFaceClient
{
    private const string TempSuffix = ".download";
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HuggingFaceClient(HttpClient http, string baseUrl = "https://huggingface.co")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>An HttpClient tuned for large Hub downloads (redirects to the CDN, no timeout).</summary>
    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            MaxConnectionsPerServer = 16,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None, // model files are already compressed
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceType/1.0 (+https://github.com/DimQ1/nemotron-speech-csharp)");
        return client;
    }

    /// <summary>
    /// Lists the files of <paramref name="repoId"/>. With <paramref name="subfolder"/>
    /// only that folder is returned and its prefix is stripped from the local path;
    /// with <paramref name="singleFile"/> only that file is returned.
    /// </summary>
    public async Task<IReadOnlyList<RemoteFile>> ListFilesAsync(
        string repoId, string? subfolder = null, string? singleFile = null, CancellationToken cancellationToken = default)
    {
        // blobs=true makes the Hub report sizes of LFS files, which byte progress needs.
        using var response = await _http.GetAsync($"{_baseUrl}/api/models/{repoId}?blobs=true", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var files = new List<RemoteFile>();
        if (!document.RootElement.TryGetProperty("siblings", out var siblings))
            return files;

        var prefix = string.IsNullOrEmpty(subfolder) ? null : subfolder.TrimEnd('/') + "/";
        foreach (var sibling in siblings.EnumerateArray())
        {
            var path = sibling.GetProperty("rfilename").GetString() ?? "";
            if (path.Length == 0 || Path.GetFileName(path).StartsWith('.'))
                continue;
            if (singleFile is not null && !string.Equals(path, singleFile, StringComparison.Ordinal))
                continue;
            if (prefix is not null && !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var local = prefix is null ? path : path[prefix.Length..];
            if (singleFile is not null)
                local = Path.GetFileName(path);
            var size = sibling.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number
                ? sizeElement.GetInt64()
                : 0;
            files.Add(new RemoteFile(path, local, size));
        }

        return files;
    }

    /// <summary>True when the file is already present with the expected size (download can be skipped).</summary>
    public static bool IsComplete(RemoteFile file, string targetDirectory)
    {
        var path = Path.Combine(targetDirectory, file.LocalPath);
        return File.Exists(path) && file.SizeBytes > 0 && new FileInfo(path).Length == file.SizeBytes;
    }

    /// <summary>
    /// Downloads one file into <paramref name="targetDirectory"/>. A partial
    /// <c>.download</c> file from an interrupted run is resumed with a Range request.
    /// <paramref name="onBytes"/> receives the byte count of every chunk written
    /// (including the resumed prefix once, up front).
    /// </summary>
    public async Task DownloadFileAsync(
        string repoId, RemoteFile file, string targetDirectory, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(targetDirectory, file.LocalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + TempSuffix;

        long existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (file.SizeBytes > 0 && existing > file.SizeBytes)
        {
            File.Delete(temp);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(repoId, file.RepoPath));
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // The partial file already holds everything.
            onBytes(existing);
            MoveIntoPlace(temp, destination, cancellationToken);
            return;
        }

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Hugging Face rejected '{file.RepoPath}': {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed)
            onBytes(existing);

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(temp, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onBytes(read);
            }
        }

        MoveIntoPlace(temp, destination, cancellationToken);
    }

    private string ResolveUrl(string repoId, string repoPath) =>
        $"{_baseUrl}/{repoId}/resolve/main/{string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>
    /// Replaces the destination atomically, retrying while it is locked (a running
    /// recognizer may have the previous model memory-mapped).
    /// </summary>
    private static void MoveIntoPlace(string temp, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 12)
            {
                Thread.Sleep(250);
            }
        }
    }
}
