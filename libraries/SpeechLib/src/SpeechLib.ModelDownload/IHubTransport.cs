namespace SpeechLib.ModelDownload;

/// <summary>
/// How the Hub is reached. The default implementation uses <see cref="HttpClient"/>,
/// but WSL instances whose networking cannot reach huggingface.co (DNS/route
/// restrictions) shell out to the Windows <c>curl.exe</c> instead — the
/// <see cref="WindowsCurlHubTransport"/> does that.
/// </summary>
public interface IHubTransport
{
    /// <summary>GETs a Hub URL and returns the response body (repo metadata).</summary>
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    /// Streams a file into <paramref name="tempPath"/>.
    /// <paramref name="onBytes"/> receives every chunk written; when the transport
    /// resumes a partial file it reports the existing prefix once up front.
    /// The file is written in place (no temp rename) — the caller owns the name.
    /// </summary>
    Task DownloadToFileAsync(
        string url,
        string tempPath,
        long expectedSize,
        Action<long> onBytes,
        CancellationToken cancellationToken);
}
