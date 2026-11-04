using VoiceType.Uno.Services.Updates;
using Xunit;

namespace VoiceType.Uno.Tests.Updates;

public sealed class AppUpdatesTests
{
    private const string Json = """
    [
      { "tag_name": "uno-android-v1.2.0", "name": "Android", "draft": false, "prerelease": false, "assets": [] },
      { "tag_name": "uno-v2.1.0", "name": "", "draft": true, "prerelease": false, "assets": [] },
      { "tag_name": "uno-v2.0.1-rc", "draft": false, "prerelease": true, "assets": [] },
      { "tag_name": "uno-v2.0.0", "name": "uno-v2.0.0", "body": "Notes", "html_url": "https://github.com/x/y/releases/tag/uno-v2.0.0",
        "draft": false, "prerelease": false,
        "assets": [
          { "name": "VoiceType.Uno-win-x64.zip", "size": 100, "browser_download_url": "https://dl/win.zip" },
          { "name": "VoiceType.Uno-linux-x64.tar.gz", "size": 90, "browser_download_url": "https://dl/linux.tar.gz" },
          { "name": "voicetype-uno_2.0.0_amd64.deb", "size": 80, "browser_download_url": "https://dl/app.deb" },
          { "name": "VoiceType.Uno-macos-arm64.dmg", "size": 70, "browser_download_url": "https://dl/mac.dmg" },
          { "name": "com.voicetype-Signed.apk", "size": 60, "browser_download_url": "https://dl/app.apk" }
        ] },
      { "tag_name": "uno-v1.10.0", "draft": false, "prerelease": false, "assets": [] }
    ]
    """;

    [Theory]
    [InlineData("uno-v1.10.0", 1, 10, 0)]
    [InlineData("v2.0.0", 2, 0, 0)]
    [InlineData("1.0", 1, 0, 0)]
    [InlineData("2.0.0.0", 2, 0, 0)]
    [InlineData("1.9.2+abc", 1, 9, 2)]
    public void Parse_ShouldReadVersions(string text, int major, int minor, int patch) =>
        Assert.Equal(new AppVersion(major, minor, patch), AppVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("uno-v2.0.1-rc")]
    [InlineData("latest")]
    public void Parse_ShouldRejectOthers(string text) => Assert.Null(AppVersion.Parse(text));

    [Fact]
    public void Compare_ShouldBeNumeric() => Assert.True(new AppVersion(1, 10, 0) > new AppVersion(1, 9, 2));

    [Fact]
    public void ParseReleases_ShouldKeepOnlyPublishedUnoReleasesNewestFirst()
    {
        var releases = AppUpdates.ParseReleases(Json);
        Assert.Equal(["uno-v2.0.0", "uno-v1.10.0"], releases.Select(r => r.Tag));
        Assert.Equal(5, releases[0].Assets.Count);
        Assert.Equal("Notes", releases[0].Notes);
    }

    [Fact]
    public void Newer_ShouldBeNullWhenUpToDate()
    {
        var releases = AppUpdates.ParseReleases(Json);
        Assert.Null(AppUpdates.Newer(releases, new AppVersion(2, 0, 0)));
        Assert.Equal("uno-v2.0.0", AppUpdates.Newer(releases, new AppVersion(1, 10, 0))?.Tag);
    }

    [Theory]
    [InlineData(UpdatePlatform.WindowsX64, false, true, UpdateInstallKind.ReplaceInPlace, "VoiceType.Uno-win-x64.zip")]
    [InlineData(UpdatePlatform.WindowsX64, false, false, UpdateInstallKind.OpenPage, null)]
    [InlineData(UpdatePlatform.LinuxX64, false, true, UpdateInstallKind.ReplaceInPlace, "VoiceType.Uno-linux-x64.tar.gz")]
    [InlineData(UpdatePlatform.LinuxX64, true, false, UpdateInstallKind.OpenPackage, "voicetype-uno_2.0.0_amd64.deb")]
    [InlineData(UpdatePlatform.MacArm64, false, false, UpdateInstallKind.OpenPackage, "VoiceType.Uno-macos-arm64.dmg")]
    [InlineData(UpdatePlatform.Android, false, false, UpdateInstallKind.OpenPage, null)]
    public void Plan_ShouldPickTheWayOfThePlatform(UpdatePlatform platform, bool deb, bool writable, UpdateInstallKind kind, string? asset)
    {
        var release = AppUpdates.ParseReleases(Json)[0];
        var plan = AppUpdates.Plan(release, platform, new AppVersion(1, 10, 0), storePackage: false, writable, deb);
        Assert.NotNull(plan);
        Assert.Equal(kind, plan!.Kind);
        Assert.Equal(asset, plan.Asset?.Name);
    }

    [Fact]
    public void Plan_StorePackageAndIos_ShouldOfferNothing()
    {
        var release = AppUpdates.ParseReleases(Json)[0];
        Assert.Null(AppUpdates.Plan(release, UpdatePlatform.WindowsX64, new AppVersion(1, 10, 0), storePackage: true, true));
        Assert.Null(AppUpdates.Plan(release, UpdatePlatform.Ios, new AppVersion(1, 10, 0), false, true));
    }

    [Fact]
    public void Plan_DeveloperBuild_ShouldOnlyOpenThePage()
    {
        var release = AppUpdates.ParseReleases(Json)[0];
        Assert.Equal(UpdateInstallKind.OpenPage, AppUpdates.Plan(release, UpdatePlatform.WindowsX64, AppUpdates.DeveloperBuild, false, true)!.Kind);
    }

    [Fact]
    public void IsCheckDue_ShouldWaitTheInterval()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.True(AppUpdates.IsCheckDue(null, now));
        Assert.False(AppUpdates.IsCheckDue(now.AddHours(-2), now));
        Assert.True(AppUpdates.IsCheckDue(now.AddHours(-21), now));
        Assert.True(AppUpdates.IsCheckDue(now.AddDays(3), now)); // clock moved back
    }

    [Fact]
    public void Scripts_ShouldQuotePathsAndWaitForTheProcess()
    {
        var ps = AppUpdates.WindowsReplaceScript(42, @"C:\T\it's\staging", @"C:\App", @"C:\App\VoiceType.Uno.exe");
        Assert.Contains("Get-Process -Id 42", ps);
        Assert.Contains(@"'C:\T\it''s\staging'", ps);
        Assert.Contains("robocopy", ps);
        var sh = AppUpdates.UnixReplaceScript(42, "/tmp/a'b/staging", "/home/u/vt", "/home/u/vt/VoiceType.Uno");
        Assert.Contains("kill -0 42", sh);
        Assert.Contains(@"'/tmp/a'\''b/staging'/.", sh);
        Assert.DoesNotContain("\r", sh);
    }
}
