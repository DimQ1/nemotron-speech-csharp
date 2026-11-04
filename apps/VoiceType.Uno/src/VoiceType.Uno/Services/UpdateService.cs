using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services.Updates;

namespace VoiceType.Uno.Services;

/// <summary>State of the last update check, worded at display time (follows a language switch).</summary>
public enum UpdateCheckState { NotChecked, Checking, UpToDate, Available, Failed }

/// <summary>
/// Updates the app from GitHub Releases (<see cref="AppUpdates"/>): checks the newest <c>uno-vX.Y.Z</c> release, downloads
/// the asset of this platform and either replaces the app folder after the app quits (portable Windows zip, Linux tar.gz)
/// or hands the package/page to the system. Off in the Microsoft Store package and on iOS.
/// </summary>
public sealed class UpdateService
{
    private readonly SettingsStore _settings;
    private readonly HttpClient _http;
    private string _error = "";
    private string? _skipped;

    public UpdateService(SettingsStore settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub's API needs a User-Agent; a generic client name, nothing about the user.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoiceType-Updater", CurrentVersion.ToString()));
    }

    /// <summary>Raised (on any thread) when <see cref="State"/> or <see cref="Available"/> changes.</summary>
    public event Action? Changed;

    public UpdateCheckState State { get; private set; }

    /// <summary>The update this installation can take, after a successful check.</summary>
    public UpdatePlan? Available { get; private set; }

    /// <summary>The running version (<c>ApplicationDisplayVersion</c>, set by the release workflow; 1.0.0 for local builds).</summary>
    public static AppVersion CurrentVersion { get; } = ReadVersion();

    public static UpdatePlatform Platform { get; } = DetectPlatform();

    /// <summary>False in the Store package (the Store updates it) and where no release asset exists (iOS).</summary>
    public static bool IsSupported => !PackageIdentity.IsPackaged && Platform is not (UpdatePlatform.Ios or UpdatePlatform.Other);

    /// <summary>One line for Settings: what the last check found.</summary>
    public string StatusText => State switch
    {
        UpdateCheckState.Checking => Loc.T("Updates.Status.Checking"),
        UpdateCheckState.UpToDate => Loc.F("Updates.Status.UpToDate", CurrentVersion),
        UpdateCheckState.Available when Available is { } plan => Loc.F("Updates.Status.Available", plan.Release.Version),
        UpdateCheckState.Failed => Loc.F("Updates.Status.Failed", _error),
        _ => _settings.Current.LastUpdateCheck is { } last
            ? Loc.F("Updates.Status.LastCheck", last.ToLocalTime().ToString("g", Loc.Culture))
            : Loc.F("Updates.Status.Version", CurrentVersion),
    };

