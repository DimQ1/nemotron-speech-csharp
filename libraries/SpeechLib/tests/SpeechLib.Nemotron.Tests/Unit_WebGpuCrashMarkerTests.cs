using System.Diagnostics;
using System.Globalization;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The crash marker in files: written before a WebGPU session exists, gone after a clean close, and a leftover of a process that no
/// longer exists is what the next start reads as a crash. Uses a folder of its own per test.
/// </summary>
public sealed class Unit_WebGpuCrashMarkerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "webgpu-marker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder, "active-*.marker") : [];

    private string WriteForeignMarker(int processId, long startTicks = 0)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"active-{processId}.marker");
        File.WriteAllText(path, $"process={processId}\nstarted={startTicks.ToString(CultureInfo.InvariantCulture)}\n");
        return path;
    }

    // ── the file's lifecycle ────────────────────────────────────────────

    [Fact]
    public void Arm_ShouldWriteOneFileNamedAfterTheProcess_ThatTheLiveProcessDoesNotSeeAsStale()
    {
        var marker = new WebGpuCrashMarker(_folder);

        marker.Arm();

        var file = Assert.Single(Files());
        Assert.Equal($"active-{Environment.ProcessId}.marker", Path.GetFileName(file));
        Assert.Contains("SPEECHLIB_WEBGPU_RETRY=1", File.ReadAllText(file));
        Assert.Null(marker.FindStale());
    }

    [Fact]
    public void Disarm_ShouldRemoveTheFile_AndDoNothingWhenNotArmed()
    {
        var marker = new WebGpuCrashMarker(_folder);
        marker.Disarm();   // never armed
        marker.Arm();

        marker.Disarm();
        marker.Disarm();

        Assert.Empty(Files());
    }

    [Fact]
    public void Disarm_ShouldNotRemoveTheMarkerOfAnotherProcess()
    {
        var foreign = WriteForeignMarker(processId: 4711);
        var marker = new WebGpuCrashMarker(_folder, isAlive: (_, _) => true);
        marker.Arm();

        marker.Disarm();

        Assert.True(File.Exists(foreign));
    }

    // ── what is stale ───────────────────────────────────────────────────

    [Fact]
    public void FindStale_MarkerOfAProcessThatIsGone_ShouldDescribeIt()
    {
        var path = WriteForeignMarker(processId: 4711);
        var marker = new WebGpuCrashMarker(_folder, isAlive: (id, _) => id != 4711);

        var stale = marker.FindStale();

        Assert.NotNull(stale);
        Assert.Contains("process 4711 ended while it was using WebGPU", stale);
        Assert.Contains(path, stale);
        Assert.True(File.Exists(path));   // finding it does not remove it: WebGPU stays off until it is cleared on purpose
    }

    [Fact]
    public void FindStale_MarkerOfAProcessThatIsStillRunning_ShouldBeIgnored()
    {
        WriteForeignMarker(processId: 4711);
        var marker = new WebGpuCrashMarker(_folder, isAlive: (_, _) => true);

        Assert.Null(marker.FindStale());
    }

    [Fact]
    public void FindStale_ShouldPassTheProcessIdAndStartTimeFromTheFileToTheLivenessCheck()
    {
        WriteForeignMarker(processId: 4711, startTicks: 638_000_000_000_000_000);
        (int Id, long Ticks)? seen = null;
        var marker = new WebGpuCrashMarker(_folder, isAlive: (id, ticks) =>
        {
            seen = (id, ticks);
            return true;
        });

        marker.FindStale();

        Assert.Equal((4711, 638_000_000_000_000_000L), seen);
    }

    [Fact]
    public void FindStale_UnreadableFileOfADeadProcess_ShouldStillCountByItsName()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "active-4711.marker"), "half writt");   // the process died while writing
        var marker = new WebGpuCrashMarker(_folder, isAlive: (_, ticks) => ticks != 0);

        Assert.NotNull(marker.FindStale());   // start time unknown (0) -> judged by the id alone
    }

    [Theory]
    [InlineData("active-notanumber.marker")]
    [InlineData("active-.marker")]
    [InlineData("other-4711.marker")]
    [InlineData("active-4711.txt")]
    public void FindStale_FilesThatAreNotMarkers_ShouldBeIgnored(string name)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, name), "x");
        var marker = new WebGpuCrashMarker(_folder, isAlive: (_, _) => false);

        Assert.Null(marker.FindStale());
    }

    [Fact]
    public void FindStale_WithoutAFolder_ShouldBeNull()
    {
        Assert.Null(new WebGpuCrashMarker(_folder).FindStale());
    }

    [Fact]
    public void ClearStale_ShouldRemoveOnlyTheMarkersOfProcessesThatAreGone()
    {
        var gone1 = WriteForeignMarker(1001);
        var gone2 = WriteForeignMarker(1002);
        var running = WriteForeignMarker(1003);
        var marker = new WebGpuCrashMarker(_folder, isAlive: (id, _) => id == 1003);

        marker.ClearStale();

        Assert.False(File.Exists(gone1));
        Assert.False(File.Exists(gone2));
        Assert.True(File.Exists(running));
        Assert.Null(marker.FindStale());
    }

    // ── liveness as the operating system answers it ─────────────────────

    [Fact]
    public void FindStale_RealProcesses_ShouldTellTheLiveOneFromOneThatExited_AndNotBeFooledByAReusedId()
    {
        var self = Process.GetCurrentProcess();
        var marker = new WebGpuCrashMarker(_folder);

        // an exited process: start one, wait for it to end
        using var child = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var childStart = child.StartTime.ToUniversalTime().Ticks;
        child.WaitForExit();
        var exited = WriteForeignMarker(child.Id, childStart);
        Assert.NotNull(marker.FindStale());
        File.Delete(exited);

        // this very process with its real start time is alive; with a start time of another day it is a different process with a reused id
        var alive = WriteForeignMarker(self.Id, self.StartTime.ToUniversalTime().Ticks);
        Assert.Null(marker.FindStale());
        File.Delete(alive);
        WriteForeignMarker(self.Id, self.StartTime.ToUniversalTime().AddDays(-3).Ticks);
        Assert.NotNull(marker.FindStale());
    }

    // ── a store that does not work ──────────────────────────────────────

    [Fact]
    public void AnUnusableFolder_ShouldNeverThrow()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "file");
        File.WriteAllText(blocker, "a file where the marker folder should be");
        var marker = new WebGpuCrashMarker(Path.Combine(blocker, "webgpu"));

        marker.Arm();
        Assert.Null(marker.FindStale());
        marker.ClearStale();
        marker.Disarm();
    }

    [Fact]
    public void ArmAndDisarm_FromManyThreads_ShouldNeverThrowAndEndWithoutAFile()
    {
        var marker = new WebGpuCrashMarker(_folder);

        Parallel.For(0, 200, i =>
        {
            marker.Arm();
            marker.FindStale();
            marker.Disarm();
        });
        marker.Disarm();

        Assert.Empty(Files());
    }

    // ── where the files go ──────────────────────────────────────────────

    [Fact]
    public void DefaultDirectory_ShouldFollowTheEnvironmentVariable_ElseTheLocalApplicationDataOfTheUser()
    {
        Assert.Equal("custom-dir", WebGpuCrashMarker.DefaultDirectory(name => name == "SPEECHLIB_WEBGPU_STATE_DIR" ? "custom-dir" : null));

        var fallback = WebGpuCrashMarker.DefaultDirectory(_ => null);
        Assert.EndsWith(Path.Combine("SpeechLib", "webgpu"), fallback);
        Assert.False(string.IsNullOrEmpty(Path.GetDirectoryName(Path.GetDirectoryName(fallback))));
    }
}
