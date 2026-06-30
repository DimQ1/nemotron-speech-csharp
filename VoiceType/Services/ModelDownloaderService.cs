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
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "VoiceType/1.0" } }
    };
    private CancellationTokenSource? _cts;

    public event Action<DownloadProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;
    public event Action<bool, string>? Completed;

    public bool IsDownloading { get; private set; }

    /// <summary>Fetch files from repo and group by top-level folders.</summary>
    public async Task<List<HfFolder>> FetchRepoFolders(string repoId)
    {
        StatusChanged?.Invoke($"Fetching file list from {repoId}...");
        var url = $"https://huggingface.co/api/models/{repoId}";
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var allFiles = new List<HfFile>();

        if (json.TryGetProperty("siblings", out var siblings))
        {
            foreach (var sib in siblings.EnumerateArray())
            {
                var rfilename = sib.GetProperty("rfilename").GetString() ?? "";
                if (rfilename.StartsWith(".")) continue;
                var size = sib.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                allFiles.Add(new HfFile { Name = Path.GetFileName(rfilename), RelativePath = rfilename, SizeBytes = size });
            }
        }

        // Group by top-level folder
        var folders = allFiles
            .GroupBy(f => f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.IndexOf('/')] : "(root)")
            .OrderBy(g => g.Key == "(root)" ? 0 : 1).ThenBy(g => g.Key)
            .Select(g => new HfFolder
            {
                Name = g.Key == "(root)" ? "📄 Root files" : $"📁 {g.Key}",
                Files = g.OrderBy(f => f.RelativePath).ToList()
            })
            .ToList();

        StatusChanged?.Invoke($"Found {folders.Count} folder(s), {allFiles.Count} file(s) in {repoId}");
        return folders;
    }

    /// <summary>Download selected folders from a HuggingFace repo into targetRoot/{folderName}/.</summary>
    public async Task DownloadFromHuggingFace(string repoId, List<HfFolder> folders, string targetRoot)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        var selectedFolders = folders.Where(f => f.Selected).ToList();
        var allFiles = selectedFolders.SelectMany(f => f.Files).ToList();
        long totalBytes = allFiles.Sum(f => f.SizeBytes);
        long downloadedBytes = 0;
        int completed = 0;

        foreach (var folder in selectedFolders)
        {
            foreach (var file in folder.Files)
            {
                var url = $"https://huggingface.co/{repoId}/resolve/main/{file.RelativePath}";
                // Place file into: targetRoot/{SubfolderName}/{rest-of-path}
                var subfolder = string.IsNullOrEmpty(folder.SubfolderName)
                    ? repoId[(repoId.LastIndexOf('/') + 1)..]  // Use repo name for root files
                    : folder.SubfolderName;
                var relativePath = file.RelativePath.Contains('/')
                    ? file.RelativePath[(file.RelativePath.IndexOf('/') + 1)..]
                    : file.RelativePath;
                var dest = Path.Combine(targetRoot, subfolder, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                StatusChanged?.Invoke($"Downloading {file.RelativePath} ({FormatSize(file.SizeBytes)})...");

                try
                {
                    await DownloadFileAsync(url, dest, file.SizeBytes,
                        (bytes) =>
                        {
                            downloadedBytes += bytes;
                            file.DownloadedBytes += bytes;
                            ProgressChanged?.Invoke(new DownloadProgress
                            {
                                CurrentFile = file.RelativePath,
                                FileProgress = file.SizeBytes > 0 ? (double)file.DownloadedBytes / file.SizeBytes * 100 : 0,
                                OverallProgress = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0,
                                DownloadedFiles = completed,
                                TotalFiles = allFiles.Count
                            });
                        }, _cts.Token);

                    completed++;
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
                    StatusChanged?.Invoke($"Error on {file.RelativePath}: {ex.Message}");
                    IsDownloading = false;
                    Completed?.Invoke(false, ex.Message);
                    return;
                }
            }
        }

        IsDownloading = false;
        StatusChanged?.Invoke($"Download complete — {completed} files to {targetRoot}");
        Completed?.Invoke(true, targetRoot);
    }

    /// <summary>Download all files from a HuggingFace repo into targetRoot/{subfolder}/.</summary>
    public async Task DownloadModelRepo(string repoId, string subfolder, string targetRoot)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        // Fetch file list
        StatusChanged?.Invoke($"Fetching {repoId}...");
        var url = $"https://huggingface.co/api/models/{repoId}";
        var response = await _http.GetAsync(url, _cts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(_cts.Token);

        var files = new List<(string path, long size)>();
        if (json.TryGetProperty("siblings", out var siblings))
        {
            foreach (var sib in siblings.EnumerateArray())
            {
                var rfilename = sib.GetProperty("rfilename").GetString() ?? "";
                if (rfilename.StartsWith(".")) continue;
                var size = sib.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                files.Add((rfilename, size));
            }
        }

        long totalBytes = files.Sum(f => f.size);
        long downloadedBytes = 0;
        int completed = 0;

        foreach (var (path, size) in files)
        {
            var fileUrl = $"https://huggingface.co/{repoId}/resolve/main/{path}";
            var dest = Path.Combine(targetRoot, subfolder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            StatusChanged?.Invoke($"Downloading {path} ({FormatSize(size)})...");

            try
            {
                long lastBytes = 0;
                await DownloadFileAsync(fileUrl, dest, size,
                    (bytes) =>
                    {
                        downloadedBytes += bytes;
                        lastBytes += bytes;
                        ProgressChanged?.Invoke(new DownloadProgress
                        {
                            CurrentFile = path,
                            FileProgress = size > 0 ? (double)lastBytes / size * 100 : 0,
                            OverallProgress = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0,
                            DownloadedFiles = completed,
                            TotalFiles = files.Count
                        });
                    }, _cts.Token);
                completed++;
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
                StatusChanged?.Invoke($"Error on {path}: {ex.Message}");
                IsDownloading = false;
                Completed?.Invoke(false, ex.Message);
                return;
            }
        }

        IsDownloading = false;
        StatusChanged?.Invoke($"Download complete — {completed} files to {targetRoot}/{subfolder}");
        Completed?.Invoke(true, targetRoot);
    }
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
    public string RelativePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public long DownloadedBytes { get; set; }
}

/// <summary>A folder containing HuggingFace files (grouped by top-level directory).</summary>
public sealed class HfFolder
{
    public string Name { get; init; } = "";
    public string SubfolderName => Name switch
    {
        _ when Name.StartsWith("📁 ") => Name[3..],
        _ when Name.StartsWith("📄 ") => Name[3..],
        "(root)" => "",
        _ => Name
    };
    public List<HfFile> Files { get; init; } = new();
    public long TotalSize => Files.Sum(f => f.SizeBytes);
    public string SizeDisplay => ModelDownloaderService.FormatSize(TotalSize);
    public bool Selected { get; set; } = true;
}

public sealed class DownloadProgress
{
    public string CurrentFile { get; init; } = "";
    public double FileProgress { get; init; }
    public double OverallProgress { get; init; }
    public int DownloadedFiles { get; init; }
    public int TotalFiles { get; init; }
}
