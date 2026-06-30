using System;
using System.Runtime.InteropServices;

namespace VoiceType.Services;

/// <summary>Native Windows folder browser dialog via SHBrowseForFolder.</summary>
internal static class FolderBrowser
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, IntPtr pszPath);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    /// <summary>Show folder browser dialog. Returns selected path or null if cancelled.</summary>
    public static string? Show(string title, string initialPath)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = GetActiveWindow(),
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            pszDisplayName = new string('\0', 260)
        };

        var pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero)
            return null;

        var pathPtr = Marshal.AllocCoTaskMem(520);
        try
        {
            if (SHGetPathFromIDList(pidl, pathPtr))
                return Marshal.PtrToStringUni(pathPtr);
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
            Marshal.FreeCoTaskMem(pidl);
        }
    }
}
