using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinSkitch;

internal sealed record AppRelease(Version Version, string Tag, string AssetName, Uri DownloadUri, long Size, string Sha256);
internal sealed record PreparedUpdate(AppRelease Release, string ExecutablePath, string ExecutableSha256);

/// <summary>Only public, stable releases from this repository can supply an update.</summary>
internal sealed class AppUpdates : IDisposable
{
    internal const string Repository = "1000ri-jp/win_skitch";
    internal static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private const long MaxArchiveSize = 200 * 1024 * 1024;
    private const long MaxExecutableSize = 300 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _directory;
    private readonly string _runtime;
    internal static Version CurrentVersion
    {
        get
        {
            Version version = typeof(App).Assembly.GetName().Version!;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    internal static string CurrentRuntime => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64",
        _ => throw new NotSupportedException("このPC向けの自動更新には対応していません。")
    };

    internal AppUpdates(HttpClient? http = null, string? directory = null, string? runtime = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = http is null;
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSkitch", "updates"));
        _runtime = runtime ?? CurrentRuntime;
        PruneOldDownloads();
    }

    private void PruneOldDownloads()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (string directory in Directory.EnumerateDirectories(_directory))
            {
                // Only remove our GUID staging folders; never follow a junction or symlink.
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(directory) >= DateTime.UtcNow.AddDays(-7)) continue;
                string absolute = Path.GetFullPath(directory);
                string prefix = _directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(absolute, recursive: true);
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(LatestReleaseUri, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var memory = new MemoryStream();
        await CopyBoundedAsync(response, memory, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(memory.ToArray());
        return ParseRelease(document.RootElement, currentVersion, _runtime);
    }

    // GitHub provides each uploaded asset's SHA-256 digest in its release API.
    // https://docs.github.com/rest/releases/releases#get-the-latest-release
    internal static AppRelease? ParseRelease(JsonElement release, Version currentVersion, string runtime)
    {
        if (runtime is not ("win-x64" or "win-arm64")) throw new NotSupportedException("未対応のPCです。");
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        string[] components = (tag.StartsWith('v') ? tag[1..] : tag).Split('.');
        if (components.Length != 3 || !Array.TrueForAll(components, value =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0) ||
            !Version.TryParse(string.Join('.', components), out Version? version) || version <= currentVersion) return null;
        string assetName = $"WinSkitch-{tag}-{runtime}.zip";
        AppRelease? result = null;
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;
            string expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
            string digest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
            long size = asset.GetProperty("size").GetInt64();
            if (asset.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > MaxArchiveSize ||
                asset.GetProperty("browser_download_url").GetString() != expectedUrl ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]) || result is not null)
                throw new InvalidDataException("更新ファイルの検証情報が正しくありません。");
            result = new AppRelease(version, tag, assetName, new Uri(expectedUrl), size, digest[7..].ToLowerInvariant());
        }
        return result;
    }

    internal async Task<PreparedUpdate> DownloadAsync(AppRelease release, CancellationToken cancellationToken)
    {
        // Unique staging paths also keep concurrent processes from sharing a partial download.
        string directory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "release.zip");
        string executablePath = Path.Combine(directory, "WinSkitch.exe");
        try
        {
            using (var response = await GetAsync(release.DownloadUri, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long length = await CopyBoundedAsync(response, file, release.Size, cancellationToken).ConfigureAwait(false);
                if (length != release.Size) throw new InvalidDataException("更新ファイルのダウンロードが完了していません。");
            }
            if (await FileHashAsync(archivePath, cancellationToken).ConfigureAwait(false) != release.Sha256)
                throw new InvalidDataException("更新ファイルのSHA-256が一致しません。");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry? executable = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName != "WinSkitch.exe") continue;
                    if (executable is not null) throw new InvalidDataException("更新ZIPに実行ファイルが重複しています。");
                    executable = entry;
                }
                if (executable is null || executable.Length <= 0 || executable.Length > MaxExecutableSize)
                    throw new InvalidDataException("更新ZIPに正しいWinSkitch.exeがありません。");
                using Stream input = executable.Open();
                using var output = new FileStream(executablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            ValidateExecutable(executablePath, release.Version, _runtime);
            string hash = await FileHashAsync(executablePath, cancellationToken).ConfigureAwait(false);
            File.Delete(archivePath);
            return new PreparedUpdate(release, executablePath, hash);
        }
        catch
        {
            TryDelete(archivePath); TryDelete(executablePath);
            try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"WinSkitch/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Uri? destination = response.RequestMessage?.RequestUri;
        if (destination is null || destination.Scheme != "https" || destination.Host is not
            ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
        {
            response.Dispose();
            throw new InvalidDataException("更新のダウンロード先が正しくありません。");
        }
        return response;
    }

    private static async Task<long> CopyBoundedAsync(HttpResponseMessage response, Stream output, long limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long size && size > limit)
            throw new InvalidDataException("更新ファイルのサイズが上限を超えています。");
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
            if (length > limit) throw new InvalidDataException("更新ファイルのサイズが上限を超えています。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return length;
    }

    internal static bool IsSha256(string hash) => hash.Length == 64 && Array.TrueForAll(hash.ToCharArray(), Uri.IsHexDigit);
    internal static async Task<string> FileHashAsync(string path, CancellationToken cancellationToken)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    internal static void ValidateExecutable(string path, Version expectedVersion, string runtime)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length < 128 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("更新EXEの形式が正しくありません。");
        file.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 0x40 || offset > file.Length - 6) throw new InvalidDataException("更新EXEのヘッダーが正しくありません。");
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != (runtime == "win-arm64" ? 0xaa64 : 0x8664))
            throw new InvalidDataException("更新EXEがこのPCの種類に対応していません。");
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expectedVersion)
            throw new InvalidDataException("更新EXEのバージョンが一致しません。");
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
