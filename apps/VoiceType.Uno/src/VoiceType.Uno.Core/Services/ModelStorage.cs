namespace VoiceType.Uno.Services;

/// <summary>Disk usage of downloaded models, for the "x GB used" line of the Models page.</summary>
public static class ModelStorage
{
    /// <summary>
    /// Size of a model file or of every file under a model folder; 0 when the path
    /// does not exist or cannot be read. Model folders hold a handful of large files,
    /// so a recursive walk is cheap.
    /// </summary>
    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;

            if (!Directory.Exists(path))
                return 0;

            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                total += file.Length;
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
