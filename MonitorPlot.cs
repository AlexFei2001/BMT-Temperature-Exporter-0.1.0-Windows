using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class MonitorPlot
{
    private static readonly Brush[] Colors = [Brushes.DarkCyan, Brushes.IndianRed, Brushes.RoyalBlue,
        Brushes.DarkOrange, Brushes.DarkViolet, Brushes.ForestGreen];
    public static (int Index, double[] Values)[] Series(MonitorDocument doc, MonitorResult result,
        MonitorMetric metric, bool complete = false, MonitorPhase? phase = null)
    {
        var metrics = new MonitorMetrics(doc, result);
        var hidden = complete ? new HashSet<Guid>() : (doc.Hidden ?? []).ToHashSet();
        return Enumerable.Range(0, doc.Rois.Regions.Length)
            .Where(j => result.Outcomes[j].ValidFrames > 0 && !hidden.Contains(doc.Rois.Regions[j].Id))
            .Select(j => (Index: j, Values: Enumerable.Range(0, result.Frames.Length)
                .Select(i => double.IsFinite(metrics.Clock.PlotX(i)) && InPhase(metrics.Clock, phase, metric, i)
                    ? metrics.DisplayValue(metric, i, j) ?? double.NaN : double.NaN).ToArray()))
            .Where(s => s.Values.Any(double.IsFinite)).ToArray();
    }

    private static bool InPhase(MonitorClock clock, MonitorPhase? phase, MonitorMetric metric, int i)
    {
        if (phase is null) return true;
        if (!phase.Contains(clock.Elapsed(i))) return false;
        return metric switch
        {
            MonitorMetric.RegressionTemperatureRate or MonitorMetric.RegressionLocalContrastRate => true,
            _ => !MonitorMetrics.IsRate(metric) || i > 0 && phase.Contains(clock.Elapsed(i - 1))
        };
    }

    public static Pen Style(int index)
    {
        Brush brush = index < Colors.Length ? Colors[index] :
            new SolidColorBrush(Color.FromRgb(Channel(0), Channel(4), Channel(2)));
        byte Channel(int offset)
        {
            double phase = ((index * .61803398875 % 1) * 6 + offset) % 6;
            return (byte)(180 * (1 - .82 * Math.Max(0, Math.Min(Math.Min(phase, 4 - phase), 1))));
        }
        var pen = new Pen(brush, 2);
        if (index >= Colors.Length)
            pen.DashStyle = new DashStyle([2 + index % 7, 2 + index % 3], 0);
        return pen;
    }

    public static BitmapSource Draw(MonitorDocument doc, MonitorResult result, MonitorMetric metric = MonitorMetric.Mean,
        bool complete = false, MonitorPhase? phase = null)
    {
        const int width = 1200;
        var series = Series(doc, result, metric, complete, phase);
        var metrics = new MonitorMetrics(doc, result);
        var setup = doc.Setup ?? new();
        var raw = metric switch
        {
            MonitorMetric.RegressionTemperatureRate => Series(doc, result, MonitorMetric.TemperatureRate, complete, phase),
            MonitorMetric.RegressionLocalContrastRate => Series(doc, result, MonitorMetric.LocalContrastRate, complete, phase),
            _ => []
        };
        var legend = series.Select(s => s.Index).Concat(raw.Select(s => s.Index)).Distinct().ToArray();
        int height = Math.Max(720, 520 + ((legend.Length + 3) / 4) * 24);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            double[] x = Enumerable.Range(0, result.Frames.Length).Select(i =>
                phase is null || phase.Contains(metrics.Clock.Elapsed(i)) ? metrics.Clock.PlotX(i) : double.NaN).ToArray();
            Text(dc, metrics.Title(metric), 18, 18, 20, Brushes.Black, 1160);
            if (MonitorMetrics.ZeroLine(metric))
                Text(dc, $"Signal statistic: {setup.Statistic}" + (phase?.StartSeconds is not null ? $"  ·  phase: {phase.Name}" : "") + (raw.Length > 0 ?
                    $"  ·  dashed/faint: finite differences  ·  regression window: {setup.RegressionSeconds:g} s" : ""),
                    18, 46, 13, Brushes.DimGray, 1160);
            if (series.Length > 0 || raw.Length > 0)
                Plot(dc, new Rect(78, 78, 1042, 354), x, series, raw, metrics.Clock, setup.Events ?? [], MonitorMetrics.ZeroLine(metric));
            else Text(dc, "No visible ROI has values for this metric. Review the ROI list and metric inputs.", 78, 105, 18, Brushes.DimGray);
            for (int i = 0; i < legend.Length; i++)
            {
                int index = legend[i];
                double left = 20 + i % 4 * 295, top = 505 + i / 4 * 24;
                var pen = Style(index);
                dc.DrawLine(pen, new Point(left, top + 8), new Point(left + 25, top + 8));
                bool local = metric is MonitorMetric.LocalContrast or MonitorMetric.LocalContrastRate or MonitorMetric.RegressionLocalContrastRate;
                string label = $"#{index + 1} {doc.Rois.Regions[index].Name}" + (local ? $" − {metrics.LocalLabel(index)}" : "");
                Text(dc, label, left + 32, top, 14, pen.Brush, 255);
            }
            Text(dc, metrics.Clock.HasTime && metrics.Clock.Origin is not null
                ? $"Experimental time ({MonitorSetup.Symbol(setup.AxisUnit)}) · {metrics.Clock.Source}"
                : "Frame index (filename order)",
                78, 472, 15, Brushes.Black);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private static void Plot(DrawingContext dc, Rect rect, double[] x, (int Index, double[] Values)[] lines,
        (int Index, double[] Values)[] raw, MonitorClock clock, ExperimentEvent[] events, bool zero)
    {
        var values = lines.Concat(raw).SelectMany(s => s.Values).Where(double.IsFinite);
        var times = x.Where(double.IsFinite);
        double first = times.Min(), last = times.Max();
        if (first == last) { first -= .5; last += .5; }
        double ymin = values.Min(), ymax = values.Max();
        if (zero) { ymin = Math.Min(0, ymin); ymax = Math.Max(0, ymax); }
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
            double value = first + (last - first) * j / 4;
            double xx = rect.Left + j * rect.Width / 4;
            dc.DrawLine(grid, new Point(xx, rect.Top), new Point(xx, rect.Bottom));
            string label = value.ToString("0.###", CultureInfo.InvariantCulture);
            Text(dc, label, xx - 39, rect.Bottom + 5, 13, Brushes.DimGray);
        }
        double MapX(double t) => rect.Left + (t - first) / (last - first) * rect.Width;
        double MapY(double t) => rect.Bottom - (t - ymin) / (ymax - ymin) * rect.Height;
        if (zero)
        {
            double y = MapY(0);
            dc.DrawLine(new Pen(Brushes.DimGray, 1.4), new Point(rect.Left, y), new Point(rect.Right, y));
        }
        int eventIndex = 0;
        foreach (var evt in events)
        {
            if (clock.EventX(evt) is not double t || t < first || t > last) continue;
            double xx = MapX(t);
            dc.DrawLine(new Pen(Brushes.SlateGray, 1) { DashStyle = DashStyles.Dash },
                new Point(xx, rect.Top), new Point(xx, rect.Bottom));
            Text(dc, evt.Name, Math.Min(xx + 3, rect.Right - 152), rect.Top + 5 + 16 * (eventIndex++ % 5),
                12, Brushes.DarkSlateGray, 150);
        }
        dc.PushClip(new RectangleGeometry(rect));
        void Curves((int Index, double[] Values)[] plotted, bool faint)
        {
            foreach (var line in plotted)
            {
            var pen = Style(line.Index);
            if (faint) { pen.DashStyle = DashStyles.Dash; dc.PushOpacity(.45); }
            Point? previous = null;
            for (int i = 0; i < x.Length; i++)
            {
                double value = line.Values[i];
                if (!double.IsFinite(value)) { previous = null; continue; }
                var p = new Point(MapX(x[i]), MapY(value));
                if (previous is Point start) dc.DrawLine(pen, start, p);
                dc.DrawEllipse(pen.Brush, null, p, 2.2, 2.2); previous = p;
            }
            if (faint) dc.Pop();
            }
        }
        Curves(raw, true); Curves(lines, false); dc.Pop();
    }
    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush,
        double width = 1200) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, 1) { MaxTextWidth = width, MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis }, new Point(x, y));
}
