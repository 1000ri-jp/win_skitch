using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSkitch;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "qa"));
        Directory.CreateDirectory(_output);
        // Initialize resources without starting the application, tray, hotkeys, or any screen capture.
        var application = new App();
        application.InitializeComponent();
        foreach (AnnotationKind kind in Enum.GetValues<AnnotationKind>())
        {
            AnnotationKind captured = kind;
            Run($"{kind}: rendering, hit testing, move, and clone", () => AnnotationBehavior(captured));
        }
        Run("editable endpoint, box, and stamp handles", Handles);
        Run("render caches follow mutable annotation changes", CacheInvalidation);
        Run("pixelate covers annotations in composition order", () => EffectOrder(AnnotationKind.Pixelate));
        Run("blur covers annotations in composition order", () => EffectOrder(AnnotationKind.Blur));
        Run("undo and redo retain independent gesture snapshots", History);
        Run("crop expands with white margins and shifts annotations", CropAndUndo);
        Run("high-DPI inputs retain physical pixel dimensions", NormalizeDpi);
        Run("flatten replaces transparent pixels with white", TransparentFlatten);
        Run("PNG and JPEG save, reopen, and release file handles", SaveAndReopen);
        Run("saved image and video history persists after restart and retains moved files", SavedHistoryRoundTrip);
        Run("saved history keeps the newest twenty destinations and refreshes repeated Windows paths", SavedHistoryLimit);
        Run("saved history tolerates damaged JSON and skips invalid records without losing valid entries", SavedHistoryDamagedData);
        Run("history persistence failure preserves old data and keeps the successful save in memory", SavedHistoryWriteFailure);
        Run("saved history window renders long Japanese paths and updates available actions without shell launches", SavedHistoryWindowRender);
        Run("main window records only completed image saves in its injected history", MainWindowSaveHistory);
        Run("synthetic desktop cropping handles negative global coordinates", DesktopCropping);
        Run("synthetic video frames encode to a playable MP4 with timing and changing colors", VideoRoundTrip);
        Run("odd capture sizes retain content and pad MP4 dimensions to even pixels", VideoOddDimensions);
        Run("abandoned video preserves existing output and removes temporary files", VideoAbort);
        Run("invalid and empty video input fails without publishing partial output", VideoInvalidInput);
        Run("a locked video destination preserves the finished recording for recovery", VideoPublishFailure);
        Run("recording controls show elapsed time and save state without opening a window", RecordingControls);
        Run("text editing commits, undoes, and restores visibility", TextEditing);
        Run("launch arguments distinguish tray startup from editor and image startup", LaunchArguments);
        Run("launch arguments reject ambiguous startup requests", InvalidLaunchArguments);
        Run("startup command preserves spaces and Japanese executable paths", StartupCommand);
        Run("startup command rejects unsafe or incomplete executable paths", InvalidStartupCommands);
        Run("startup registration selects the app executable instead of the dotnet host", StartupExecutable);
        Run("a second launch signals the existing resident and exits independently", ResidentActivation);
        Run("offscreen main window layout and image editing render", MainWindowRender);
        application.Shutdown();
        Console.WriteLine($"\n{_passed} passed, {_failed} failed. Visual artifacts: {_output}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { _failed++; Console.Error.WriteLine($"FAIL {name}\n{error}"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private static void Near(double expected, double actual, double tolerance, string message) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"{message}: expected about {expected}, got {actual}");

    private static Annotation Example(AnnotationKind kind) => new()
    {
        Kind = kind, Start = kind == AnnotationKind.Stamp ? new Point(88, 58) : new Point(40, 25),
        End = new Point(135, 90), Width = kind is AnnotationKind.Text or AnnotationKind.Stamp ? 24 : 6,
        Color = Color.FromRgb(234, 51, 35), Text = "注釈 Aa", Stamp = "check",
        Points = new List<Point> { new(40, 25), new(70, 65), new(135, 90) }
    };

    private static void AnnotationBehavior(AnnotationKind kind)
    {
        BitmapSource background = Checkerboard(180, 130);
        Annotation annotation = Example(kind);
        BitmapSource output = AnnotationRenderer.Render(background, new[] { annotation });
        Equal(180, output.PixelWidth, "output width");
        Equal(130, output.PixelHeight, "output height");
        Check(output.IsFrozen, "render output must be safe to share across WPF consumers");
        Check(ChangedPixels(background, output) > 30, $"{kind} must visibly alter the image");
        if (kind is not AnnotationKind.Pixelate and not AnnotationKind.Blur)
            Check(CountPixels(output, p => p.R > p.G + 45 && p.R > p.B + 45) > 10, $"{kind} should use the selected red color");

        Point hit = kind switch
        {
            AnnotationKind.Arrow or AnnotationKind.Line => new Point(87.5, 57.5),
            AnnotationKind.Rectangle or AnnotationKind.RoundedRectangle or AnnotationKind.Ellipse => new Point(87.5, 25),
            AnnotationKind.Pen or AnnotationKind.Highlighter => new Point(70, 65),
            AnnotationKind.Stamp => annotation.Start,
            _ => new Point(annotation.Bounds.Left + annotation.Bounds.Width / 2, annotation.Bounds.Top + annotation.Bounds.Height / 2)
        };
        Check(annotation.HitTest(hit, 3), $"{kind} should be selectable at its visible geometry");
        Check(!annotation.HitTest(new Point(-100, -100), 3), $"{kind} must reject a distant point");
        if (kind is AnnotationKind.Rectangle or AnnotationKind.RoundedRectangle or AnnotationKind.Ellipse)
            Check(!annotation.HitTest(new Point(87.5, 57.5), 1), "shape interiors should allow selection of annotations underneath");

        Annotation clone = annotation.Clone();
        Point originalStart = annotation.Start;
        Point originalPoint = annotation.Points[0];
        clone.Move(12, -8);
        clone.Points.Add(new Point(1, 2));
        clone.Text = "changed";
        Equal(originalStart, annotation.Start, "moving a clone must leave its source alone");
        Equal(originalPoint, annotation.Points[0], "cloned pen points must be independent");
        Equal(3, annotation.Points.Count, "adding cloned points must not mutate source history");
        Equal(originalStart + new Vector(12, -8), clone.Start, "move should translate annotation geometry");
        annotation.Hidden = true;
        Check(!annotation.HitTest(hit, 3), "temporarily hidden text must not be selectable");
        Equal(0, ChangedPixels(background, AnnotationRenderer.Render(background, new[] { annotation })), "hidden annotation should not export");
        SavePng(output, $"annotation-{kind}.png");
    }

    private static void Handles()
    {
        var arrow = Example(AnnotationKind.Arrow);
        Equal(2, arrow.Handles.Count, "arrows have two endpoint handles");
        arrow.SetHandle("p0", new Point(20, 30));
        arrow.SetHandle("p1", new Point(140, 100));
        Equal(new Point(20, 30), arrow.Start, "first endpoint");
        Equal(new Point(140, 100), arrow.End, "second endpoint");

        var box = Example(AnnotationKind.Rectangle);
        Equal(4, box.Handles.Count, "boxes have corner handles");
        box.SetHandle("x1y0", new Point(150, 15));
        Equal(new Point(40, 15), box.Start, "corner resize retains the opposite x coordinate");
        Equal(new Point(150, 90), box.End, "corner resize retains the opposite y coordinate");
        box.SetHandle("x0y1", new Point(30, 95));
        Equal(new Point(30, 15), box.Start, "other diagonal corner x");
        Equal(new Point(150, 95), box.End, "other diagonal corner y");
        box.SetHandle("x0y0", new Point(170, 110));
        Check(box.Bounds.Width > 0 && box.Bounds.Height > 0, "crossing box corners must still produce normalized bounds");

        var stamp = Example(AnnotationKind.Stamp);
        Equal(1, stamp.Handles.Count, "stamps have a radial handle");
        stamp.SetHandle("r", stamp.Start + new Vector(30, 40));
        Near(50, stamp.Width, 0.001, "stamp resize uses distance from center");
        stamp.SetHandle("r", stamp.Start);
        Check(stamp.Width >= 8, "stamp radial resize must not collapse to zero");
        Equal(0, Example(AnnotationKind.Pen).Handles.Count, "freehand strokes are moved without endpoint handles");
    }

    private static void CacheInvalidation()
    {
        BitmapSource background = Solid(220, 160, Colors.White);
        var annotation = new Annotation { Kind = AnnotationKind.Line, Start = new Point(20, 40), End = new Point(100, 40), Width = 6, Color = Colors.Red };
        BitmapSource first = AnnotationRenderer.Render(background, new[] { annotation });
        annotation.Move(0, 65);
        annotation.Color = Colors.Blue;
        BitmapSource second = AnnotationRenderer.Render(background, new[] { annotation });
        Check(ChangedPixels(first, second) > 500, "cached geometry must follow move and color edits");
        Pixel moved = ReadPixel(second, 65, 105);
        Check(moved.B > 200 && moved.R < 50, "edited annotation must appear at its new position and color");
        Pixel old = ReadPixel(second, 65, 40);
        Check(old.R > 245 && old.G > 245 && old.B > 245, "old cached position must be cleared");
    }

    private static void EffectOrder(AnnotationKind kind)
    {
        BitmapSource background = Solid(160, 110, Colors.White);
        var line = new Annotation { Kind = AnnotationKind.Line, Start = new Point(30, 50), End = new Point(130, 50), Width = 3, Color = Color.FromRgb(230, 20, 30) };
        var effect = new Annotation { Kind = kind, Start = new Point(10, 10), End = new Point(145, 95), Width = 6 };
        BitmapSource sharp = AnnotationRenderer.Render(background, new[] { line });
        BitmapSource redacted = AnnotationRenderer.Render(background, new[] { line, effect });
        BitmapSource emptyEffect = AnnotationRenderer.Render(background, new[] { effect });
        Check(ChangedPixels(sharp, redacted) > 100, "redaction must alter an earlier annotation");
        Check(ChangedPixels(emptyEffect, redacted) > 100, "effect output must retain the color contribution of earlier annotations");
        Check(ReadPixel(redacted, 75, 50).G > ReadPixel(sharp, 75, 50).G + 40, "redaction must soften the previously sharp red line");
        Equal(ReadPixel(sharp, 3, 3), ReadPixel(redacted, 3, 3), "redaction should leave pixels outside its region alone");
        var later = line.Clone();
        later.Color = Colors.Blue;
        BitmapSource withLater = AnnotationRenderer.Render(background, new[] { line, effect, later });
        Pixel top = ReadPixel(withLater, 75, 50);
        Check(top.B > 200 && top.R < 50, "annotations added after a redaction must draw above it");
        SavePng(redacted, $"ordered-{kind}.png");
    }

    private static void History()
    {
        var document = new ImageDocument();
        document.Load(Solid(80, 60, Colors.White));
        Annotation pen = Example(AnnotationKind.Pen);
        document.Annotations.Add(pen);
        ImageDocument.Snapshot beforeMove = document.CaptureState();
        Point original = pen.Points[0];
        for (int i = 0; i < 4; i++) pen.Move(3, 2);
        document.CommitHistory(beforeMove);
        Check(document.Undo(), "a completed gesture should be undoable");
        Equal(original, document.Annotations[0].Points[0], "one undo must revert the entire gesture");
        Check(!document.CanUndo, "intermediate gesture updates should not create extra history entries");
        Check(document.Redo(), "gesture should be redoable");
        Equal(original + new Vector(12, 8), document.Annotations[0].Points[0], "redo retains all gesture updates");
        Check(document.Undo(), "second undo");
        ImageDocument.Snapshot newBranch = document.CaptureState();
        document.Annotations[0].Points[0] = new Point(2, 3);
        document.CommitHistory(newBranch);
        Check(!document.CanRedo, "editing after undo must discard redo history");
        Equal(original, beforeMove.Annotations[0].Points[0], "old snapshot data must not change when the document is mutated");
    }

    private static void CropAndUndo()
    {
        var document = new ImageDocument();
        document.Load(Solid(40, 30, Colors.Blue));
        var annotation = new Annotation { Kind = AnnotationKind.Rectangle, Start = new Point(10, 10), End = new Point(20, 20) };
        document.Annotations.Add(annotation);
        document.Crop(new Rect(-5, -7, 50, 45));
        Equal(50, document.Background!.PixelWidth, "expanded crop width");
        Equal(45, document.Background.PixelHeight, "expanded crop height");
        Equal(new Point(15, 17), document.Annotations[0].Start, "crop expansion translates annotation origins");
        Equal(new Point(25, 27), document.Annotations[0].End, "crop expansion translates annotation endpoints");
        Equal(new Pixel(255, 255, 255, 255), ReadPixel(document.Background, 1, 1), "outside crop margins are white");
        Equal(new Pixel(0, 0, 255, 255), ReadPixel(document.Background, 6, 8), "original image occupies its shifted location");
        Check(document.Undo(), "crop can be undone");
        Equal(40, document.Background!.PixelWidth, "undo restores original pixel width");
        Equal(new Point(10, 10), document.Annotations[0].Start, "undo restores annotation position");
        Check(document.Redo(), "crop can be redone");
        Equal(new Point(15, 17), document.Annotations[0].Start, "redo restores shifted annotations");
        document.Crop(new Rect(5, 7, 20, 15));
        Equal(20, document.Background!.PixelWidth, "inward crop width");
        Equal(new Point(10, 10), document.Annotations[0].Start, "inward crop translates editable annotations");
        Check(document.Undo(), "second crop can be undone independently");
        Equal(50, document.Background!.PixelWidth, "second crop undo returns expanded canvas");
        SavePng(document.Composite(), "crop-expansion.png");
    }

    private static void NormalizeDpi()
    {
        BitmapSource source = Solid(80, 60, Colors.Green, 192);
        Near(40, source.Width, 0.001, "test source should have a 192-DPI logical width");
        var document = new ImageDocument();
        document.Load(source);
        Equal(80, document.Background!.PixelWidth, "normalization preserves physical width");
        Near(96, document.Background.DpiX, 0.001, "editing coordinates use 96-DPI pixels");
        Near(80, document.Background.Width, 0.001, "normalized WPF width equals physical pixel width");
        BitmapSource composite = document.Composite();
        Equal(80, composite.PixelWidth, "high-DPI export width is not scaled down");
        Equal(ReadPixel(source, 40, 30), ReadPixel(composite, 40, 30), "normalization preserves color");
    }

    private static void TransparentFlatten()
    {
        var document = new ImageDocument();
        document.Load(Solid(20, 16, Colors.Transparent));
        Equal((byte)0, ReadPixel(document.Composite(), 5, 5).A, "composite retains source transparency");
        Pixel pixel = ReadPixel(document.Flatten(), 5, 5);
        Equal(new Pixel(255, 255, 255, 255), pixel, "export flattens transparency over white");
        var editor = new EditorSurface();
        editor.Load(document.Background!);
        BitmapSource preview = RenderElement(editor, 100, 80);
        Equal(pixel, ReadPixel(preview, 50, 40), "transparent image preview must match white export backing");
    }

    private static void SaveAndReopen()
    {
        var document = new ImageDocument();
        Color color = Color.FromRgb(70, 150, 210);
        document.Load(Solid(32, 24, color));
        foreach (string extension in new[] { "png", "jpg" })
        {
            string path = Path.Combine(_output, $"saved-image.{extension}");
            document.Save(path);
            Equal(path, document.FilePath!, "save tracks destination");
            var opened = new ImageDocument();
            opened.Open(path);
            Equal(32, opened.Background!.PixelWidth, "reopened image width");
            Equal(24, opened.Background.PixelHeight, "reopened image height");
            Pixel sample = ReadPixel(opened.Background, 15, 10);
            Near(color.R, sample.R, extension == "png" ? 0 : 5, "saved red channel");
            Near(color.G, sample.G, extension == "png" ? 0 : 5, "saved green channel");
            Near(color.B, sample.B, extension == "png" ? 0 : 5, "saved blue channel");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(exclusive.Length > 0, "opening an image must release its file so users can overwrite it");
        }
    }

    private static string SavedHistoryTestDirectory(string scenario)
    {
        string directory = Path.Combine(_output, "saved-history", scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateHistoryFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Synthetic saved-file history fixture. No desktop or player access.");
        return path;
    }

    private static void SavedHistoryRoundTrip()
    {
        string directory = SavedHistoryTestDirectory("restart");
        string storage = Path.Combine(directory, "settings", "history.json");
        string png = CreateHistoryFile(directory, "説明画像.png");
        string jpeg = CreateHistoryFile(directory, "共有画像.jpg");
        string video = CreateHistoryFile(directory, "操作動画.mp4");
        var history = new SavedFileHistory(storage);
        foreach (string path in new[] { png, jpeg, video })
            Check(history.Add(path), "a completed save should persist its destination");
        var beforeRestart = history.Entries.ToArray();
        File.Delete(png);
        var restored = new SavedFileHistory(storage);
        Equal(3, restored.Entries.Count, "image and video history survives a new instance");
        Check(beforeRestart.SequenceEqual(restored.Entries), "restart must retain destination order and save timestamps");
        Check(restored.Entries.Any(entry => entry.FilePath == png), "moved or deleted files retain their original folder in history");
        Check(restored.LastError is null, "a missing saved file must not be mistaken for corrupt history");
    }

    private static void SavedHistoryLimit()
    {
        string directory = SavedHistoryTestDirectory("newest-twenty");
        string storage = Path.Combine(directory, "settings", "history.json");
        var history = new SavedFileHistory(storage);
        string[] paths = Enumerable.Range(0, 23)
            .Select(index => CreateHistoryFile(directory, $"capture-{index:D2}.png")).ToArray();
        foreach (string path in paths) Check(history.Add(path), "fixture destination must be persisted");
        Equal(20, history.Entries.Count, "history should remain bounded after more than twenty saves");
        Check(paths.Skip(3).Reverse().SequenceEqual(history.Entries.Select(entry => entry.FilePath)),
            "the twenty most recent saves should be presented newest first");

        string repeated = Path.Combine(directory, ".", Path.GetFileName(paths[8])).ToUpperInvariant();
        Check(history.Add(repeated), "saving again should accept equivalent absolute Windows paths");
        Equal(20, history.Entries.Count, "saving an existing destination must not consume an extra slot");
        Equal(Path.GetFullPath(repeated), history.Entries[0].FilePath, "the repeated normalized path should move to the top");
        Equal(1, history.Entries.Count(entry => string.Equals(entry.FilePath, paths[8], StringComparison.OrdinalIgnoreCase)),
            "paths that differ only by casing refer to the same history destination");
        Check(history.Entries.Select(entry => entry.FilePath).SequenceEqual(new SavedFileHistory(storage).Entries.Select(entry => entry.FilePath)),
            "the bounded deduplicated history must survive restart");

        // Simulate an older writer or manually reordered settings file rather than relying on Add's own ordering.
        File.WriteAllText(storage, JsonSerializer.Serialize(history.Entries.Reverse()
            .Concat(new[] { new SavedFileEntry(paths[8], DateTimeOffset.UnixEpoch) })
            .Concat(paths.Take(3).Select(path => new SavedFileEntry(path, DateTimeOffset.UnixEpoch)))));
        var reordered = new SavedFileHistory(storage);
        Check(history.Entries.SequenceEqual(reordered.Entries),
            "loading must sort timestamps, retain the newest casing-insensitive duplicate, and discard excess older records");
    }

    private static void SavedHistoryDamagedData()
    {
        string directory = SavedHistoryTestDirectory("damaged-data");
        foreach (string damaged in new[] { "{ invalid json", "{}", "null" })
        {
            string storage = Path.Combine(directory, "broken-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(storage, damaged);
            var history = new SavedFileHistory(storage);
            Equal(0, history.Entries.Count, "invalid history should allow startup with an empty list");
            Check(!string.IsNullOrWhiteSpace(history.LastError), "invalid history should report a recoverable warning");
            string recovered = CreateHistoryFile(directory, "recovered.mp4");
            Check(history.Add(recovered), "a subsequent successful save should recover damaged history");
            Equal(recovered, new SavedFileHistory(storage).Entries.Single().FilePath, "recovered history should be loadable");
        }

        string valid = Path.Combine(directory, "以前の保存先", "移動済み.png");
        string mixedStorage = Path.Combine(directory, "mixed.json");
        DateTimeOffset savedAt = DateTimeOffset.Parse("2026-10-04T10:20:30+09:00");
        File.WriteAllText(mixedStorage, JsonSerializer.Serialize(new object?[]
        {
            new { FilePath = valid, SavedAt = savedAt }, null,
            new { FilePath = "", SavedAt = savedAt },
            new { FilePath = "relative.png", SavedAt = savedAt },
            new { FilePath = directory + "\\invalid\0.png", SavedAt = savedAt },
            new { FilePath = valid, SavedAt = "invalid date" },
            new { FilePath = 42, SavedAt = savedAt }
        }));
        var mixed = new SavedFileHistory(mixedStorage);
        Equal(1, mixed.Entries.Count, "invalid individual records must not discard a valid saved destination");
        Equal(valid, mixed.Entries[0].FilePath, "a valid missing file still represents a useful saved destination");
        Equal(savedAt, mixed.Entries[0].SavedAt, "loading must preserve the save timestamp including its offset");
        Check(!string.IsNullOrWhiteSpace(mixed.LastError), "skipped invalid records should produce a warning");
    }

    private static void SavedHistoryWriteFailure()
    {
        string directory = SavedHistoryTestDirectory("locked-settings");
        string storage = Path.Combine(directory, "settings", "history.json");
        string first = CreateHistoryFile(directory, "first.png");
        string completedVideo = CreateHistoryFile(directory, "completed-video.mp4");
        var history = new SavedFileHistory(storage);
        Check(history.Add(first), "initial history should be persisted before the lock");
        byte[] original = File.ReadAllBytes(storage);
        int changed = 0;
        history.Changed += () => changed++;
        using (var locked = new FileStream(storage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Check(!history.Add(completedVideo), "a locked history file should report persistence failure without throwing");
            Equal(completedVideo, history.Entries[0].FilePath, "the saved video should remain discoverable for this session");
            Check(!string.IsNullOrWhiteSpace(history.LastError), "persistence failure should explain that history could not be written");
            Equal(1, changed, "an open history window must be notified of the in-memory update after failed persistence");
            Check(File.Exists(completedVideo), "history failure must leave the successfully saved file intact");
        }
        Check(original.SequenceEqual(File.ReadAllBytes(storage)), "failed atomic publication must preserve the prior history file");
        Equal(first, new SavedFileHistory(storage).Entries.Single().FilePath, "the prior on-disk history must remain valid");
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(storage)!).Length, "a failed write must clean up its temporary settings file");
        Check(history.Add(completedVideo), "retry after releasing the lock should persist the complete history");
        Check(history.LastError is null, "a successful retry should clear the persistence warning");
        Equal(2, new SavedFileHistory(storage).Entries.Count, "retry should include both the earlier and completed destinations");
    }

    private static void SavedHistoryWindowRender()
    {
        string directory = SavedHistoryTestDirectory("window");
        var history = new SavedFileHistory(Path.Combine(directory, "settings", "history.json"));
        var window = new SaveHistoryWindow(history);
        Check(window.Content is FrameworkElement, "history must contain an offscreen-renderable WPF layout");
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel && panel.Background is null) panel.Background = window.Background;
        BitmapSource empty = RenderElement(content, 860, 540);
        Check(window.EmptyState.Visibility == Visibility.Visible, "empty history should explain how to populate the list");
        Check(!window.OpenFileButton.IsEnabled && !window.OpenFolderButton.IsEnabled,
            "empty history must disable actions that require a destination");
        SavePng(empty, "saved-history-empty.png");

        string japaneseFolder = Path.Combine(directory, "共有する資料と操作動画の保存先 " + new string('保', 55));
        string image = CreateHistoryFile(japaneseFolder, "注釈画像 " + new string('画', 28) + ".png");
        string video = CreateHistoryFile(japaneseFolder, "操作説明動画 " + new string('動', 28) + ".mp4");
        Check(history.Add(image) && history.Add(video), "fixture saves must populate the already-created history window");
        BitmapSource populated = RenderElement(content, 860, 540);
        Equal(2, window.HistoryList.Items.Count, "history change events must update an existing window");
        Equal(video, window.SelectedEntry!.FilePath, "the latest saved file should initially be selected");
        Equal("保存先を開く", window.OpenFolderButton.Content.ToString()!, "folder action should have its Japanese label");
        Equal("ファイルを開く", window.OpenFileButton.Content.ToString()!, "file action should have its Japanese label");
        Check(window.OpenFileButton.IsEnabled && window.OpenFolderButton.IsEnabled, "existing saved files should offer both actions");
        Equal(Visibility.Collapsed, window.EmptyState.Visibility, "populated history should replace the empty-state message");
        Check(Descendants(content).OfType<TextBlock>().Any(text => text.Text == video || text.Text == Path.GetDirectoryName(video)),
            "the long Japanese destination must be visible in the layout, not just retained internally");
        Check(CountPixels(populated, pixel => pixel.A == 255) > 300_000, "saved history should render a complete usable dialog surface");
        SavePng(populated, "saved-history-populated.png");

        File.Delete(video);
        window.Refresh();
        Check(!window.OpenFileButton.IsEnabled && window.OpenFolderButton.IsEnabled,
            "a deleted file must still allow its surviving save folder to open");
        Check(!string.IsNullOrWhiteSpace(window.StatusText.Text), "deleted files should display an explanation");
        SavePng(RenderElement(content, 860, 540), "saved-history-missing-file.png");
        File.Delete(image);
        Directory.Delete(japaneseFolder);
        window.Refresh();
        Check(!window.OpenFileButton.IsEnabled && !window.OpenFolderButton.IsEnabled,
            "when both the saved file and its parent are gone, both actions must be disabled");
        window.Close();
    }

    private static void MainWindowSaveHistory()
    {
        string directory = SavedHistoryTestDirectory("editor-save");
        var history = new SavedFileHistory(Path.Combine(directory, "settings", "history.json"));
        var window = new MainWindow(history);
        window.Editor.Load(Checkerboard(80, 60));
        string destination = Path.Combine(directory, "completed-image.png");
        window.Editor.Document.Save(destination); // Establish a filename without opening a save dialog.
        Layout((FrameworkElement)window.Content, 1000, 700);
        Menu menu = Descendants((DependencyObject)window.Content).OfType<Menu>().Single();
        MenuItem file = menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "ファイル");
        MenuItem save = file.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "保存");
        Check(file.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "保存履歴…"),
            "the File menu must provide access to saved history");
        Check(Descendants((DependencyObject)window.Content).OfType<Button>().Any(button => button.Content?.ToString() == "履歴"),
            "the toolbar must provide access to saved history");
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Equal(0, history.Entries.Count, "failed image writes must not appear as completed saves");
        }
        save.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Equal(destination, history.Entries.Single().FilePath, "the main window should remember a successfully saved image");
        Equal(destination, new SavedFileHistory(Path.Combine(directory, "settings", "history.json")).Entries.Single().FilePath,
            "image save integration should persist its destination without touching production settings");
    }

    private static void DesktopCropping()
    {
        BitmapSource source = CoordinatePattern(80, 60);
        var capture = new DesktopCapture(source, new Int32Rect(-120, -40, 80, 60), Array.Empty<Int32Rect>());
        BitmapSource cropped = ScreenCapture.Crop(capture, new Int32Rect(-110, -30, 20, 15));
        Equal(20, cropped.PixelWidth, "crop width in global coordinates");
        Equal(15, cropped.PixelHeight, "crop height in global coordinates");
        Equal(ReadPixel(source, 10, 10), ReadPixel(cropped, 0, 0), "negative desktop origin must be subtracted exactly once");
        Equal(ReadPixel(source, 29, 24), ReadPixel(cropped, 19, 14), "opposite crop corner maps to original pixels");
        BitmapSource clipped = ScreenCapture.Crop(capture, new Int32Rect(-125, -45, 20, 20));
        Equal(15, clipped.PixelWidth, "partly off-desktop crop clips its width");
        Equal(15, clipped.PixelHeight, "partly off-desktop crop clips its height");
        Equal(ReadPixel(source, 0, 0), ReadPixel(clipped, 0, 0), "clipping uses the desktop edge");
        bool rejected = false;
        try { ScreenCapture.Crop(capture, new Int32Rect(500, 500, 10, 10)); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "a wholly off-desktop crop must be rejected");
    }

    private static void VideoRoundTrip()
    {
        string path = Path.Combine(_output, "synthetic-video.mp4");
        const int framesPerSecond = 15, frameCount = 30;
        using (var writer = new MediaFoundationVideoWriter(path, 160, 96, framesPerSecond))
        {
            for (int i = 0; i < frameCount; i++)
            {
                long timestamp = i * 10_000_000L / framesPerSecond;
                long nextTimestamp = (i + 1) * 10_000_000L / framesPerSecond;
                writer.WriteFrame(Pixels(Solid(160, 96, i < 15 ? Colors.Red : Colors.Blue)), timestamp, nextTimestamp - timestamp);
            }
            writer.Finish();
        }
        DecodedVideo video = VideoReader.Decode(path);
        Equal(160, video.Width, "MP4 width");
        Equal(96, video.Height, "MP4 height");
        Near(framesPerSecond, video.FramesPerSecond, 0.01, "MP4 frame rate");
        Equal(frameCount, video.FrameCount, "all supplied frames should decode");
        Near(2, video.DurationSeconds, 0.05, "video duration follows sample timestamps");
        Check(video.First.R > 220 && video.First.G < 35 && video.First.B < 35, "decoded first frame must retain its red content");
        Check(video.Last.B > 220 && video.Last.R < 35 && video.Last.G < 35, "decoded last frame must retain changed blue content");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(exclusive.Length > 1000, "finished encoder and decoder must release the playable MP4 file");
    }

    private static void VideoOddDimensions()
    {
        string path = Path.Combine(_output, "synthetic-video-odd.mp4");
        using (var writer = new MediaFoundationVideoWriter(path, 161, 97))
        {
            Equal(162, writer.Width, "odd width is padded without shrinking the capture");
            Equal(98, writer.Height, "odd height is padded without shrinking the capture");
            byte[] frame = Pixels(CreateBitmap(161, 97, (_, y) => y < 48 ? Colors.Red : Colors.Blue));
            for (int i = 0; i < 4; i++)
                writer.WriteFrame(frame, i * 666_667L, 666_667);
            writer.Finish();
        }
        DecodedVideo video = VideoReader.Decode(path);
        Equal(162, video.Width, "decoder observes even padded width");
        Equal(98, video.Height, "decoder observes even padded height");
        Equal(4, video.FrameCount, "odd sizes still produce all frames");
        Check(video.FirstTop.R > 220 && video.FirstTop.G < 35 && video.FirstTop.B < 35,
            "the red top band must remain at the top after encoding and padding");
        Check(video.FirstBottom.B > 220 && video.FirstBottom.R < 35 && video.FirstBottom.G < 35,
            "the blue bottom band must remain at the bottom after encoding and padding");
    }

    private static void VideoAbort()
    {
        string directory = Path.Combine(_output, "video-abort");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "existing.mp4");
        byte[] original = { 21, 42, 63, 84 };
        File.WriteAllBytes(path, original);
        string[] before = Directory.GetFiles(directory).OrderBy(p => p).ToArray();
        using (var writer = new MediaFoundationVideoWriter(path, 160, 96))
        {
            writer.WriteFrame(Pixels(Solid(160, 96, Colors.Red)), 0, 666_667);
            Check(File.ReadAllBytes(path).SequenceEqual(original), "recording must keep the previous destination intact until it is finalized");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "abandoning an unfinished recording must preserve an existing destination");
        Check(Directory.GetFiles(directory).OrderBy(p => p).SequenceEqual(before), "abandoning recording must remove its staging files");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Equal((long)original.Length, exclusive.Length, "abort must release all destination handles");
    }

    private static void VideoInvalidInput()
    {
        string directory = Path.Combine(_output, "video-invalid");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
        string[] before = Directory.GetFiles(directory).OrderBy(p => p).ToArray();
        using (var writer = new MediaFoundationVideoWriter(path, 160, 96))
        {
            bool rejected = false;
            try { writer.WriteFrame(new byte[32], 0, 666_667); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "an incomplete frame must be rejected before native memory is accessed");
            rejected = false;
            try { writer.Finish(); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "an empty recording must not be published as a successful MP4");
        }
        Check(!File.Exists(path), "failed empty recording must not create a destination");
        Check(Directory.GetFiles(directory).OrderBy(p => p).SequenceEqual(before), "invalid input must not leave partial output behind");
    }

    private static void VideoPublishFailure()
    {
        string directory = Path.Combine(_output, "video-publish-failure");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "existing.mp4");
        byte[] original = { 12, 34, 56, 78 };
        File.WriteAllBytes(path, original);
        string[] before = Directory.GetFiles(directory).OrderBy(p => p).ToArray();
        string? errorMessage = null;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new MediaFoundationVideoWriter(path, 160, 96))
        {
            for (int i = 0; i < 4; i++)
                writer.WriteFrame(Pixels(Solid(160, 96, Colors.Red)), i * 666_667L, 666_667);
            try { writer.Finish(); }
            catch (IOException error) { errorMessage = error.Message; }
            Check(errorMessage is not null, "publishing to an exclusively locked destination must fail visibly");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "publication failure must preserve the existing destination");
        string[] recoveryFiles = Directory.GetFiles(directory).Except(before).ToArray();
        Equal(1, recoveryFiles.Length, "a finalized recording must remain available for recovery");
        string recoveryPath = recoveryFiles[0];
        try
        {
            Check(errorMessage!.Contains(recoveryPath, StringComparison.OrdinalIgnoreCase), "the error must identify the recoverable recording path");
            DecodedVideo video = VideoReader.Decode(recoveryPath);
            Equal(4, video.FrameCount, "retained recording must contain all completed frames");
            Check(video.First.R > 220 && video.First.G < 35 && video.First.B < 35,
                "the retained file must remain a playable recording with the captured content");
        }
        finally { File.Delete(recoveryPath); }
        Check(Directory.GetFiles(directory).OrderBy(p => p).SequenceEqual(before), "test cleanup must remove only its verified recovery file");
    }

    private static void RecordingControls()
    {
        Equal("00:59", RecordingControlsWindow.FormatElapsed(TimeSpan.FromSeconds(59)), "sub-minute timer");
        Equal("01:00", RecordingControlsWindow.FormatElapsed(TimeSpan.FromMinutes(1)), "minute rollover");
        Equal("01:01:01", RecordingControlsWindow.FormatElapsed(TimeSpan.FromSeconds(3661)), "recordings over an hour retain the full duration");
        Equal("100:00:00", RecordingControlsWindow.FormatElapsed(TimeSpan.FromHours(100)), "elapsed hours must not wrap after a day");
        int stopCalls = 0;
        var window = new RecordingControlsWindow(() => TimeSpan.FromSeconds(59), () => stopCalls++);
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel && panel.Background is null) panel.Background = window.Background;
        Button stop = Descendants(content).OfType<Button>().Single();
        Check(stop.IsEnabled, "recording must initially allow stopping");
        Check(stop.Content.ToString()!.Contains("停止"), "stop action must be visible");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(1, stopCalls, "stop button invokes recording completion");
        BitmapSource image = RenderElement(content, 332, 80);
        Check(CountPixels(image, p => p.R > p.G + 50 && p.R > p.B + 50) > 20, "recording state should have a visible red indicator");
        SavePng(image, "recording-controls.png");
        window.BeginSaving();
        Check(!stop.IsEnabled, "saving must disable duplicate stop requests");
        Check(stop.Content.ToString()!.Contains("保存中"), "finalization must expose its progress state");
        window.CloseAfterRecording();
    }

    private static void TextEditing()
    {
        var editor = new EditorSurface();
        editor.Load(Solid(240, 160, Colors.White));
        Layout(editor, 500, 300);
        editor.StartText(new Point(20, 20));
        Check(editor.IsEditingText, "text entry starts an inline editor");
        TextBox textbox = Descendants(editor).OfType<TextBox>().Single();
        textbox.Text = "before\n二行目";
        editor.CommitText();
        Check(!editor.IsEditingText, "committing text removes the inline editor");
        Equal(1, editor.Document.Annotations.Count, "new text is a single annotation");
        Equal("before\n二行目", editor.Document.Annotations[0].Text, "multiline text content is preserved");
        Check(editor.Selected is not null && !editor.Selected.Hidden, "committed annotation is visible and selected");
        Annotation existing = editor.Document.Annotations[0];
        editor.StartText(existing.Start, existing);
        Check(existing.Hidden, "text being edited is hidden behind its inline textbox");
        Descendants(editor).OfType<TextBox>().Single().Text = "after";
        editor.CommitText();
        Equal("after", editor.Document.Annotations[0].Text, "edit changes existing annotation");
        Check(!existing.Hidden, "commit makes existing annotation visible again");
        editor.Undo();
        Equal("before\n二行目", editor.Document.Annotations[0].Text, "undo restores previous text");
        Check(!editor.Document.Annotations[0].Hidden, "undo must not restore hidden editing state");
        Check(editor.Selected is null, "undo clears selection pointing at replaced annotation instances");
        editor.Redo();
        Equal("after", editor.Document.Annotations[0].Text, "redo restores edited text");
        editor.Undo();
        editor.Undo();
        Equal(0, editor.Document.Annotations.Count, "second undo removes newly created text");
        editor.Redo();
        editor.Refresh();
        SavePng(RenderElement(editor, 500, 300), "editor-text.png");
    }

    private static void LaunchArguments()
    {
        LaunchOptions normal = LaunchOptions.Parse(Array.Empty<string>());
        Check(!normal.StartInTray && normal.ImagePath is null, "manual startup should open an empty editor");

        LaunchOptions tray = LaunchOptions.Parse(new[] { "--tray" });
        Check(tray.StartInTray && tray.ImagePath is null, "logon startup should initialize only the tray");

        const string imagePath = @"C:\画像 フォルダー\--tray sample.png";
        LaunchOptions image = LaunchOptions.Parse(new[] { imagePath });
        Check(!image.StartInTray, "a filename containing --tray must not trigger background startup");
        Equal(imagePath, image.ImagePath!, "manual image startup must preserve the complete filename");

        foreach (string[] arguments in new[] { new[] { "--tray", imagePath }, new[] { imagePath, "--tray" } })
        {
            LaunchOptions combined = LaunchOptions.Parse(arguments);
            Check(combined.StartInTray, "the tray switch should work before or after an image path");
            Equal(imagePath, combined.ImagePath!, "tray switch must not be interpreted as the image path");
        }
        LaunchOptions repeated = LaunchOptions.Parse(new[] { "--tray", "--tray" });
        Check(repeated.StartInTray && repeated.ImagePath is null, "repeated tray switches must remain a background-only request");
        const string relativePath = @"images\例.png";
        Equal(relativePath, LaunchOptions.Parse(new[] { relativePath }).ImagePath!, "relative image paths must be preserved for opening");
    }

    private static void InvalidLaunchArguments()
    {
        foreach (string[]? arguments in new string[]?[] { null, new[] { "" }, new[] { " " },
            new[] { "--unknown" }, new[] { "--Tray" }, new[] { "first.png", "second.png" } })
        {
            bool rejected = false;
            try { LaunchOptions.Parse(arguments!); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "missing, empty, unsupported, or ambiguous launch arguments must be rejected");
        }
    }

    private static void StartupCommand()
    {
        const string executable = @"C:\利用者\画像 ツール\WinSkitch.exe";
        Equal('"' + executable + "\" --tray", StartupRegistration.BuildCommand(executable),
            "the Run command must quote the complete executable and request background startup");
        const string networkExecutable = @"\\server\共有 フォルダー\WinSkitch.exe";
        Equal('"' + networkExecutable + "\" --tray", StartupRegistration.BuildCommand(networkExecutable),
            "an absolute UNC executable path must also be preserved");
        string maximumExecutable = @"C:\" + new string('a', 244) + ".exe";
        Equal(260, StartupRegistration.BuildCommand(maximumExecutable).Length,
            "a startup command at Windows' 260-character limit must remain usable");
    }

    private static void InvalidStartupCommands()
    {
        foreach (string? path in new string?[] { null, "", "WinSkitch.exe", @".\WinSkitch.exe",
            "C:\\Apps\\WinSkitch.exe\n", "C:\\Apps\\WinSkitch.exe\r", "C:\\Apps\\\"WinSkitch.exe",
            @"C:\" + new string('a', 245) + ".exe" })
        {
            bool rejected = false;
            try { StartupRegistration.BuildCommand(path!); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "a missing, relative, or injection-prone startup path must be rejected");
        }
    }

    private static void StartupExecutable()
    {
        const string executable = @"C:\利用者\画像 ツール\WinSkitch.exe";
        const string baseDirectory = @"C:\開発 ツール\WinSkitch\bin";
        Equal(executable, StartupRegistration.ResolveExecutablePath(executable, baseDirectory, false),
            "a published app must register its actual executable rather than a build location");
        Equal(Path.Combine(baseDirectory, "WinSkitch.exe"),
            StartupRegistration.ResolveExecutablePath(@"C:\Program Files\dotnet\dotnet.exe", baseDirectory, true),
            "a dotnet-hosted launch must register the apphost executable");
        bool rejected = false;
        try { StartupRegistration.ResolveExecutablePath(@"C:\Program Files\dotnet\dotnet.exe", baseDirectory, false); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "without an apphost, registration must fail instead of registering dotnet.exe");
    }

    private static void ResidentActivation()
    {
        // Use a private test name so no running app or global production handles are touched.
        string name = @"Local\WinSkitch.Tests." + Guid.NewGuid().ToString("N");
        using var shown = new ManualResetEventSlim();
        using (var primary = new ResidentInstance(() => shown.Set(), name))
        {
            Check(primary.IsPrimary, "the first resident must retain the instance handles");
            using var duplicate = new ResidentInstance(() => { }, name);
            Check(!duplicate.IsPrimary, "a second process request must not become another resident");
            duplicate.RequestShow();
            Check(shown.Wait(TimeSpan.FromSeconds(2)), "a second manual launch must signal the existing editor to open");
        }
        using var replacement = new ResidentInstance(() => { }, name);
        Check(replacement.IsPrimary, "exiting the resident must release handles for the next launch");
    }

    private static void MainWindowRender()
    {
        // No Show(), clipboard operations, global hotkeys, or desktop reads in this test.
        var window = new MainWindow(new SavedFileHistory(Path.Combine(SavedHistoryTestDirectory("main-render"), "history.json")));
        Check(window.Content is FrameworkElement, "main window must contain a WPF visual tree");
        var content = (FrameworkElement)window.Content;
        // RenderTargetBitmap draws the content visual, so include its real window backdrop.
        if (content is Panel panel && panel.Background is null) panel.Background = window.Background;
        EditorSurface editor = Descendants(content).OfType<EditorSurface>().Single();
        editor.Load(Checkerboard(520, 320), "synthetic-qa.png");
        editor.Document.Annotations.Add(new Annotation { Kind = AnnotationKind.Arrow, Start = new Point(65, 50), End = new Point(330, 170), Color = Colors.Red, Width = 6 });
        editor.Document.Annotations.Add(new Annotation { Kind = AnnotationKind.Text, Start = new Point(75, 220), Text = "WPF 注釈テスト", Color = Colors.DodgerBlue, Width = 32 });
        editor.Refresh();
        BitmapSource screenshot = RenderElement(content, 1000, 700);
        Check(CountPixels(screenshot, p => p.A == 255) > 500_000, "complete editor layout should render an opaque usable surface");
        Check(CountPixels(screenshot, p => p.R > p.G + 60 && p.R > p.B + 60) > 300, "loaded image annotations should be visible in the main editor");
        SavePng(screenshot, "main-window.png");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Layout(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static BitmapSource RenderElement(FrameworkElement element, int width, int height)
    {
        Layout(element, width, height);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        image.Freeze();
        return image;
    }

    private readonly record struct DecodedVideo(int Width, int Height, double FramesPerSecond,
        int FrameCount, double DurationSeconds, Pixel First, Pixel Last, Pixel FirstTop, Pixel FirstBottom);

    // Decode the synthetic output through Windows' real MP4/H.264 pipeline. COM slots and
    // GUIDs follow Microsoft's WinSDK mfobjects.h, mfreadwrite.h, and mfapi.h declarations.
    // This inspector uses no desktop capture, playback window, microphone, or clipboard.
    private static class VideoReader
    {
        private const uint FirstVideoStream = 0xfffffffc;
        private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

        public static DecodedVideo Decode(string path)
        {
            uint apartment = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 2u : 0u;
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, apartment));
            bool started = false;
            IntPtr attributes = IntPtr.Zero, reader = IntPtr.Zero, nativeType = IntPtr.Zero, decodedType = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
                started = true;
                Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
                Guid processing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
                Marshal.ThrowExceptionForHR(Method<SetUInt32>(attributes, 21)(attributes, ref processing, 1));
                Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(path, attributes, out reader));
                Marshal.ThrowExceptionForHR(Method<GetNativeMediaType>(reader, 5)(reader, FirstVideoStream, 0, out nativeType));
                ulong size = UInt64(nativeType, FrameSize), rate = UInt64(nativeType, FrameRate);
                int width = checked((int)(size >> 32)), height = checked((int)(size & uint.MaxValue));
                double framesPerSecond = (rate >> 32) / (double)(uint)rate;

                Marshal.ThrowExceptionForHR(MFCreateMediaType(out decodedType));
                Guid major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), video = new("73646976-0000-0010-8000-00aa00389b71");
                Guid subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
                Marshal.ThrowExceptionForHR(Method<SetGuid>(decodedType, 24)(decodedType, ref major, ref video));
                Marshal.ThrowExceptionForHR(Method<SetGuid>(decodedType, 24)(decodedType, ref subtype, ref rgb32));
                Marshal.ThrowExceptionForHR(Method<SetCurrentMediaType>(reader, 7)(reader, FirstVideoStream, IntPtr.Zero, decodedType));
                Release(ref decodedType);
                Marshal.ThrowExceptionForHR(Method<GetCurrentMediaType>(reader, 6)(reader, FirstVideoStream, out decodedType));
                Guid defaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
                int stride;
                if (Method<GetUInt32>(decodedType, 7)(decodedType, ref defaultStride, out uint rawStride) >= 0)
                    stride = unchecked((int)rawStride);
                else
                    Marshal.ThrowExceptionForHR(MFGetStrideForBitmapInfoHeader(22, (uint)width, out stride));
                Check(Math.Abs(stride) >= checked(width * 4), "decoded RGB stride must cover an entire row");

                int frameCount = 0;
                long end = 0;
                Pixel first = default, last = default, firstTop = default, firstBottom = default;
                bool ended = false;
                for (int attempt = 0; attempt < 1024; attempt++)
                {
                    IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
                    try
                    {
                        Marshal.ThrowExceptionForHR(Method<ReadSample>(reader, 9)(reader, FirstVideoStream, 0,
                            out _, out uint flags, out long timestamp, out sample));
                        Check((flags & 1) == 0, "MP4 source reader must not report a decoding error");
                        if (sample != IntPtr.Zero)
                        {
                            Marshal.ThrowExceptionForHR(Method<GetDuration>(sample, 37)(sample, out long duration));
                            end = Math.Max(end, timestamp + duration);
                            Marshal.ThrowExceptionForHR(Method<GetBuffer>(sample, 41)(sample, out buffer));
                            Marshal.ThrowExceptionForHR(Method<LockBuffer>(buffer, 3)(buffer, out IntPtr data, out _, out uint length));
                            try
                            {
                                Check(length >= checked(Math.Abs(stride) * height), "decoded frame must contain all requested RGB pixels");
                                last = Probe(data, stride, height, width / 2, height / 2);
                                if (frameCount == 0)
                                {
                                    first = last;
                                    firstTop = Probe(data, stride, height, width / 2, height / 4);
                                    firstBottom = Probe(data, stride, height, width / 2, height * 3 / 4);
                                }
                                frameCount++;
                            }
                            finally { Marshal.ThrowExceptionForHR(Method<UnlockBuffer>(buffer, 4)(buffer)); }
                        }
                        if ((flags & 2) != 0) { ended = true; break; }
                    }
                    finally { Release(ref buffer); Release(ref sample); }
                }
                Check(ended, "MP4 must decode to end of stream within the synthetic frame limit");
                Check(frameCount > 0, "MP4 must expose at least one decoded video frame");
                return new DecodedVideo(width, height, framesPerSecond, frameCount, end / 10_000_000.0, first, last, firstTop, firstBottom);
            }
            finally
            {
                Release(ref decodedType); Release(ref nativeType); Release(ref reader); Release(ref attributes);
                if (started) MFShutdown();
                CoUninitialize();
            }
        }

        private static ulong UInt64(IntPtr attributes, Guid key)
        {
            Marshal.ThrowExceptionForHR(Method<GetUInt64>(attributes, 8)(attributes, ref key, out ulong value));
            return value;
        }

        private static Pixel Probe(IntPtr data, int stride, int height, int x, int y)
        {
            // IMFMediaBuffer::Lock exposes the lowest address; a negative stride
            // means the displayed top row starts at the last row in that buffer.
            int top = stride < 0 ? checked(-stride * (height - 1)) : 0;
            int offset = checked(top + y * stride + x * 4);
            return new Pixel(Marshal.ReadByte(data, offset + 2), Marshal.ReadByte(data, offset + 1),
                Marshal.ReadByte(data, offset), 255);
        }

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        private static void Release(ref IntPtr instance)
        {
            if (instance != IntPtr.Zero) Marshal.Release(instance);
            instance = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetUInt32(IntPtr instance, ref Guid key, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUInt32(IntPtr instance, ref Guid key, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetUInt64(IntPtr instance, ref Guid key, out ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetGuid(IntPtr instance, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNativeMediaType(IntPtr instance, uint stream, uint index, out IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCurrentMediaType(IntPtr instance, uint stream, out IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetCurrentMediaType(IntPtr instance, uint stream, IntPtr reserved, IntPtr mediaType);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadSample(IntPtr instance, uint stream, uint control, out uint actualStream,
            out uint flags, out long timestamp, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDuration(IntPtr instance, out long duration);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBuffer(IntPtr instance, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LockBuffer(IntPtr instance, out IntPtr data, out uint maximumLength, out uint currentLength);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int UnlockBuffer(IntPtr instance);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IntPtr mediaType);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFGetStrideForBitmapInfoHeader(uint format, uint width, out int stride);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr sourceReader);
    }

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);

    private static Pixel ReadPixel(BitmapSource image, int x, int y)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return new Pixel(bytes[2], bytes[1], bytes[0], bytes[3]);
    }

    private static byte[] Pixels(BitmapSource image)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        bgra.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static int ChangedPixels(BitmapSource first, BitmapSource second)
    {
        byte[] a = Pixels(first), b = Pixels(second);
        Equal(a.Length, b.Length, "images being compared must have equal dimensions");
        int changed = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) + Math.Abs(a[i + 3] - b[i + 3]) > 5) changed++;
        return changed;
    }

    private static int CountPixels(BitmapSource image, Func<Pixel, bool> predicate)
    {
        byte[] pixels = Pixels(image);
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (predicate(new Pixel(pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]))) count++;
        return count;
    }

    private static BitmapSource Solid(int width, int height, Color color, double dpi = 96) =>
        CreateBitmap(width, height, (_, _) => color, dpi);

    private static BitmapSource Checkerboard(int width, int height) => CreateBitmap(width, height,
        (x, y) => (x / 6 + y / 6) % 2 == 0 ? Color.FromRgb(215, 215, 215) : Color.FromRgb(70, 70, 70));

    private static BitmapSource CoordinatePattern(int width, int height) => CreateBitmap(width, height,
        (x, y) => Color.FromRgb((byte)(x * 3), (byte)(y * 4), (byte)(x + y)));

    private static BitmapSource CreateBitmap(int width, int height, Func<int, int, Color> color, double dpi = 96)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color c = color(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = c.A;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource image, string fileName)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(_output, fileName));
        encoder.Save(file);
    }
}
