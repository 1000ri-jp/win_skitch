using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinSkitch;

internal sealed record UpdateInstallPlan(string TargetPath, string StagedPath, string CandidatePath,
    string BackupPath, string Version, string Runtime, string Sha256, int ParentId, long ParentStarted,
    string ReadyEvent, bool StartInTray, string? BundleDirectory);

/// <summary>The downloaded EXE acts as a helper while the resident releases its files.</summary>
internal static class UpdateInstaller
{
    private const string CandidatePrefix = ".winskitch-update-";
    private const string ReadyPrefix = @"Local\WinSkitch.Update.";
    internal const string StartupReadyVariable = "WINSKITCH_UPDATE_STARTUP_READY";
    internal static async Task StartAsync(PreparedUpdate update, string targetPath, CancellationToken cancellationToken)
    {
        using Process parent = Process.GetCurrentProcess();
        UpdateInstallPlan plan = await PrepareAsync(update, targetPath, parent, cancellationToken).ConfigureAwait(false);
        string manifest = Path.Combine(Path.GetDirectoryName(update.ExecutablePath)!, "install.json");
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, plan.ReadyEvent);
        Process? helper = null;
        try
        {
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(plan), cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(plan.StagedPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(plan.TargetPath)!
            };
            start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(manifest);
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(plan.StagedPath)!, "runtime");
            helper = Process.Start(start) ?? throw new IOException("更新用のプロセスを起動できませんでした。");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(ready,
                (_, timedOut) => completion.TrySetResult(!timedOut), null, 60000, true);
            try
            {
                if (!await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
                    throw new IOException("更新用のプロセスを準備できませんでした。アプリは現在のバージョンのままです。");
            }
            finally { wait.Unregister(null); }
        }
        catch
        {
            // The resident is still alive, so a failed helper must never proceed later.
            if (helper is not null)
            {
                try { if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync().ConfigureAwait(false); } }
                catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            AppUpdates.TryDelete(plan.CandidatePath);
            throw;
        }
        finally { helper?.Dispose(); }
    }

    internal static async Task<UpdateInstallPlan> PrepareAsync(PreparedUpdate update, string targetPath,
        Process parent, CancellationToken cancellationToken)
    {
        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(targetPath)) throw new FileNotFoundException("更新するWinSkitch.exeがありません。", targetPath);
        string candidate = Path.Combine(Path.GetDirectoryName(targetPath)!, $"{CandidatePrefix}{Guid.NewGuid():N}.exe");
        var plan = new UpdateInstallPlan(targetPath, Path.GetFullPath(update.ExecutablePath), candidate,
            targetPath + ".previous", update.Release.Version.ToString(), AppUpdates.CurrentRuntime,
            update.ExecutableSha256, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            ReadyPrefix + Guid.NewGuid().ToString("N"), false,
            Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        ValidatePlan(plan);
        try
        {
            // Check write access before closing the editor. Stage alongside the target for an atomic replacement.
            using (var input = File.OpenRead(plan.StagedPath))
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await ValidateCandidateAsync(plan, cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch { AppUpdates.TryDelete(candidate); throw; }
    }

    internal static void ValidatePlan(UpdateInstallPlan plan, string? helperPath = null)
    {
        foreach (string path in new[] { plan.TargetPath, plan.StagedPath, plan.CandidatePath, plan.BackupPath })
            if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path))
                throw new InvalidDataException("更新先には正規化した絶対パスが必要です。");
        string name = Path.GetFileName(plan.CandidatePath);
        if (!string.Equals(Path.GetFileName(plan.TargetPath), "WinSkitch.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(plan.StagedPath), "WinSkitch.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(plan.TargetPath, plan.StagedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(plan.CandidatePath), Path.GetDirectoryName(plan.TargetPath), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(CandidatePrefix, StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[CandidatePrefix.Length..^4], "N", out _) || plan.BackupPath != plan.TargetPath + ".previous" ||
            !Version.TryParse(plan.Version, out _) || plan.Runtime is not ("win-x64" or "win-arm64") ||
            !AppUpdates.IsSha256(plan.Sha256) || plan.ParentId <= 0 || plan.ParentStarted <= 0 ||
            !plan.ReadyEvent.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(plan.ReadyEvent[ReadyPrefix.Length..], "N", out _))
            throw new InvalidDataException("更新の実行情報が正しくありません。");
        if (helperPath is not null && !string.Equals(plan.StagedPath, Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("検証した更新EXEから実行してください。");
    }

    internal static async Task ApplyAsync(string manifestPath, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        UpdateInstallPlan plan = JsonSerializer.Deserialize<UpdateInstallPlan>(json)
            ?? throw new InvalidDataException("更新の実行情報がありません。");
        ValidatePlan(plan, Environment.ProcessPath);
        await ValidateCandidateAsync(plan, cancellationToken).ConfigureAwait(false);
        using (var ready = EventWaitHandle.OpenExisting(plan.ReadyEvent)) ready.Set();
        await WaitForParentAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            await InstallAndRestartAsync(plan, () => RestartAsync(plan, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // InstallAsync restores the previous binary if restarting the new one fails.
            try { using Process process = StartExecutable(plan); } catch (Exception) { }
            throw;
        }
    }

    internal static async Task WaitForParentAsync(UpdateInstallPlan plan, CancellationToken cancellationToken)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            // A recycled PID must never cause the updater to wait on or stop another application.
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    private static Process StartExecutable(UpdateInstallPlan plan, string? readyEvent = null)
    {
        var start = new ProcessStartInfo(plan.TargetPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(plan.TargetPath)!
        };
        if (plan.StartInTray) { start.ArgumentList.Add("--tray"); start.WindowStyle = ProcessWindowStyle.Hidden; }
        if (plan.BundleDirectory is null) start.Environment.Remove("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        else start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = plan.BundleDirectory;
        if (readyEvent is null) start.Environment.Remove(StartupReadyVariable);
        else start.Environment[StartupReadyVariable] = readyEvent;
        return Process.Start(start) ?? throw new IOException("更新したアプリを起動できませんでした。");
    }

    private static async Task RestartAsync(UpdateInstallPlan plan, CancellationToken cancellationToken)
    {
        string name = @"Local\WinSkitch.Update.Started." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        using Process process = StartExecutable(plan, name);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(ready,
            (_, timedOut) => completion.TrySetResult(!timedOut), null, 60000, true);
        try
        {
            Task exited = process.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(completion.Task, exited).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (exited.IsCompleted && !completion.Task.IsCompleted)
                await Task.WhenAny(completion.Task, Task.Delay(500, cancellationToken)).ConfigureAwait(false);
            // Startup confirmation wins even when a secondary instance exits immediately afterward.
            if (!(completion.Task.IsCompletedSuccessfully && completion.Task.Result))
                throw new IOException("更新したアプリの起動を確認できませんでした。旧版に戻します。");
        }
        catch
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); }
            throw;
        }
        finally { wait.Unregister(null); }
    }

    internal static Task InstallAsync(UpdateInstallPlan plan, Action restart, CancellationToken cancellationToken) =>
        InstallAndRestartAsync(plan, () => { restart(); return Task.CompletedTask; }, cancellationToken);

    private static async Task InstallAndRestartAsync(UpdateInstallPlan plan, Func<Task> restart, CancellationToken cancellationToken)
    {
        ValidatePlan(plan);
        bool replaced = false;
        try
        {
            await ValidateCandidateAsync(plan, cancellationToken).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Replace(plan.CandidatePath, plan.TargetPath, plan.BackupPath); replaced = true; break; }
                catch (IOException) when (attempt < 30) { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
            }
            await restart().ConfigureAwait(false);
            AppUpdates.TryDelete(plan.BackupPath);
        }
        catch
        {
            if (replaced) File.Replace(plan.BackupPath, plan.TargetPath, null);
            throw;
        }
        finally { AppUpdates.TryDelete(plan.CandidatePath); }
    }

    private static async Task ValidateCandidateAsync(UpdateInstallPlan plan, CancellationToken cancellationToken)
    {
        if (await AppUpdates.FileHashAsync(plan.CandidatePath, cancellationToken).ConfigureAwait(false) != plan.Sha256)
            throw new InvalidDataException("更新EXEのSHA-256が一致しません。");
        AppUpdates.ValidateExecutable(plan.CandidatePath, Version.Parse(plan.Version), plan.Runtime);
    }
}
