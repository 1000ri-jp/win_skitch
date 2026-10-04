using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace WinSkitch;

internal static class ToolIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new();

    public static ImageSource Create(string tool, string? variant)
    {
        string key = tool + "/" + variant;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // Transparent extent keeps every vector icon on the same scale.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            var ink = new SolidColorBrush(Color.FromRgb(230, 230, 230));
            var pen = new Pen(ink, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            switch (tool)
            {
                case "arrow":
                    dc.DrawLine(new Pen(ink, 3.5), new Point(5, 27), new Point(26, 6));
                    dc.DrawGeometry(ink, null, Geometry.Parse("M15,4 L29,3 L28,17 Z"));
                    break;
                case "text":
                    dc.DrawText(Text("A", 30, ink, true), new Point(6, -3)); break;
                case "shape" when variant == "oval": dc.DrawEllipse(null, pen, new Point(16, 16), 12, 9); break;
                case "shape" when variant == "line": dc.DrawLine(pen, new Point(5, 27), new Point(27, 5)); break;
                case "shape": dc.DrawRoundedRectangle(null, pen, new Rect(5, 7, 23, 19), variant == "rrect" ? 5 : 0, variant == "rrect" ? 5 : 0); break;
                case "pen" when variant == "highlighter": dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(253, 226, 58)), 9), new Point(5, 23), new Point(27, 10)); break;
                case "pen": dc.DrawGeometry(null, pen, Geometry.Parse("M3,23 L9,12 L15,21 L21,10 L27,19 L30,10")); break;
                case "mosaic":
                    for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++)
                    {
                        byte c = new byte[] { 130, 208, 175, 240 }[(x + y * 2) % 4];
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(c, c, c)), null, new Rect(4 + x * 8, 4 + y * 8, 8, 8));
                    }
                    break;
                case "stamp":
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(138, 138, 138)), null, new Point(16, 16), 13, 13);
                    string symbol = variant switch { "cross" => "✖", "question" => "?", "exclaim" => "!", "star" => "★", "heart" => "♥", _ => "✔" };
                    var text = Text(symbol, 21, Brushes.White, true);
                    dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
                    break;
                case "crop":
                    dc.DrawGeometry(null, pen, Geometry.Parse("M9,2 L9,23 L30,23 M2,9 L23,9 L23,30")); break;
            }
            if (tool is "shape" or "pen" or "mosaic" or "stamp") dc.DrawGeometry(ink, null, Geometry.Parse("M27,31 L32,31 L32,26 Z"));
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        Cache[key] = image;
        return image;
    }

    private static FormattedText Text(string text, double size, Brush brush, bool bold) => new(text,
        System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Symbol, Yu Gothic UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, 1);
}
