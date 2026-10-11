using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinSkitch;

internal static class UpdatesTests
{
    private static string _output = "";
    private static string FixtureExe => Path.Combine(AppContext.BaseDirectory, "WinSkitch.exe");
    private static Version FixtureVersion
    {
        get { var info = FileVersionInfo.GetVersionInfo(FixtureExe); return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart); }
    }

    internal static void RunAll(Action<string, Action> run, string output)
    {
        _output = output;
        run("updates select only newer stable releases and the correct PC architecture", ReleaseSelection);
        run("update metadata rejects foreign URLs, invalid hashes, and duplicate assets", InvalidReleaseMetadata);
        run("update downloads verify ZIP hash, executable version, and architecture without extracting other entries", Download);
        run("corrupt, truncated, oversized, canceled, and invalid update downloads never produce an executable", InvalidDownloads);
        run("update installation atomically replaces the executable and rolls back when restarting fails", InstallAndRollback);
        run("locked or modified update executables preserve the original app", LockedAndTampered);
        run("update plans reject unexpected paths and wait for the original process to exit", InstallPlansAndParent);
        run("a real update helper confirms startup and rolls back a crashed new process", HelperRoundTrip);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static void Await(Task task) => task.GetAwaiter().GetResult();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string DirectoryFor(string name)
    {
        string directory = Path.Combine(_output, "updates", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }

    private static string ReleaseJson(string tag, string hash, long size, string runtime = "win-x64", bool draft = false, bool prerelease = false)
    {
        string name = $"WinSkitch-{tag}-{runtime}.zip";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { new { name, size, state = "uploaded", digest = "sha256:" + hash,
                browser_download_url = $"https://github.com/{AppUpdates.Repository}/releases/download/{tag}/{name}" } }
        });
    }

    private static AppRelease? Parse(string json, Version? current = null, string runtime = "win-x64")
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return AppUpdates.ParseRelease(document.RootElement, current ?? new Version(0, 0, 0), runtime);
    }

    private static void ReleaseSelection()
    {
        string hash = new('a', 64);
        string json = ReleaseJson("v1.2.3", hash, 100);
        Check(Parse(json, new Version(1, 2, 2))?.Version == new Version(1, 2, 3), "a newer patch version should be offered");
        Check(Parse(json, new Version(1, 2, 3)) is null && Parse(json, new Version(2, 0, 0)) is null, "equal and older versions must not downgrade the app");
        Check(Parse(ReleaseJson("v1.2.3", hash, 100, draft: true)) is null, "draft releases are not updates");
        Check(Parse(ReleaseJson("v1.2.3", hash, 100, prerelease: true)) is null, "prereleases are not automatic updates");
        foreach (string tag in new[] { "v2.0.0-beta", "v1.2", "v1.2.3.4", "v1.+2.3", "../1.2.3" })
            Check(Parse(ReleaseJson(tag, hash, 100)) is null, "nonstandard release tags must not become executable paths");
        Check(Parse(json, runtime: "win-arm64") is null, "x64 packages must not be selected for ARM64");
        Check(Parse(ReleaseJson("v1.2.3", hash, 100, "win-arm64"), runtime: "win-arm64")?.AssetName.EndsWith("win-arm64.zip") == true,
            "ARM64 processes must select the ARM64 asset");
    }

    private static void InvalidReleaseMetadata()
    {
        string json = ReleaseJson("v1.2.3", new string('a', 64), 100);
        foreach (string invalid in new[]
        {
            json.Replace("https://github.com/", "https://example.com/"),
            json.Replace("sha256:", "md5:"), json.Replace(new string('a', 64), "bad"),
            json.Replace("\"size\":100", "\"size\":999999999"), json.Replace("uploaded", "new")
        }) ExpectFailure(() => Parse(invalid));
        using JsonDocument document = JsonDocument.Parse(json);
        string asset = document.RootElement.GetProperty("assets")[0].GetRawText();
        ExpectFailure(() => Parse($"{{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":false,\"assets\":[{asset},{asset}]}}"));
    }

    private static byte[] Archive(string? extra = null, bool duplicate = false, bool omitExe = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (!omitExe)
            {
                using (Stream stream = zip.CreateEntry("WinSkitch.exe").Open()) stream.Write(File.ReadAllBytes(FixtureExe));
                if (duplicate) using (Stream stream = zip.CreateEntry("WinSkitch.exe").Open()) stream.Write(File.ReadAllBytes(FixtureExe));
            }
            if (extra is not null) using (var writer = new StreamWriter(zip.CreateEntry(extra).Open())) writer.Write("not an executable");
        }
        return memory.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    private static PreparedUpdate GetUpdate(byte[] bytes, string directory, string runtime = "win-x64", Version? version = null)
    {
        string metadata = ReleaseJson("v" + (version ?? FixtureVersion), Hash(bytes), bytes.Length, runtime);
        using var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri == AppUpdates.LatestReleaseUri ? new StringContent(metadata) : new ByteArrayContent(bytes)
        }));
        using var updates = new AppUpdates(http, directory, runtime);
        AppRelease release = Await(updates.CheckAsync(new Version(0, 0, 0), CancellationToken.None))!;
        return Await(updates.DownloadAsync(release, CancellationToken.None));
    }

    private static void Download()
    {
        string root = DirectoryFor("download");
        PreparedUpdate update = GetUpdate(Archive("../../escape.txt"), root);
        Check(File.ReadAllBytes(update.ExecutablePath).SequenceEqual(File.ReadAllBytes(FixtureExe)), "the verified executable must match the downloaded payload");
        Check(update.ExecutableSha256 == Hash(File.ReadAllBytes(FixtureExe)), "the extracted executable gets its own integrity digest");
        Check(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP paths outside the staging folder must never be extracted");
        Check(Directory.GetFiles(Path.GetDirectoryName(update.ExecutablePath)!).Length == 1, "only the executable is extracted and the ZIP is removed");
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var updates = new AppUpdates(http, root);
        Check(Await(updates.CheckAsync(new Version(0, 0, 0), CancellationToken.None)) is null, "a repository without a release does not produce a false update");
    }

    private static void InvalidDownloads()
    {
        byte[] archive = Archive();
        AppRelease release = Parse(ReleaseJson("v" + FixtureVersion, Hash(archive), archive.Length))!;
        foreach (byte[] bad in new[] { archive.Select((value, index) => index == 0 ? (byte)(value ^ 0xff) : value).ToArray(), archive[..^1], archive.Concat(new byte[] { 0 }).ToArray() })
        {
            string root = DirectoryFor("bad-download");
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bad) }));
            using var updates = new AppUpdates(http, root);
            ExpectFailure(() => Await(updates.DownloadAsync(release, CancellationToken.None)));
            Check(!Directory.EnumerateFiles(root, "WinSkitch.exe", SearchOption.AllDirectories).Any(), "a failed download must not leave an installable executable");
        }
        ExpectFailure(() => GetUpdate(Archive(duplicate: true), DirectoryFor("duplicate")));
        ExpectFailure(() => GetUpdate(Archive(omitExe: true, extra: "folder/WinSkitch.exe"), DirectoryFor("missing")));
        ExpectFailure(() => GetUpdate(Archive(), DirectoryFor("version"), version: new Version(99, 0, 0)));
        ExpectFailure(() => GetUpdate(Archive(), DirectoryFor("architecture"), runtime: "win-arm64"));
        using (var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/release.zip")
        })))
        using (var updates = new AppUpdates(http, DirectoryFor("redirect")))
            ExpectFailure(() => Await(updates.DownloadAsync(release, CancellationToken.None)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var canceledHttp = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        using var canceledUpdates = new AppUpdates(canceledHttp, DirectoryFor("canceled"));
        ExpectFailure(() => Await(canceledUpdates.DownloadAsync(release, canceled.Token)));
    }

    private static UpdateInstallPlan Plan(string directory, out string original)
    {
        PreparedUpdate update = GetUpdate(Archive(), Path.Combine(directory, "downloads"));
        string targetDirectory = Path.Combine(directory, "日本語 ' & $ フォルダー");
        Directory.CreateDirectory(targetDirectory);
        string target = Path.Combine(targetDirectory, "WinSkitch.exe");
        original = "Existing binary: " + Guid.NewGuid().ToString();
        File.WriteAllText(target, original);
        using Process parent = Process.GetCurrentProcess();
        return Await(UpdateInstaller.PrepareAsync(update, target, parent, CancellationToken.None));
    }

    private static void InstallAndRollback()
    {
        UpdateInstallPlan plan = Plan(DirectoryFor("install"), out _);
        bool restarted = false;
        Await(UpdateInstaller.InstallAsync(plan, () => restarted = true, CancellationToken.None));
        Check(restarted && File.ReadAllBytes(plan.TargetPath).SequenceEqual(File.ReadAllBytes(FixtureExe)), "install must replace the old bytes before restarting the same path");
        Check(!File.Exists(plan.CandidatePath) && !File.Exists(plan.BackupPath), "a successful update removes its temporary candidate and backup");
        plan = Plan(DirectoryFor("rollback"), out string original);
        ExpectFailure(() => Await(UpdateInstaller.InstallAsync(plan, () => throw new IOException("Synthetic restart failure"), CancellationToken.None)));
        Check(File.ReadAllText(plan.TargetPath) == original, "failed restart must restore the previous executable");
    }

    private static void LockedAndTampered()
    {
        UpdateInstallPlan plan = Plan(DirectoryFor("locked"), out string original);
        using (var locked = new FileStream(plan.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var timeout = new CancellationTokenSource(100))
            ExpectFailure(() => Await(UpdateInstaller.InstallAsync(plan, () => throw new InvalidOperationException("Must not restart"), timeout.Token)));
        Check(File.ReadAllText(plan.TargetPath) == original, "a locked destination must preserve the existing app");
        plan = Plan(DirectoryFor("tampered"), out original);
        File.WriteAllText(plan.CandidatePath, "changed after download");
        ExpectFailure(() => Await(UpdateInstaller.InstallAsync(plan, () => throw new InvalidOperationException("Must not restart"), CancellationToken.None)));
        Check(File.ReadAllText(plan.TargetPath) == original, "modified staged bytes must never replace the app");
    }

    private static void InstallPlansAndParent()
    {
        UpdateInstallPlan plan = Plan(DirectoryFor("paths"), out _);
        foreach (UpdateInstallPlan invalid in new[]
        {
            plan with { TargetPath = "relative.exe" }, plan with { TargetPath = Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, "other.exe") },
            plan with { CandidatePath = Path.Combine(Path.GetDirectoryName(plan.StagedPath)!, Path.GetFileName(plan.CandidatePath)) },
            plan with { BackupPath = plan.StagedPath }, plan with { ReadyEvent = "unexpected" }, plan with { Sha256 = "invalid" }
        }) ExpectFailure(() => UpdateInstaller.ValidatePlan(invalid));
        ExpectFailure(() => UpdateInstaller.ValidatePlan(plan, plan.TargetPath));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WinSkitch.Tests.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--update-test-parent");
        using Process parent = Process.Start(start)!;
        var waitPlan = plan with { ParentId = parent.Id, ParentStarted = parent.StartTime.ToUniversalTime().Ticks };
        Await(UpdateInstaller.WaitForParentAsync(waitPlan, CancellationToken.None));
        Check(parent.HasExited && parent.ExitCode == 0, "the helper waits for its exact original process without terminating it");
        using Process current = Process.GetCurrentProcess();
        using var timeout = new CancellationTokenSource(200);
        Await(UpdateInstaller.WaitForParentAsync(plan with { ParentStarted = current.StartTime.ToUniversalTime().Ticks + 1 }, timeout.Token));
    }

    private static void HelperRoundTrip()
    {
        HelperRoundTrip(failStartup: false);
        HelperRoundTrip(failStartup: true);
    }

    private static void HelperRoundTrip(bool failStartup)
    {
        string root = DirectoryFor("helper-round-trip");
        string staging = Path.Combine(root, "staging");
        string targetDirectory = Path.Combine(root, "installed");
        Directory.CreateDirectory(staging); Directory.CreateDirectory(targetDirectory);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            if (Path.GetExtension(file) == ".exe") continue;
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)));
        }
        // Test clients host the production updater without a GUI, tray, hotkeys, or network access.
        string helperPath = Path.Combine(staging, "WinSkitch.exe");
        byte[] helperBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WinSkitch.Tests.exe"));
        File.WriteAllBytes(helperPath, helperBytes);
        var info = FileVersionInfo.GetVersionInfo(helperPath);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        var release = new AppRelease(version, "v" + version, "fixture.zip", new Uri("https://github.com/"), 1, Hash(helperBytes));
        var prepared = new PreparedUpdate(release, helperPath, Hash(helperBytes));
        string target = Path.Combine(targetDirectory, "WinSkitch.exe");
        File.WriteAllText(target, "Previous fixture executable");
        var parentStart = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WinSkitch.Tests.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        parentStart.ArgumentList.Add("--update-test-parent");
        using Process parent = Process.Start(parentStart)!;
        UpdateInstallPlan plan = Await(UpdateInstaller.PrepareAsync(prepared, target, parent, CancellationToken.None));
        string manifest = Path.Combine(staging, "install.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(plan));
        string report = Path.Combine(root, "restarted.txt");
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, plan.ReadyEvent);
        var start = new ProcessStartInfo(helperPath)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(manifest);
        start.Environment["WINSKITCH_UPDATE_TEST_REPORT"] = report;
        if (failStartup) start.Environment["WINSKITCH_UPDATE_TEST_FAIL_STARTUP"] = "1";
        using Process helper = Process.Start(start)!;
        if (!helper.WaitForExit(15_000)) { helper.Kill(); throw new IOException("Test update helper timed out."); }
        string error = Path.Combine(staging, "helper-error.txt");
        if (failStartup)
        {
            Check(helper.ExitCode == 1, "the helper must report the failed startup");
            Check(File.ReadAllText(target) == "Previous fixture executable", "an immediately crashed new process must restore the old executable");
            return;
        }
        Check(helper.ExitCode == 0, File.Exists(error) ? File.ReadAllText(error) : "the helper must complete successfully");
        Check(ready.WaitOne(0), "the helper must announce readiness before waiting for its parent");
        for (int attempt = 0; attempt < 30 && !File.Exists(report); attempt++) Thread.Sleep(100);
        Check(File.Exists(report) && File.ReadAllText(report) == "restarted", "the installed executable must restart successfully");
        Check(File.ReadAllBytes(target).SequenceEqual(helperBytes), "the helper installs verified bytes at the original executable path");
    }

    private static void ExpectFailure(Action action)
    {
        bool failed = false;
        try { action(); } catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException) { failed = true; }
        Check(failed, "the invalid update operation must fail safely");
    }
}
