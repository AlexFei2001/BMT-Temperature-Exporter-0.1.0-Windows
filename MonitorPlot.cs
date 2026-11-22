using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class MonitorPlot
{
    private static readonly Brush[] Colors = [Brushes.DarkCyan, Brushes.IndianRed, Brushes.RoyalBlue,
        Brushes.DarkOrange, Brushes.DarkViolet, Brushes.ForestGreen];
    public static BitmapSource Draw(MonitorDocument doc, MonitorResult result)
    {
        const int width = 1200, height = 720;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var (a, b) = Monitoring.Pair(doc);
            int[] selected = a >= 0 && b >= 0 && a != b ? [a, b] :
                Enumerable.Range(0, doc.Rois.Regions.Length).Take(6).ToArray();
            var temperature = selected.Select(j => result.Frames.Select(f => f.Regions[j].Mean).ToArray()).ToArray();
            double[] x = Enumerable.Range(0, result.Frames.Length).Select(i => Monitoring.X(result, i)).ToArray();
            Text(dc, "ROI mean temperature (°C)", 18, 18, 20, Brushes.Black);
            for (int i = 0; i < selected.Length; i++)
            {
                string name = doc.Rois.Regions[selected[i]].Name;
                Text(dc, name.Length > 23 ? name[..22] + "…" : name, 70 + i * 185, 52, 14, Colors[i]);
            }
            Plot(dc, new Rect(72, 82, 1070, 242), x, temperature, result, Colors);
            var delta = result.Frames.Select(f => Monitoring.Delta(f, a, b)).ToArray();
            if (delta.All(d => d.HasValue))
            {
                Text(dc, "ΔT = anomaly mean − reference mean (°C)", 18, 373, 20, Brushes.Black);
                Plot(dc, new Rect(72, 407, 1070, 242), x,
                    [delta.Select(d => d!.Value).ToArray()], result, [Brushes.DarkSlateBlue]);
            }
            else Text(dc, "Select two different ROIs to plot ΔT.", 18, 380, 18, Brushes.DimGray);
            Text(dc, result.UsesCameraTime ? "Acquisition time (camera clock, time zone unknown)" : "Frame index (filename order)",
                72, 684, 15, Brushes.Black);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static void Plot(DrawingContext dc, Rect rect, double[] x, double[][] lines, MonitorResult result, Brush[] colors)
    {
        double ymin = lines.Min(a => a.Min()), ymax = lines.Max(a => a.Max());
        double pad = Math.Max((ymax - ymin) * .1, .025);
        ymin -= pad; ymax += pad;
        var grid = new Pen(Brushes.Gainsboro, 1);
        for (int j = 0; j <= 4; j++)
        {
            double yy = rect.Top + j * rect.Height / 4;
            dc.DrawLine(grid, new Point(rect.Left, yy), new Point(rect.Right, yy));
            Text(dc, (ymax - (ymax - ymin) * j / 4).ToString("0.00", CultureInfo.InvariantCulture), 8, yy - 10, 13, Brushes.DimGray);
        }
        for (int j = 0; j <= 4; j++)
        {
            double value = x[0] + (x[^1] - x[0]) * j / 4;
            double xx = rect.Left + j * rect.Width / 4;
            dc.DrawLine(grid, new Point(xx, rect.Top), new Point(xx, rect.Bottom));
            string label = result.UsesCameraTime ? result.Frames[0].Source.CameraClock!.Value.AddSeconds(value)
                .ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
            Text(dc, label, xx - 39, rect.Bottom + 5, 13, Brushes.DimGray);
        }
        for (int j = 0; j < lines.Length; j++)
        {
            var pen = new Pen(colors[j], 2);
            Point previous = default;
            for (int i = 0; i < x.Length; i++)
            {
                var p = new Point(rect.Left + (x[i] - x[0]) / (x[^1] - x[0]) * rect.Width,
                    rect.Bottom - (lines[j][i] - ymin) / (ymax - ymin) * rect.Height);
                if (i > 0) dc.DrawLine(pen, previous, p);
                dc.DrawEllipse(colors[j], null, p, 2.2, 2.2); previous = p;
            }
        }
    }
    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
}
