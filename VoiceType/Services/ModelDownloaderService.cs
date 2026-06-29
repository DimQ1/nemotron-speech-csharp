using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoiceType.Services;

/// <summary>
/// Downloads ONNX model files from HuggingFace or custom URLs with progress tracking.
/// </summary>
public sealed class ModelDownloaderService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource? _cts;

    public event Action<DownloadProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;
    public event Action<bool, string>? Completed;

    public bool IsDownloading { get; private set; }

    /// <summary>Fetch file list from a HuggingFace repo, optionally filtered by subfolder.</summary>
    public async Task<List<HfFile>> FetchRepoFiles(string repoId, string? subfolder = null)
    {
        var displayId = subfolder is not null ? $"{repoId}/{subfolder}" : repoId;
        StatusChanged?.Invoke($"Fetching file list from {displayId}...");
        var url = $"https://huggingface.co/api/models/{repoId}";
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var files = new List<HfFile>();

        if (json.TryGetProperty("siblings", out var siblings))
        {
            var prefix = subfolder is not null ? subfolder.TrimEnd('/') + "/" : "";
            foreach (var sib in siblings.EnumerateArray())
            {
                var rfilename = sib.GetProperty("rfilename").GetString() ?? "";
                if (rfilename.StartsWith(".")) continue;
                // Filter by subfolder prefix
                if (!string.IsNullOrEmpty(prefix) && !rfilename.StartsWith(prefix)) continue;
                var size = sib.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                files.Add(new HfFile
                {
                    Name = rfilename,
                    SizeBytes = size,
                    Selected = true
                });
            }
        }
        StatusChanged?.Invoke($"Found {files.Count} files in {displayId}");
        return files;
    }

    /// <summary>Download selected files from a HuggingFace repo.</summary>
    public async Task DownloadFromHuggingFace(string repoId, List<HfFile> files, string targetDir)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Directory.CreateDirectory(targetDir);

        var selected = files.Where(f => f.Selected).ToList();
        long totalBytes = selected.Sum(f => f.SizeBytes);
        long downloadedBytes = 0;
        int completed = 0;

        foreach (var file in selected)
        {
            var url = $"https://huggingface.co/{repoId}/resolve/main/{file.Name}";
            var dest = Path.Combine(targetDir, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); // create subdirs

            StatusChanged?.Invoke($"Downloading {file.Name} ({FormatSize(file.SizeBytes)})...");

            try
            {
                await DownloadFileAsync(url, dest, file.SizeBytes,
                    (bytes) =>
                    {
                        downloadedBytes += bytes;
                        ProgressChanged?.Invoke(new DownloadProgress
                        {
                            CurrentFile = file.Name,
                            FileProgress = file.SizeBytes > 0 ? (double)file.DownloadedBytes / file.SizeBytes * 100 : 0,
                            OverallProgress = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0,
                            DownloadedFiles = completed,
                            TotalFiles = selected.Count
                        });
                    }, _cts.Token);

                completed++;
                downloadedBytes = selected.Take(completed).Sum(f => f.SizeBytes);
            }
            catch (OperationCanceledException)
            {
                StatusChanged?.Invoke("Download cancelled");
                IsDownloading = false;
                Completed?.Invoke(false, "Cancelled");
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Error: {ex.Message}");
                IsDownloading = false;
                Completed?.Invoke(false, ex.Message);
                return;
            }
        }

        IsDownloading = false;
        StatusChanged?.Invoke($"Download complete — {completed} files to {targetDir}");
        Completed?.Invoke(true, targetDir);
    }

    /// <summary>Download a single file from any URL.</summary>
    public async Task DownloadSingleFile(string url, string destPath)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        var dir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(destPath);

        StatusChanged?.Invoke($"Downloading {fileName}...");
        await DownloadFileAsync(url, destPath, 0, _ => { }, _cts.Token);

        IsDownloading = false;
        StatusChanged?.Invoke($"Download complete → {destPath}");
        Completed?.Invoke(true, destPath);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        IsDownloading = false;
    }

    private async Task DownloadFileAsync(string url, string dest, long totalSize,
        Action<long> onChunk, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            onChunk(read);
        }
    }

    public void Dispose() { _cts?.Dispose(); _http.Dispose(); }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        >= 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B"
    };
}

public sealed class HfFile
{
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public long DownloadedBytes { get; set; }
    public bool Selected { get; set; }
}

public sealed class DownloadProgress
{
    public string CurrentFile { get; init; } = "";
    public double FileProgress { get; init; }
    public double OverallProgress { get; init; }
    public int DownloadedFiles { get; init; }
    public int TotalFiles { get; init; }
}
