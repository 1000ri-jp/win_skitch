using System;

namespace WinSkitch;

public sealed record LaunchOptions(bool StartInTray, string? ImagePath)
{
    public static LaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool tray = false;
        string? image = null;
        foreach (string argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("起動引数が空です。", nameof(args));
            if (argument == "--tray") { tray = true; continue; }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"不明な起動オプション: {argument}", nameof(args));
            if (image is not null) throw new ArgumentException("一度に開ける画像は1つです。", nameof(args));
            image = argument;
        }
        return new LaunchOptions(tray, image);
    }
}
