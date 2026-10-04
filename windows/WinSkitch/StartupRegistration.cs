using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WinSkitch;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinSkitch";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command)
                    return false;

                // A moved executable needs to be registered again at its new location.
                return string.Equals(command, BuildCommand(CurrentExecutablePath()), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string command = BuildCommand(CurrentExecutablePath());
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                    ?? throw new InvalidOperationException("Windows の自動起動設定を開けませんでした。");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException($"Windows の自動起動設定を変更できませんでした。{error.Message}", error);
        }
    }

    public static string BuildCommand(string executablePath)
    {
        string command = $"\"{ValidateExecutablePath(executablePath)}\" --tray";
        if (command.Length > 260)
            throw new ArgumentException("Windows の自動起動に登録できる長さを超えています。WinSkitch.exe を短いパスのフォルダーに移してから設定してください。", nameof(executablePath));
        return command;
    }

    // Deterministic path selection keeps tests independent of the machine's Run key
    // and accounts for development launches through `dotnet WinSkitch.dll`.
    public static string ResolveExecutablePath(string? processPath, string appBaseDirectory, bool appHostExists)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("WinSkitch の実行ファイルの場所を取得できませんでした。");

        string executablePath = ValidateExecutablePath(processPath);
        if (!string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return executablePath;

        ArgumentNullException.ThrowIfNull(appBaseDirectory);
        if (!Path.IsPathFullyQualified(appBaseDirectory))
            throw new ArgumentException("アプリのフォルダーには絶対パスが必要です。", nameof(appBaseDirectory));
        string appHostPath = ValidateExecutablePath(Path.Combine(appBaseDirectory, "WinSkitch.exe"));
        if (!appHostExists)
            throw new InvalidOperationException("自動起動を設定するには、ビルドまたは配布済みの WinSkitch.exe を起動してください。");
        return appHostPath;
    }

    private static string CurrentExecutablePath()
    {
        // Unlike Assembly.Location, ProcessPath stays at the user's original
        // executable location when the single-file build extracts its runtime.
        string appHostPath = Path.Combine(AppContext.BaseDirectory, "WinSkitch.exe");
        return ResolveExecutablePath(Environment.ProcessPath, AppContext.BaseDirectory, File.Exists(appHostPath));
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0 ||
            !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("実行ファイルには改行や引用符を含まない絶対パスが必要です。", nameof(executablePath));
        return Path.GetFullPath(executablePath);
    }
}
