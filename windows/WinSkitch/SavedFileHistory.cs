using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;

namespace WinSkitch;

public sealed record SavedFileEntry(string FilePath, DateTimeOffset SavedAt);

/// <summary>Remembers successfully saved files independently of the editor's undo history.</summary>
public sealed class SavedFileHistory
{
    public const int MaxEntries = 20;
    private readonly string _storagePath;
    private readonly List<SavedFileEntry> _entries = new();
    private readonly ReadOnlyCollection<SavedFileEntry> _readOnlyEntries;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<SavedFileEntry> Entries => _readOnlyEntries;
    public string? LastError { get; private set; }
    public event Action? Changed;

    public SavedFileHistory(string? storagePath = null)
    {
        _storagePath = Path.GetFullPath(storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSkitch", "save-history.json"));
        _readOnlyEntries = _entries.AsReadOnly();
        Load();
    }

    /// <summary>Returns whether the updated history was persisted; a write failure retains it in memory.</summary>
    public bool Add(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!TryNormalizeFilePath(filePath, out string normalizedPath) || !File.Exists(normalizedPath))
            throw new ArgumentException("保存履歴には、保存済みのファイルの絶対パスが必要です。", nameof(filePath));

        _entries.RemoveAll(entry => string.Equals(entry.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new SavedFileEntry(normalizedPath, DateTimeOffset.Now));
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        bool persisted = Persist();
        Changed?.Invoke();
        return persisted;
    }

    /// <summary>Clears the history without deleting any of the saved files.</summary>
    public bool Clear()
    {
        _entries.Clear();
        bool persisted = Persist();
        Changed?.Invoke();
        return persisted;
    }

    private void Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(_storagePath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("保存履歴の形式が正しくありません。");

            var loaded = new List<SavedFileEntry>();
            bool skippedEntries = false;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty(nameof(SavedFileEntry.FilePath), out JsonElement pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    !TryNormalizeFilePath(pathElement.GetString(), out string filePath) ||
                    !item.TryGetProperty(nameof(SavedFileEntry.SavedAt), out JsonElement timeElement) ||
                    timeElement.ValueKind != JsonValueKind.String ||
                    !timeElement.TryGetDateTimeOffset(out DateTimeOffset savedAt) || savedAt == default)
                {
                    skippedEntries = true;
                    continue;
                }

                // Keep valid paths even when the file has moved or been deleted, so the UI
                // can still explain the old save location and disable unavailable files.
                loaded.Add(new SavedFileEntry(filePath, savedAt));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedFileEntry entry in loaded.OrderByDescending(entry => entry.SavedAt))
            {
                if (!seen.Add(entry.FilePath)) continue;
                _entries.Add(entry);
                if (_entries.Count == MaxEntries) break;
            }
            if (skippedEntries) LastError = "保存履歴の一部を読み込めませんでした。";
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // There is no history yet. Reading it never creates a settings directory.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            LastError = $"保存履歴を読み込めませんでした。{error.Message}";
        }
    }

    private bool Persist()
    {
        string? stagedPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_storagePath)
                ?? throw new IOException("保存履歴のフォルダーを取得できませんでした。");
            Directory.CreateDirectory(directory);
            stagedPath = Path.Combine(directory, $".{Path.GetFileName(_storagePath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, _entries, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            // Stage next to the destination before replacing it, so incomplete JSON never
            // overwrites a previous history file if serialization or publishing fails.
            if (File.Exists(_storagePath)) File.Replace(stagedPath, _storagePath, destinationBackupFileName: null);
            else File.Move(stagedPath, _storagePath);
            stagedPath = null;
            LastError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            LastError = $"保存履歴を保存できませんでした。{error.Message}";
            return false;
        }
        finally
        {
            if (stagedPath is not null)
            {
                try { File.Delete(stagedPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
    }

    private static bool TryNormalizeFilePath(string? filePath, out string normalizedPath)
    {
        normalizedPath = "";
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath)) return false;

        // History is local Windows file metadata. Device paths, alternate data streams,
        // URLs and shell syntax are never accepted as persisted file locations.
        bool drivePath = filePath.Length >= 3 && IsDriveLetter(filePath[0]) && filePath[1] == ':' &&
            (filePath[2] == '\\' || filePath[2] == '/');
        bool uncPath = filePath.StartsWith(@"\\", StringComparison.Ordinal) || filePath.StartsWith("//", StringComparison.Ordinal);
        if (!drivePath && !uncPath) return false;
        if (uncPath && (filePath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            filePath.StartsWith(@"\\.\", StringComparison.Ordinal))) return false;
        for (int index = 0; index < filePath.Length; index++)
        {
            char character = filePath[index];
            if (char.IsControl(character) || character is '"' or '<' or '>' or '|' or '*' or '?' ||
                (character == ':' && (!drivePath || index != 1))) return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(filePath);
            if (uncPath)
            {
                string[] parts = normalizedPath.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0] is "." or ".." || parts[1] is "." or "..") return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = "";
            return false;
        }
    }

    private static bool IsDriveLetter(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
