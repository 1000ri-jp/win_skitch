using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        Run("synthetic desktop cropping handles negative global coordinates", DesktopCropping);
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
        var window = new MainWindow();
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
