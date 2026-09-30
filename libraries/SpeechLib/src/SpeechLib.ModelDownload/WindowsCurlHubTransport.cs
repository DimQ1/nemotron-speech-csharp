using System.Diagnostics;

namespace SpeechLib.ModelDownload;

/// <summary>
/// Transport for WSL instances that cannot reach huggingface.co directly
/// (restricted DNS/routing inside the distro): it runs the Windows
/// <c>curl.exe</c> through the WSL mount. Used only when
/// <see cref="TryCreate"/> finds both WSL and a Windows curl; every other
/// environment keeps the plain HTTP transport.
/// </summary>
public sealed class WindowsCurlHubTransport : IHubTransport
{
    private const string WindowsCurlPath = "/mnt/c/Windows/System32/curl.exe";

    private readonly string _curlPath;

    private WindowsCurlHubTransport(string curlPath) => _curlPath = curlPath;

    /// <summary>The curl executable this transport uses (for diagnostics/tests).</summary>
    public string CurlPath => _curlPath;

    /// <summary>
    /// Returns a transport when running inside WSL with a reachable Windows curl,
    /// otherwise null (callers fall back to <see cref="HttpHubTransport"/>).
    /// </summary>
    public static WindowsCurlHubTransport? TryCreate()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            return null;

        return File.Exists(WindowsCurlPath) ? new WindowsCurlHubTransport(WindowsCurlPath) : null;
    }

    /// <inheritdoc />
    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var process = Start(BuildMetadataArguments(url));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new HttpRequestException(
                    $"curl could not read {url}: {Detail(process.ExitCode, error)}");
            }

            return output;
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DownloadToFileAsync(
        string url, string tempPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
    {
        // Unlike the HTTP transport this path does not resume a partial download:
        // curl streams to stdout here (a Windows curl cannot write to a Linux path),
        // and the Windows curl cannot be told to continue a WSL file.
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        using var process = Start(BuildDownloadArguments(url));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            long written = 0;
            await using (var target = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await process.StandardOutput.BaseStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    onBytes(read);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new HttpRequestException($"curl could not download {url}: {Detail(process.ExitCode, error)}");

            if (written == 0)
                throw new InvalidOperationException($"curl returned an empty body for {url}.");
        }
        catch
        {
            Kill(process);
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>The exact curl invocation used to read Hub metadata (JSON).</summary>
    public static IReadOnlyList<string> BuildMetadataArguments(string url) =>
    [
        .. CommonArguments(),
        url
    ];

    /// <summary>The exact curl invocation used to stream a file to stdout.</summary>
    public static IReadOnlyList<string> BuildDownloadArguments(string url) =>
    [
        .. CommonArguments(),
        "--output",
        "-",
        url
    ];

    private static IEnumerable<string> CommonArguments()
    {
        yield return "--fail-with-body";
        yield return "--location";
        yield return "--retry";
        yield return "3";
        yield return "--retry-delay";
        yield return "1";
        yield return "--retry-all-errors";
        yield return "--connect-timeout";
        yield return "15";
        yield return "--silent";
        yield return "--show-error";
    }

    private Process Start(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start curl at '{_curlPath}'.");
        }

        return process;
    }

    private static string Detail(int exitCode, string error) => string.IsNullOrWhiteSpace(error)
        ? $"curl exited with code {exitCode}"
        : error.Trim();

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a leftover temp file is harmless.
        }
    }
}
