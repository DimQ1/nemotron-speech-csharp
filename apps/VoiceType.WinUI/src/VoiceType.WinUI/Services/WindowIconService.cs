using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace VoiceType.WinUI.Services;

/// <summary>Applies the VoiceType icon to native window captions and taskbar buttons.</summary>
public sealed class WindowIconService : IDisposable
{
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint DIB_RGB_COLORS = 0;
    private const uint WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0;
    private const nint ICON_BIG = 1;

    private nint _normalBigIcon;
    private nint _normalSmallIcon;
    private nint _activeBigIcon;
    private nint _activeSmallIcon;

    public WindowIconService()
    {
        var iconPath = ResolveIconPath();
        if (iconPath is null)
        {
            System.Diagnostics.Debug.WriteLine("WindowIconService: VoiceType.ico was not found.");
            return;
        }

        _normalBigIcon = LoadIcon(iconPath, 32);
        _normalSmallIcon = LoadIcon(iconPath, 16);
        _activeBigIcon = CreateRedIcon(_normalBigIcon);
        _activeSmallIcon = CreateRedIcon(_normalSmallIcon);
    }

    /// <summary>Sets the normal or active icon for a window and its taskbar button.</summary>
    public void SetWindowIcon(nint hwnd, AppWindow? appWindow, bool isTextInjectionActive)
    {
        if (hwnd == nint.Zero && appWindow is null)
            return;

        var bigIcon = isTextInjectionActive && _activeBigIcon != nint.Zero
            ? _activeBigIcon
            : _normalBigIcon;
        var smallIcon = isTextInjectionActive && _activeSmallIcon != nint.Zero
            ? _activeSmallIcon
            : _normalSmallIcon;

        if (appWindow is not null && bigIcon != nint.Zero)
            appWindow.SetIcon(Win32Interop.GetIconIdFromIcon(bigIcon));

        if (hwnd == nint.Zero)
            return;

        if (bigIcon != nint.Zero)
            SendMessage(hwnd, WM_SETICON, ICON_BIG, bigIcon);
        if (smallIcon != nint.Zero)
            SendMessage(hwnd, WM_SETICON, ICON_SMALL, smallIcon);
    }

    public void Dispose()
    {
        DestroyIcon(ref _normalBigIcon);
        DestroyIcon(ref _normalSmallIcon);
        DestroyIcon(ref _activeBigIcon);
        DestroyIcon(ref _activeSmallIcon);
        GC.SuppressFinalize(this);
    }

    private static void DestroyIcon(ref nint icon)
    {
        if (icon == nint.Zero)
            return;

        DestroyIcon(icon);
        icon = nint.Zero;
    }

    private static string? ResolveIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceType.ico"),
            Path.Combine(AppContext.BaseDirectory, "VoiceType.ico")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static nint LoadIcon(string path, int size) =>
        LoadImage(nint.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);

    private static nint CreateRedIcon(nint sourceIcon)
    {
        if (sourceIcon == nint.Zero || !GetIconInfo(sourceIcon, out var sourceInfo))
            return nint.Zero;

        nint redBitmap = nint.Zero;
        nint hdc = nint.Zero;
        try
        {
            if (sourceInfo.hbmColor == nint.Zero
                || GetObject(sourceInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var sourceBitmap) == 0
                || sourceBitmap.bmWidth <= 0
                || sourceBitmap.bmHeight <= 0)
            {
                return nint.Zero;
            }

            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = sourceBitmap.bmWidth,
                    biHeight = -sourceBitmap.bmHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                    biSizeImage = sourceBitmap.bmWidth * sourceBitmap.bmHeight * 4
                }
            };

            var pixelData = new byte[bitmapInfo.bmiHeader.biSizeImage];
            hdc = GetDC(nint.Zero);
            if (hdc == nint.Zero
                || GetDIBits(
                    hdc,
                    sourceInfo.hbmColor,
                    0,
                    (uint)sourceBitmap.bmHeight,
                    pixelData,
                    ref bitmapInfo,
                    DIB_RGB_COLORS) == 0)
            {
                return nint.Zero;
            }

            ColorizeBluePixelsToRed(pixelData);

            redBitmap = CreateDIBSection(
                hdc,
                ref bitmapInfo,
                DIB_RGB_COLORS,
                out var redBits,
                nint.Zero,
                0);
            if (redBitmap == nint.Zero || redBits == nint.Zero)
                return nint.Zero;

            Marshal.Copy(pixelData, 0, redBits, pixelData.Length);
            var redIconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = redBitmap,
                hbmMask = sourceInfo.hbmMask
            };

            return CreateIconIndirect(ref redIconInfo);
        }
        finally
        {
            if (redBitmap != nint.Zero)
                DeleteObject(redBitmap);
            if (hdc != nint.Zero)
                ReleaseDC(nint.Zero, hdc);
            if (sourceInfo.hbmColor != nint.Zero)
                DeleteObject(sourceInfo.hbmColor);
            if (sourceInfo.hbmMask != nint.Zero)
                DeleteObject(sourceInfo.hbmMask);
        }
    }

    private static void ColorizeBluePixelsToRed(byte[] pixelData)
    {
        for (var index = 0; index < pixelData.Length; index += 4)
        {
            var blue = pixelData[index];
            var green = pixelData[index + 1];
            var red = pixelData[index + 2];

            if (pixelData[index + 3] == 0 || blue <= red + 16 || blue < green)
                continue;

            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            var saturation = maximum == 0 ? 0 : (maximum - minimum) / (double)maximum;
            var nonRedChannel = (byte)Math.Clamp(
                (int)Math.Round(maximum * (1 - saturation)),
                0,
                255);

            pixelData[index] = nonRedChannel;
            pixelData[index + 1] = nonRedChannel;
            pixelData[index + 2] = (byte)maximum;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(nint hObject, int count, out BITMAP structure);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint hdc,
        nint hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }
}