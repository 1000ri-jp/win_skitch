using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSkitch;

internal static class Program
{
    // The sample is entirely synthetic. This renderer never shows a native window,
    // starts the tray, registers hotkeys, changes startup settings, or reads the desktop/clipboard.
    [STAThread]
    private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length == 1 ? args[0] : "docs/images");
        Directory.CreateDirectory(output);
        var application = new App();
        application.InitializeComponent();
        var window = new MainWindow();
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel && panel.Background is null) panel.Background = window.Background;

        EditorSurface editor = window.Editor;
        editor.Load(SampleWorkspace(), "sample-release-check.png");
        Color red = Color.FromRgb(234, 51, 35);
        Color blue = Color.FromRgb(47, 140, 242);
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Rectangle, Start = new Point(57, 337), End = new Point(642, 415),
            Color = red, Width = 4
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Arrow, Start = new Point(790, 179), End = new Point(627, 357),
            Color = red, Width = 6
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Text, Start = new Point(743, 132), Text = "ここを確認！", Color = red, Width = 30
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Stamp, Start = new Point(620, 288), Color = Color.FromRgb(65, 172, 91),
            Width = 22, Stamp = "check"
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Highlighter, Color = Color.FromRgb(253, 226, 58), Width = 9,
            Points = new() { new Point(80, 464), new Point(258, 464) }
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Pixelate, Start = new Point(719, 309), End = new Point(976, 345), Width = 7
        });
        editor.Document.Annotations.Add(new Annotation
        {
            Kind = AnnotationKind.Text, Start = new Point(719, 365), Text = "名前はモザイクで", Color = blue, Width = 24
        });
        editor.Refresh();
        Save(editor.Document.Composite(), Path.Combine(output, "annotation-example.png"));

        // Render twice so the image dimensions/zoom label updated during OnRender is included.
        Render(content, 1280, 800);
        Save(Render(content, 1280, 800), Path.Combine(output, "editor-overview.png"));
        Save(Workflow(), Path.Combine(output, "workflow.png"));
        application.Shutdown();
        Console.WriteLine("Rendered actual WPF editor and annotation example into " + output);
    }

    private static BitmapSource SampleWorkspace()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Rect(dc, 0, 0, 1040, 640, "#EEF3F8");
            Rect(dc, 0, 0, 1040, 68, "#FFFFFF");
            Rect(dc, 40, 22, 25, 25, "#2F8CF2", 7);
            Text(dc, "A", 46, 24, 16, "#FFFFFF", true);
            Text(dc, "ASTER", 77, 21, 21, "#22324A", true);
            Text(dc, "Team workspace", 177, 28, 13, "#758398");
            Text(dc, "ホーム     プロジェクト     ファイル", 580, 27, 14, "#718096");
            Rect(dc, 923, 23, 77, 26, "#EDF2F7", 13);
            Text(dc, "SAMPLE", 937, 27, 11, "#718096", true);
            Text(dc, "リリース前の確認", 40, 97, 30, "#24334C", true);
            Text(dc, "公開前に、チェック項目をひとつずつ確認しましょう。", 42, 145, 15, "#728199");

            Card(dc, 40, 196, 620, 392);
            Text(dc, "チェックリスト", 64, 220, 19, "#2C3D56", true);
            Text(dc, "更新: 2026 / 10 / 04", 478, 226, 12, "#8C98AA");
            Line(dc, 64, 258, 636, 258, "#E8EEF4");
            Task(dc, 278, "配布ファイルを確認", "最新版を共有フォルダーに配置しました", "完了", "#ECF8F0", "#32965A");
            Task(dc, 368, "起動時の表示を確認", "初回起動と、常駐からの復帰をチェック", "要確認", "#FFF5E2", "#BA8218");
            Task(dc, 458, "README を更新", "操作の説明と、スクリーンショットを追加", "進行中", "#EAF2FD", "#3B7BD2");
            Line(dc, 64, 521, 636, 521, "#E8EEF4");
            Text(dc, "3 項目中 1 項目が完了", 65, 542, 14, "#8290A5");
            Rect(dc, 381, 544, 251, 8, "#E8EEF4", 4);
            Rect(dc, 381, 544, 84, 8, "#4F96E9", 4);

            Card(dc, 700, 196, 300, 392);
            Text(dc, "共有メモ", 724, 221, 19, "#2C3D56", true);
            Line(dc, 724, 258, 976, 258, "#E8EEF4");
            Text(dc, "担当者", 724, 280, 13, "#8C98AA");
            Text(dc, "山田 サンプル / sample@example.test", 724, 313, 15, "#44546B");
            Text(dc, "共有前に確認", 724, 431, 14, "#728199", true);
            Text(dc, "✓  変更点が伝わる\n✓  関係ない情報は隠す\n✓  画像をコピーして送る", 724, 465, 14, "#728199");
            Text(dc, "デモ用の架空データです", 40, 610, 12, "#99A4B4");
        }
        var image = new RenderTargetBitmap(1040, 640, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static BitmapSource Workflow()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Rect(dc, 0, 0, 1120, 236, "#FFFFFF");
            string[] titles = { "撮る", "伝える", "共有する" };
            string[] details = { "範囲を選んでスナップ", "矢印・文字・モザイクで注釈", "コピー / 保存 / ドラッグ" };
            string[] hints = { "Ctrl + Shift + 5", "A  矢印   T  文字   P  モザイク", "Ctrl + C  →  他のアプリへ" };
            for (int i = 0; i < 3; i++)
            {
                int x = i * 394;
                Rect(dc, x, 7, 332, 222, "#F3F7FC", 16);
                dc.DrawEllipse(Brush("#2F8CF2"), null, new Point(x + 39, 44), 15, 15);
                Text(dc, (i + 1).ToString(CultureInfo.InvariantCulture), x + 34, 32, 17, "#FFFFFF", true);
                Text(dc, titles[i], x + 25, 76, 29, "#263853", true);
                Text(dc, details[i], x + 26, 126, 17, "#677D99");
                Text(dc, hints[i], x + 26, 185, 13, "#5D83B4");
            }
            // Camera, annotation arrow, and export: original vector icons for this explainer.
            dc.DrawRoundedRectangle(null, new Pen(Brush("#2F8CF2"), 3), new Rect(256, 30, 47, 34), 5, 5);
            dc.DrawEllipse(null, new Pen(Brush("#2F8CF2"), 3), new Point(280, 47), 8, 8);
            Line(dc, 270, 26, 288, 26, "#2F8CF2");
            var arrow = new Annotation { Kind = AnnotationKind.Arrow, Start = new Point(656, 63), End = new Point(692, 28), Color = Color.FromRgb(234, 51, 35), Width = 4 };
            var arrowImage = AnnotationRenderer.Render(Blank(720, 70), new[] { arrow });
            dc.DrawImage(arrowImage, new Rect(0, 0, 720, 70));
            var exportPen = new Pen(Brush("#2F8CF2"), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawRoundedRectangle(null, exportPen, new Rect(1045, 38, 45, 28), 4, 4);
            dc.DrawLine(exportPen, new Point(1068, 51), new Point(1068, 23));
            dc.DrawLine(exportPen, new Point(1068, 23), new Point(1058, 33));
            dc.DrawLine(exportPen, new Point(1068, 23), new Point(1078, 33));
            foreach (int x in new[] { 350, 744 })
            {
                var pen = new Pen(Brush("#A7BBD5"), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(x, 114), new Point(x + 26, 114));
                dc.DrawLine(pen, new Point(x + 26, 114), new Point(x + 18, 106));
                dc.DrawLine(pen, new Point(x + 26, 114), new Point(x + 18, 122));
            }
        }
        var image = new RenderTargetBitmap(1120, 236, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static BitmapSource Blank(int width, int height) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, new byte[width * height * 4], width * 4);

    private static void Task(DrawingContext dc, int y, string title, string detail, string status, string fill, string ink)
    {
        Rect(dc, 66, y + 2, 21, 21, "#F2F6FA", 5);
        Text(dc, title, 103, y - 3, 17, "#33445E", true);
        Text(dc, detail, 103, y + 25, 13, "#8C98AA");
        Rect(dc, 526, y + 1, 76, 28, fill, 14);
        Text(dc, status, 543, y + 5, 12, ink, true);
    }

    private static void Card(DrawingContext dc, int x, int y, int w, int h)
    {
        Rect(dc, x, y + 3, w, h, "#E2E9F1", 12);
        Rect(dc, x, y, w, h, "#FFFFFF", 12);
    }

    private static Brush Brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    private static void Rect(DrawingContext dc, double x, double y, double w, double h, string fill, double radius = 0) =>
        dc.DrawRoundedRectangle(Brush(fill), null, new Rect(x, y, w, h), radius, radius);
    private static void Line(DrawingContext dc, double x1, double y1, double x2, double y2, string color) =>
        dc.DrawLine(new Pen(Brush(color), 1), new Point(x1, y1), new Point(x2, y2));
    private static void Text(DrawingContext dc, string value, double x, double y, double size, string color, bool bold = false)
    {
        var typeface = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(value, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, size, Brush(color), 1);
        dc.DrawText(text, new Point(x, y));
    }

    private static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
