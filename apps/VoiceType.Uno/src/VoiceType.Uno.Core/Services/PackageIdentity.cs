using System.Runtime.InteropServices;

namespace VoiceType.Uno.Services;

/// <summary>
/// The MSIX package the process runs in (the Microsoft Store build of the WinAppSDK head), read with
/// <c>GetCurrentPackageFamilyName</c> so the UI-free core needs no WinRT. Null when unpackaged or not on Windows.
/// </summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private static readonly Lazy<string?> s_familyName = new(ReadFamilyName);

    /// <summary>The package family name, e.g. <c>DimQ1.VoiceType_310ax279fjzmt</c>, or null.</summary>
    public static string? FamilyName => s_familyName.Value;

    public static bool IsPackaged => FamilyName is not null;

    /// <summary>
    /// The real folder the packaged app's writes to <c>%LOCALAPPDATA%</c> go to:
    /// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local</c>. Explorer and other apps see files only there.
    /// </summary>
    public static string? RedirectedLocalAppData(string localAppData) =>
        FamilyName is { } family ? Path.Combine(localAppData, "Packages", family, "LocalCache", "Local") : null;

    private static string? ReadFamilyName()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
            return null;
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer)
                return null; // APPMODEL_ERROR_NO_PACKAGE: unpackaged
            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // DllImport, not LibraryImport: the source generator needs AllowUnsafeBlocks, which the core does not enable for one call.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
