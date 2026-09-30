using System.Net;
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
/// The wire format is provided by an <see cref="IHubTransport"/>, so WSL
/// instances that cannot reach the Hub over HTTP can run the Windows curl
/// instead (see <see cref="WindowsCurlHubTransport"/>).
/// </summary>
public sealed class HuggingFaceClient
{
    private const string TempSuffix = ".download";

    private readonly IHubTransport _transport;
    private readonly string _baseUrl;

    /// <summary>Client over an explicit transport (HTTP, Windows curl, a test double…).</summary>
    public HuggingFaceClient(IHubTransport transport, string baseUrl = "https://huggingface.co")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Client over the default HTTP transport.</summary>
    public HuggingFaceClient(HttpClient http, string baseUrl = "https://huggingface.co")
        : this(new HttpHubTransport(http), baseUrl)
    {
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
        var json = await _transport
            .GetStringAsync($"{_baseUrl}/api/models/{repoId}?blobs=true", cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var files = new List<RemoteFile>();
        if (!document.RootElement.TryGetProperty("siblings", out var siblings))
            return files;

        foreach (var sibling in siblings.EnumerateArray())
        {
            var path = sibling.GetProperty("rfilename").GetString() ?? "";
            if (path.Length == 0 || Path.GetFileName(path).StartsWith('.'))
                continue;

            var size = sibling.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number
                ? sizeElement.GetInt64()
                : 0;
            files.Add(new RemoteFile(path, path, size));
        }

        if (singleFile is not null)
        {
            return files
                .Where(file => string.Equals(file.RepoPath, singleFile, StringComparison.Ordinal))
                .Select(file => file with { LocalPath = Path.GetFileName(file.RepoPath) })
                .ToList();
        }

        // The subfolder is stripped from the local path only — the repo path stays
        // intact, because that is what the download URL is built from.
        return ModelFileSelection.ForSubfolder(files, subfolder);
    }

    /// <summary>True when the file is already present with the expected size (download can be skipped).</summary>
    public static bool IsComplete(RemoteFile file, string targetDirectory)
    {
        var path = Path.Combine(targetDirectory, file.LocalPath);
        return File.Exists(path) && file.SizeBytes > 0 && new FileInfo(path).Length == file.SizeBytes;
    }

    /// <summary>
    /// Downloads one file into <paramref name="targetDirectory"/>. A partial
    /// <c>.download</c> file from an interrupted run is resumed when the transport
    /// supports it (the HTTP one does, the Windows curl one restarts).
    /// <paramref name="onBytes"/> receives the byte count of every chunk written
    /// (including the resumed prefix once, up front).
    /// </summary>
    public async Task DownloadFileAsync(
        string repoId, RemoteFile file, string targetDirectory, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(targetDirectory, file.LocalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + TempSuffix;

        await _transport.DownloadToFileAsync(
            ResolveUrl(repoId, file.RepoPath),
            temp,
            file.SizeBytes,
            onBytes,
            cancellationToken).ConfigureAwait(false);

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
