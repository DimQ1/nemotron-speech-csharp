using System.Net;
using System.Net.Http.Headers;

namespace SpeechLib.ModelDownload;

/// <summary>Default transport: plain HTTP against the Hub (redirects to the CDN).</summary>
public sealed class HttpHubTransport : IHubTransport
{
    private readonly HttpClient _http;

    public HttpHubTransport(HttpClient http)
        => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DownloadToFileAsync(
        string url, string tempPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            // The partial file is larger than the real file: it cannot be a prefix of it.
            File.Delete(tempPath);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // The partial file already holds everything the server has.
            onBytes(existing);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Hugging Face rejected the request: {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed)
            onBytes(existing);

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
            tempPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 16,
            useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onBytes(read);
            }
        }
    }
}
