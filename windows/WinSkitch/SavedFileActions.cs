using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WinSkitch;

/// <summary>Opens only supported saved media and its containing folder after a user action.</summary>
public static class SavedFileActions
{
    public static bool CanOpenFile(string filePath) => TryGetMediaPath(filePath, out string fullPath)
        && File.Exists(fullPath);

    public static bool CanOpenFolder(string filePath) => TryGetMediaPath(filePath, out string fullPath)
        && Directory.Exists(Path.GetDirectoryName(fullPath));

    public static void OpenFile(string filePath)
    {
        string fullPath = GetMediaPath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("保存したファイルが見つかりません。", fullPath);
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }

    public static void OpenFolder(string filePath)
    {
        string fullPath = GetMediaPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("保存先のフォルダーが見つかりません。移動・削除されていないか確認してください。");

        // PIDLs keep spaces, Japanese text, and commas out of Explorer's command-line parser.
        // If the file has moved or the shell cannot select it, its existing folder can still open.
        if (File.Exists(fullPath) && TrySelectInFolder(directory, fullPath)) return;
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private static string GetMediaPath(string filePath)
    {
        if (!TryGetMediaPath(filePath, out string fullPath))
            throw new ArgumentException("PNG・JPEG画像またはMP4動画の保存先を開いてください。", nameof(filePath));
        return fullPath;
    }

    private static bool TryGetMediaPath(string filePath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath)) return false;
        try
        {
            fullPath = Path.GetFullPath(filePath);
            // Reject device paths, alternate data streams, and invalid filename characters.
            // History JSON is data and must never become an executable or shell command.
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            string? root = Path.GetPathRoot(fullPath);
            if (root is null) return false;
            string relativePath = fullPath[root.Length..];
            if (relativePath.IndexOfAny(new[] { ':', '"', '<', '>', '|', '*', '?' }) >= 0) return false;
            foreach (char character in fullPath)
                if (char.IsControl(character)) return false;
            return Path.GetExtension(fullPath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".mp4";
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TrySelectInFolder(string directory, string filePath)
    {
        IntPtr folderId = IntPtr.Zero;
        IntPtr fileId = IntPtr.Zero;
        try
        {
            folderId = ILCreateFromPath(directory);
            fileId = ILCreateFromPath(filePath);
            if (folderId == IntPtr.Zero || fileId == IntPtr.Zero) return false;
            IntPtr childId = ILFindLastID(fileId);
            return childId != IntPtr.Zero && SHOpenFolderAndSelectItems(folderId, 1, new[] { childId }, 0) >= 0;
        }
        catch (Exception error) when (error is ExternalException)
        {
            return false;
        }
        finally
        {
            if (fileId != IntPtr.Zero) ILFree(fileId);
            if (folderId != IntPtr.Zero) ILFree(folderId);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ILCreateFromPathW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr itemId);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr itemId);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folderId, uint count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] childIds, uint flags);
}
