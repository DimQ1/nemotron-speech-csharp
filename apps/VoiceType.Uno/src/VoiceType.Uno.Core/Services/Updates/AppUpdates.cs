using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoiceType.Uno.Services.Updates;

/// <summary>A release version <c>MAJOR.MINOR.PATCH</c> (the Uno release tags are <c>uno-vX.Y.Z</c>, see release-voicetype-uno.yml).</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public const string TagPrefix = "uno-v";

    /// <summary>Parses <c>1.10.0</c>, <c>v1.10.0</c>, <c>uno-v1.10.0</c> or <c>1.10</c>; anything else (or a pre-release suffix) is null.</summary>
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) t = t[TagPrefix.Length..];
        else if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        var parts = t.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var n = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
            if (i < 3) n[i] = v;
        }
        return new AppVersion(n[0], n[1], n[2]);
    }

    public int CompareTo(AppVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>One downloadable file of a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>A published GitHub release of the Uno app.</summary>
public sealed record AppRelease(AppVersion Version, string Tag, string Title, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Where the app runs, as far as updating is concerned.</summary>
public enum UpdatePlatform { WindowsX64, LinuxX64, MacArm64, Android, Ios, Other }

/// <summary>How an update is applied.</summary>
public enum UpdateInstallKind
{
    /// <summary>Not offered: Microsoft Store package (the Store updates it), iOS.</summary>
    None,
    /// <summary>Download the archive, unpack it, replace the app folder after the app quits, start the new version.</summary>
    ReplaceInPlace,
    /// <summary>Download a package and hand it to the system (macOS .dmg, Debian .deb).</summary>
    OpenPackage,
    /// <summary>Open the release page in the browser (Android, read-only install folder, a local developer build).</summary>
    OpenPage,
}

/// <summary>What the app offers for one release on this machine.</summary>
public sealed record UpdatePlan(AppRelease Release, UpdateInstallKind Kind, ReleaseAsset? Asset);

/// <summary>
/// Updates from GitHub Releases (the tags <c>uno-vX.Y.Z</c> that release-voicetype-uno.yml publishes): finds the newest
/// release, picks the asset of this platform and decides how it is installed. Pure logic; the download and the
/// replacement run in the app (<c>VoiceType.Uno.Services.UpdateInstaller</c>).
/// </summary>
public static class AppUpdates
{
    public const string Repository = "DimQ1/nemotron-speech-csharp";
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{Repository}/releases?per_page=30";
    public static string ReleasesPageUrl => $"https://github.com/{Repository}/releases";

    /// <summary>Automatic checks at start run at most this often.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(20);

    /// <summary>The version a local build reports (csproj default): such a build never replaces itself.</summary>
    public static readonly AppVersion DeveloperBuild = new(1, 0, 0);

    /// <summary>Parses the GitHub "list releases" answer; drafts, pre-releases and other tag families are left out.</summary>
    public static IReadOnlyList<AppRelease> ParseReleases(string json)
    {
        var result = new List<AppRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft") || Bool(r, "prerelease")) continue;
            var tag = Str(r, "tag_name");
            if (!tag.StartsWith(AppVersion.TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (AppVersion.Parse(tag) is not { } version) continue;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var url = Str(a, "browser_download_url");
                    if (url.Length > 0)
                        assets.Add(new ReleaseAsset(Str(a, "name"), url, a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0));
                }
            }
            var title = Str(r, "name");
            result.Add(new AppRelease(version, tag, title.Length > 0 ? title : tag, Str(r, "body"), Str(r, "html_url"), assets));
        }
        return result.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>The newest release when it is newer than <paramref name="current"/>, else null.</summary>
    public static AppRelease? Newer(IReadOnlyList<AppRelease> releases, AppVersion current) =>
        releases.Where(r => r.Version > current).OrderByDescending(r => r.Version).FirstOrDefault();

    /// <summary>The release asset of a platform, or null when the release has none.</summary>
    public static ReleaseAsset? AssetFor(AppRelease release, UpdatePlatform platform, bool debianPackage = false)
    {
        bool Ends(ReleaseAsset a, string suffix) => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        return platform switch
        {
            UpdatePlatform.WindowsX64 => release.Assets.FirstOrDefault(a => a.Name.Equals("VoiceType.Uno-win-x64.zip", StringComparison.OrdinalIgnoreCase)),
            UpdatePlatform.LinuxX64 when debianPackage => release.Assets.FirstOrDefault(a => Ends(a, "_amd64.deb")),
            UpdatePlatform.LinuxX64 => release.Assets.FirstOrDefault(a => a.Name.Equals("VoiceType.Uno-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase)),
            UpdatePlatform.MacArm64 => release.Assets.FirstOrDefault(a => Ends(a, "macos-arm64.dmg")),
            UpdatePlatform.Android => release.Assets.FirstOrDefault(a => Ends(a, ".apk")),
            _ => null,
        };
    }

    /// <summary>
    /// How this installation takes <paramref name="release"/>.
    /// </summary>
    /// <param name="storePackage">Microsoft Store (MSIX) package: the Store updates it, the app never does.</param>
    /// <param name="current">The running version; a developer build (1.0.0) only gets the release page.</param>
    /// <param name="installFolderWritable">The app folder can be replaced by the user (portable zip / tar.gz).</param>
    /// <param name="debianPackage">Linux installed from the .deb (under /usr or /opt).</param>
    public static UpdatePlan? Plan(AppRelease release, UpdatePlatform platform, AppVersion current, bool storePackage,
        bool installFolderWritable, bool debianPackage = false)
    {
        if (storePackage || platform is UpdatePlatform.Ios or UpdatePlatform.Other)
            return null;
        if (!(release.Version > current))
            return null;
        var asset = AssetFor(release, platform, debianPackage);
        var kind = platform switch
        {
            // Android release APKs are signed with a throw-away debug key (release-voicetype-uno.yml): Android refuses
            // them as an update of the installed copy, so the page explains it and offers the file.
            UpdatePlatform.Android => UpdateInstallKind.OpenPage,
            _ when current == DeveloperBuild => UpdateInstallKind.OpenPage,
            _ when asset is null => UpdateInstallKind.OpenPage,
            UpdatePlatform.MacArm64 => UpdateInstallKind.OpenPackage,
            UpdatePlatform.LinuxX64 when debianPackage => UpdateInstallKind.OpenPackage,
            _ when !installFolderWritable => UpdateInstallKind.OpenPage,
            _ => UpdateInstallKind.ReplaceInPlace,
        };
        return new UpdatePlan(release, kind, kind == UpdateInstallKind.OpenPage ? null : asset);
    }

    /// <summary>Whether a start-up check is due.</summary>
    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= CheckInterval || last > now;

    /// <summary>The release notes shortened for a dialog (Markdown left as text).</summary>
    public static string ShortNotes(string notes, int maxLength = 1200)
    {
        var text = (notes ?? "").Replace("\r\n", "\n").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    /// <summary>
    /// Windows: a PowerShell script that waits for the app to quit, copies the unpacked release over the app folder,
    /// starts the new version and removes the staging folder.
    /// </summary>
    public static string WindowsReplaceScript(int processId, string stagingDir, string appDir, string executable)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$deadline = (Get-Date).AddSeconds(60)");
        sb.AppendLine($"while ((Get-Process -Id {processId} -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {{ Start-Sleep -Milliseconds 300 }}");
        sb.AppendLine("Start-Sleep -Milliseconds 500");
        sb.AppendLine($"robocopy {Q(stagingDir)} {Q(appDir)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null");
        sb.AppendLine($"Start-Process -FilePath {Q(executable)}");
        sb.AppendLine($"Remove-Item -LiteralPath {Q(stagingDir)} -Recurse -Force -ErrorAction SilentlyContinue");
        return sb.ToString();
    }

    /// <summary>Linux/macOS: the same as <see cref="WindowsReplaceScript"/> for <c>sh</c>.</summary>
    public static string UnixReplaceScript(int processId, string stagingDir, string appDir, string executable)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("i=0\n");
        sb.Append($"while kill -0 {processId} 2>/dev/null && [ $i -lt 200 ]; do sleep 0.3; i=$((i+1)); done\n");
        sb.Append($"cp -a {Q(stagingDir)}/. {Q(appDir)}/\n");
        sb.Append($"chmod +x {Q(executable)}\n");
        sb.Append($"nohup {Q(executable)} >/dev/null 2>&1 &\n");
        sb.Append($"rm -rf {Q(stagingDir)}\n");
        return sb.ToString();
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