    /// <summary>The start-up check: only when enabled and due; a skipped version stays hidden.</summary>
    public async Task<UpdatePlan?> CheckOnStartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!IsSupported || !settings.CheckUpdatesOnStart || !AppUpdates.IsCheckDue(settings.LastUpdateCheck, DateTimeOffset.UtcNow))
            return null;
        var plan = await CheckAsync(cancellationToken).ConfigureAwait(false);
        return plan is not null && IsSkipped(plan) ? null : plan;
    }

    /// <summary>Asks GitHub for the newest release. Never throws: a failure becomes <see cref="UpdateCheckState.Failed"/>.</summary>
    public async Task<UpdatePlan?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return null;
        SetState(UpdateCheckState.Checking, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppUpdates.ReleasesApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var newer = AppUpdates.Newer(AppUpdates.ParseReleases(json), CurrentVersion);
            var plan = newer is null ? null
                : AppUpdates.Plan(newer, Platform, CurrentVersion, PackageIdentity.IsPackaged, IsInstallFolderWritable(), IsDebianInstall());
            _settings.Post(s => s.LastUpdateCheck = DateTimeOffset.UtcNow);
            SetState(plan is null ? UpdateCheckState.UpToDate : UpdateCheckState.Available, plan);
            return plan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[VoiceType.Uno] Update check failed: {ex.Message}");
            _error = ex.Message;
            SetState(UpdateCheckState.Failed, null);
            return null;
        }
    }

    /// <summary>The user chose "Skip this version" for this release.</summary>
    public bool IsSkipped(UpdatePlan plan) => plan.Release.Version.ToString() == (_skipped ?? _settings.Current.SkippedUpdateVersion);

    /// <summary>"Skip this version": its banner is not shown again.</summary>
    public void Skip(UpdatePlan plan)
    {
        _skipped = plan.Release.Version.ToString();
        _settings.Post(s => s.SkippedUpdateVersion = plan.Release.Version.ToString());
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies <paramref name="plan"/>. For <see cref="UpdateInstallKind.ReplaceInPlace"/> the returned task completes when
    /// the replacement script runs and the caller must quit the app (<c>Application.Current.Exit()</c>); the script waits
    /// for that, copies the new files and starts the new version. Returns false when nothing needs quitting.
    /// </summary>
    public async Task<bool> InstallAsync(UpdatePlan plan, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        switch (plan.Kind)
        {
            case UpdateInstallKind.OpenPage:
                await OpenUriAsync(plan.Release.PageUrl.Length > 0 ? plan.Release.PageUrl : AppUpdates.ReleasesPageUrl).ConfigureAwait(false);
                return false;

            case UpdateInstallKind.OpenPackage:
            {
                var file = await DownloadAsync(plan, progress, cancellationToken).ConfigureAwait(false);
                OpenFile(file);
                return false;
            }

            case UpdateInstallKind.ReplaceInPlace:
            {
                var file = await DownloadAsync(plan, progress, cancellationToken).ConfigureAwait(false);
                var staging = Path.Combine(Path.GetDirectoryName(file)!, "staging");
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                await Task.Run(() => Extract(file, staging), cancellationToken).ConfigureAwait(false);
                StartReplacement(staging);
                return true;
            }

            default:
                return false;
        }
    }

    private async Task<string> DownloadAsync(UpdatePlan plan, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var asset = plan.Asset ?? throw new InvalidOperationException("The release has no file for this platform.");
        var dir = Path.Combine(Path.GetTempPath(), "voicetype-update", plan.Release.Version.ToString());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, asset.Name);
        var partial = target + ".part";

        using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total > 0)
                    progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    private static void Extract(string archive, string staging)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            return;
        }
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
    }

    private static void StartReplacement(string staging)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executable = Environment.ProcessPath ?? Path.Combine(appDir, OperatingSystem.IsWindows() ? "VoiceType.Uno.exe" : "VoiceType.Uno");
        var pid = Environment.ProcessId;
        var scriptDir = Path.GetDirectoryName(staging)!;
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(scriptDir, "apply-update.ps1");
            File.WriteAllText(script, AppUpdates.WindowsReplaceScript(pid, staging, appDir, executable));
            start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                CreateNoWindow = true,
            };
        }
        else
        {
            var script = Path.Combine(scriptDir, "apply-update.sh");
            File.WriteAllText(script, AppUpdates.UnixReplaceScript(pid, staging, appDir, executable));
            start = new ProcessStartInfo("/bin/sh") { ArgumentList = { script } };
        }
        start.UseShellExecute = false;
        Process.Start(start);
    }

    private static async Task OpenUriAsync(string url)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Could not open {url}: {ex.Message}");
            if (!OperatingSystem.IsAndroid())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private static void OpenFile(string file)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start("open", [file]);
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", [file]);
        else
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void SetState(UpdateCheckState state, UpdatePlan? plan)
    {
        State = state;
        Available = plan;
        Changed?.Invoke();
    }

    private static bool IsInstallFolderWritable()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDebianInstall() =>
        OperatingSystem.IsLinux()
        && (AppContext.BaseDirectory.StartsWith("/usr/", StringComparison.Ordinal) || AppContext.BaseDirectory.StartsWith("/opt/", StringComparison.Ordinal));

    private static AppVersion ReadVersion()
    {
        var text = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ApplicationDisplayVersion")?.Value;
        return AppVersion.Parse(text) ?? AppUpdates.DeveloperBuild;
    }

    private static UpdatePlatform DetectPlatform()
    {
        if (OperatingSystem.IsAndroid()) return UpdatePlatform.Android;
        if (OperatingSystem.IsIOS()) return UpdatePlatform.Ios;
        if (OperatingSystem.IsMacOS()) return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? UpdatePlatform.MacArm64 : UpdatePlatform.Other;
        if (OperatingSystem.IsWindows()) return RuntimeInformation.OSArchitecture == Architecture.X64 ? UpdatePlatform.WindowsX64 : UpdatePlatform.Other;
        if (OperatingSystem.IsLinux()) return RuntimeInformation.OSArchitecture == Architecture.X64 ? UpdatePlatform.LinuxX64 : UpdatePlatform.Other;
        return UpdatePlatform.Other;
    }
}
