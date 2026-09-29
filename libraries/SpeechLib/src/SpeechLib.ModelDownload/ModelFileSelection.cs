namespace SpeechLib.ModelDownload;

/// <summary>
/// Maps Hub files onto local model folders. A multi-precision repo (Parakeet
/// fp32/int8/int4) keeps its files in subfolders: the subfolder must be stripped
/// for the LOCAL path while the REPO path stays intact, because the file is still
/// fetched from <c>resolve/main/&lt;subfolder&gt;/&lt;file&gt;</c>.
/// Shared by the WinUI and Uno downloaders.
/// </summary>
public static class ModelFileSelection
{
    /// <summary>
    /// Returns the files of <paramref name="subfolder"/> with the subfolder prefix
    /// removed from <see cref="RemoteFile.LocalPath"/>. A null/empty subfolder
    /// returns <paramref name="files"/> unchanged.
    /// </summary>
    public static IReadOnlyList<RemoteFile> ForSubfolder(IEnumerable<RemoteFile> files, string? subfolder)
    {
        var all = files
            .Where(file => !string.IsNullOrWhiteSpace(file.RepoPath))
            .ToList();

        if (string.IsNullOrWhiteSpace(subfolder))
            return all;

        var prefix = subfolder.TrimEnd('/') + "/";
        return all
            .Where(file => file.RepoPath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(file => file with { LocalPath = file.RepoPath[prefix.Length..] })
            .ToList();
    }
}
