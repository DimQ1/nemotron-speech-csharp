using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Controls Windows taskbar button state: injection dot, overlay icon, and progress bar.
/// Used to show the microphone state while recognition is active.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    public enum RecordingOverlayMode
    {
        CaptureDot,
        InjectionText
    }

    private nint _hwnd;
    private ITaskbarList3? _taskbarList;
    private nint _overlayIcon;
    private bool _isIndicating;
    private bool _disposed;

    /// <summary>Initialize with the main window handle. Call once after window creation.</summary>
    public void Initialize(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var hr = CoCreateInstance(
                typeof(TaskbarList).GUID,
                nint.Zero,
                CLSCTX_ALL,
                typeof(ITaskbarList3).GUID,
                out var obj);
            if (hr == 0 && obj is not null)
            {
                _taskbarList = (ITaskbarList3)obj;
                _taskbarList.HrInit();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService init failed: {ex.Message}");
        }
    }

    /// <summary>Show the microphone indicator for the current recognition state.</summary>
    public void StartRecordingIndicator(bool isMuted, RecordingOverlayMode overlayMode)
    {
        if (_taskbarList is null || _hwnd == nint.Zero) return;
        if (_isIndicating)
        {
            UpdateRecordingIndicator(isMuted, overlayMode);
            return;
        }

        _isIndicating = true;

        try
        {
            SetRecordingOverlay(isMuted, overlayMode);
            SetRecognitionProgress();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService StartRecordingIndicator failed: {ex.Message}");
        }
    }

    /// <summary>Update the microphone overlay for the current recognition state.</summary>
    public void UpdateRecordingIndicator(bool isMuted, RecordingOverlayMode overlayMode)
    {
        if (_taskbarList is null || _hwnd == nint.Zero || !_isIndicating) return;

        try
        {
            SetRecordingOverlay(isMuted, overlayMode);
            SetRecognitionProgress();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService UpdateRecordingIndicator failed: {ex.Message}");
        }
    }

    /// <summary>Hide the microphone indicator.</summary>
    public void StopRecordingIndicator()
    {
        if (_taskbarList is null || _hwnd == nint.Zero || !_isIndicating) return;
        _isIndicating = false;

        try
        {
            _taskbarList.SetOverlayIcon(_hwnd, nint.Zero, null);
            _taskbarList.SetProgressState(_hwnd, TBPF_NOPROGRESS);

            if (_overlayIcon != nint.Zero)
            {
                DestroyIcon(_overlayIcon);
                _overlayIcon = nint.Zero;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarService StopRecordingIndicator failed: {ex.Message}");
        }
    }

    /// <summary>Clean up resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        StopRecordingIndicator();
        if (_overlayIcon != nint.Zero)
        {
            DestroyIcon(_overlayIcon);
            _overlayIcon = nint.Zero;
        }
        if (_taskbarList is not null)
        {
            Marshal.ReleaseComObject(_taskbarList);
            _taskbarList = null;
        }
        _disposed = true;
    }

    private void SetRecordingOverlay(bool isMuted, RecordingOverlayMode overlayMode)
    {
        var newIcon = overlayMode == RecordingOverlayMode.InjectionText
            ? CreateTextOverlayIcon()
            : CreateRedDotOverlayIcon();
        if (newIcon == nint.Zero)
            return;

        try
        {
            _taskbarList!.SetOverlayIcon(
                _hwnd,
                newIcon,
                overlayMode == RecordingOverlayMode.InjectionText
                    ? "Transcribing and typing..."
                    : isMuted ? "Microphone muted" : "Listening...");

            var previousIcon = _overlayIcon;
            _overlayIcon = newIcon;
            if (previousIcon != nint.Zero)
                DestroyIcon(previousIcon);
        }
        catch
        {
            DestroyIcon(newIcon);
            throw;
        }
    }

    private void SetRecognitionProgress()
    {
        _taskbarList!.SetProgressState(
            _hwnd,
            TBPF_INDETERMINATE);
    }

    private static nint CreateRedDotOverlayIcon()
    {
        const int size = 16;
        var hdcScreen = GetDC(nint.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        nint hbmColor = nint.Zero;
        nint hbmMask = nint.Zero;

        try
        {
            if (hdcScreen == nint.Zero || hdcMem == nint.Zero)
                return nint.Zero;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                    biSizeImage = size * size * 4
                }
            };

            hbmColor = CreateDIBSection(hdcMem, ref bmi, 0, out var bits, nint.Zero, 0);
            if (hbmColor == nint.Zero || bits == nint.Zero)
                return nint.Zero;

            var maskStride = ((size + 31) / 32) * 4;
            var maskData = new byte[maskStride * size];
            var maskHandle = GCHandle.Alloc(maskData, GCHandleType.Pinned);
            try
            {
                hbmMask = CreateBitmap(size, size, 1, 1, maskHandle.AddrOfPinnedObject());
            }
            finally
            {
                maskHandle.Free();
            }

            if (hbmMask == nint.Zero)
                return nint.Zero;

            var pixelData = new byte[size * size * 4];
            for (var pixelY = 0; pixelY < size; pixelY++)
            {
                for (var pixelX = 0; pixelX < size; pixelX++)
                {
                    var distanceX = pixelX - 7.5;
                    var distanceY = pixelY - 7.5;
                    if (distanceX * distanceX + distanceY * distanceY > 30.25)
                        continue;

                    var index = (pixelY * size + pixelX) * 4;
                    pixelData[index + 0] = 53;
                    pixelData[index + 1] = 57;
                    pixelData[index + 2] = 229;
                    pixelData[index + 3] = 255;
                }
            }

            Marshal.Copy(pixelData, 0, bits, pixelData.Length);
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = hbmColor,
                hbmMask = hbmMask
            };

            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (hbmColor != nint.Zero)
                DeleteObject(hbmColor);
            if (hbmMask != nint.Zero)
                DeleteObject(hbmMask);
            if (hdcMem != nint.Zero)
                DeleteDC(hdcMem);
            if (hdcScreen != nint.Zero)
                ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    private static nint CreateTextOverlayIcon()
    {
        const int size = 16;
        var hdcScreen = GetDC(nint.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        nint hbmColor = nint.Zero;
        nint hbmMask = nint.Zero;

        try
        {
            if (hdcScreen == nint.Zero || hdcMem == nint.Zero)
                return nint.Zero;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                    biSizeImage = size * size * 4
                }
            };

            hbmColor = CreateDIBSection(hdcMem, ref bmi, 0, out var bits, nint.Zero, 0);
            if (hbmColor == nint.Zero || bits == nint.Zero)
                return nint.Zero;

            var maskStride = ((size + 31) / 32) * 4;
            var maskData = new byte[maskStride * size];
            var maskHandle = GCHandle.Alloc(maskData, GCHandleType.Pinned);
            try
            {
                hbmMask = CreateBitmap(size, size, 1, 1, maskHandle.AddrOfPinnedObject());
            }
            finally
            {
                maskHandle.Free();
            }

            if (hbmMask == nint.Zero)
                return nint.Zero;

            var pixelData = new byte[size * size * 4];
            for (var pixelY = 0; pixelY < size; pixelY++)
            {
                for (var pixelX = 0; pixelX < size; pixelX++)
                {
                    var isHorizontal = pixelY is >= 2 and <= 4 && pixelX is >= 2 and <= 13;
                    var isVertical = pixelX is >= 7 and <= 9 && pixelY is >= 4 and <= 13;
                    if (!isHorizontal && !isVertical)
                        continue;

                    var index = (pixelY * size + pixelX) * 4;
                    pixelData[index + 0] = 53;
                    pixelData[index + 1] = 57;
                    pixelData[index + 2] = 229;
                    pixelData[index + 3] = 255;
                }
            }

            Marshal.Copy(pixelData, 0, bits, pixelData.Length);
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = hbmColor,
                hbmMask = hbmMask
            };

            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (hbmColor != nint.Zero)
                DeleteObject(hbmColor);
            if (hbmMask != nint.Zero)
                DeleteObject(hbmMask);
            if (hdcMem != nint.Zero)
                DeleteDC(hdcMem);
            if (hdcScreen != nint.Zero)
                ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    // ---- Icon creation (legacy microphone helper kept for compatibility) using raw Win32 ----

    private static nint CreateMicrophoneOverlayIcon(bool isMuted, bool isActivelyInjecting)
    {
        // Create a simple 16x16 icon using raw Win32 GDI — no System.Drawing dependency.
        // Keep the overlay as a microphone silhouette; the color communicates its state.

        const int size = 16;
        var hdcScreen = GetDC(nint.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);

        // Color bitmap (32bpp with alpha)
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
                biSizeImage = size * size * 4
            }
        };

        var hbmColor = CreateDIBSection(hdcMem, ref bmi, 0, out var bits, nint.Zero, 0);
        var maskStride = ((size + 31) / 32) * 4;
        var maskData = new byte[maskStride * size];
        var maskHandle = GCHandle.Alloc(maskData, GCHandleType.Pinned);
        nint hbmMask;
        try
        {
            hbmMask = CreateBitmap(size, size, 1, 1, maskHandle.AddrOfPinnedObject());
        }
        finally
        {
            maskHandle.Free();
        }

        var red = isActivelyInjecting ? (byte)229 : isMuted ? (byte)245 : (byte)0;
        var green = isActivelyInjecting ? (byte)57 : isMuted ? (byte)158 : (byte)120;
        var blue = isActivelyInjecting ? (byte)53 : isMuted ? (byte)11 : (byte)212;

        // Fill color bitmap: microphone silhouette (BGRA, premultiplied not needed for overlay)
        var pixelData = new byte[size * size * 4];
        for (var pixelY = 0; pixelY < size; pixelY++)
        {
            for (var pixelX = 0; pixelX < size; pixelX++)
            {
                var idx = (pixelY * size + pixelX) * 4;

                if (IsMicrophonePixel(pixelX, pixelY))
                {
                    pixelData[idx + 0] = blue;
                    pixelData[idx + 1] = green;
                    pixelData[idx + 2] = red;
                    pixelData[idx + 3] = 255;
                }
            }
        }
        Marshal.Copy(pixelData, 0, bits, pixelData.Length);

        // Keep the monochrome mask fully opaque and let the 32bpp alpha channel provide
        // transparency around the microphone. Do not add a circular badge.

        var ii = new ICONINFO
        {
            fIcon = true,
            hbmColor = hbmColor,
            hbmMask = hbmMask
        };

        var hIcon = CreateIconIndirect(ref ii);

        // Cleanup
        DeleteObject(hbmColor);
        DeleteObject(hbmMask);
        DeleteDC(hdcMem);
        ReleaseDC(nint.Zero, hdcScreen);

        return hIcon;
    }

    private static bool IsMicrophonePixel(int pixelX, int pixelY)
    {
        var capsule = IsRoundedRectangle(pixelX, pixelY, 5, 2, 10, 10, 3);
        var stem = pixelX is >= 7 and <= 8 && pixelY is >= 10 and <= 13;
        var baseLine = IsRoundedRectangle(pixelX, pixelY, 4, 13, 11, 15, 1);

        var distanceX = pixelX - 8;
        var distanceY = pixelY - 8;
        var distanceSquared = distanceX * distanceX + distanceY * distanceY;
        var outerArc = pixelY >= 5 && distanceSquared is >= 25 and <= 36;

        return capsule || stem || baseLine || outerArc;
    }

    private static bool IsRoundedRectangle(
        int pixelX,
        int pixelY,
        int left,
        int top,
        int right,
        int bottom,
        int radius)
    {
        if (pixelX < left || pixelX > right || pixelY < top || pixelY > bottom)
            return false;

        var nearestX = Math.Clamp(pixelX, left + radius, right - radius);
        var nearestY = Math.Clamp(pixelY, top + radius, bottom - radius);
        var distanceX = pixelX - nearestX;
        var distanceY = pixelY - nearestY;
        return distanceX * distanceX + distanceY * distanceY <= radius * radius;
    }

    // ---- Win32 / COM interop ----

    private const uint CLSCTX_ALL = 0x17;
    private const int TBPF_NOPROGRESS = 0x0;
    private const int TBPF_INDETERMINATE = 0x1;

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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO ii);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(nint hwnd, int tbpFlags);
        void RegisterTab(nint hwndTab, nint hwndMDI);
        void UnregisterTab(nint hwndTab);
        void SetTabOrder(nint hwndTab, nint hwndInsertBefore);
        void SetTabActive(nint hwndTab, nint hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarUpdateButtons(nint hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButton);
        void ThumbBarSetImageList(nint hwnd, nint himl);
        void SetOverlayIcon(nint hwnd, nint hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        void SetThumbnailClip(nint hwnd, nint prcClip);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }
}
