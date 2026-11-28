using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class UpdateQa
{
    public static async Task<object> Run(string folder, string[] sessions)
    {
        var registration = await Registration(folder);
        var thermal = Thermal(folder);
        var u07 = MonitorQa.Run(sessions);
        return new { passed = true, registration, thermal, u07 };
    }

    private static async Task<object> Registration(string folder)
    {
        const int w = 96, h = 72;
        byte[] gray = new byte[w * h], rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                gray[i] = (byte)(20 + x * 2 + (y / 12 % 2) * 30);
                rgb[i * 3] = (byte)(65 + y * 2);
                rgb[i * 3 + 1] = (byte)(70 + x);
                rgb[i * 3 + 2] = (byte)(40 + x / 12 * 22);
            }
        var ir = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray, w);
        var optical = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        ir.Freeze(); optical.Freeze();
        var temperatures = gray.Select(v => 18 + v / 16f).ToArray();
        ThermalFrame frame = new("registration-test.bmt", "synthetic", w, h, temperatures, "test", "test", 0, ir, optical, []);
        var window = new RegistrationWindow(frame);
        Pixel[] src = [new(12, 10), new(75, 10), new(75, 54), new(12, 54)];
        foreach (var p in src) { window.Click(p, false); window.Click(new(p.X + 4, p.Y + 3), true); }
        await window.PendingFit;
        var fit = window.CurrentFit ?? throw new InvalidOperationException("Packaged worker did not return a fit.");
        Near(fit.Homography[0][2], 4, "Homography translation X");
        Near(fit.Homography[1][2], 3, "Homography translation Y");
        Require(fit.Rmse < 1e-6 && fit.Inliers.All(x => x), "Fit residuals");
        var point = window.ControlPoints[0];
        window.Move(point, false, new(11, 10));
        await window.PendingFit;
        Require(window.ControlPoints[0].Id == point.Id && window.ControlPoints[0].Rgb == point.Rgb,
            "Thermal edit preserves ID and RGB counterpart");
        Require(window.CurrentFit is not null && Math.Abs(window.CurrentFit.Homography[0][2] - 4) > .01, "Automatic refit");
        window.Restore(false);
        await window.PendingFit;
        Near(window.CurrentFit!.Homography[0][2], 4, "Undo recomputes original fit");
        window.Restore(true);
        await window.PendingFit;
        Require(window.ControlPoints[0].Thermal == new Pixel(11, 10) && window.CurrentFit is not null, "Redo coordinates and fit");
        window.Restore(false);
        await window.PendingFit;
        window.ApplyCoordinates("12", "11");
        await window.PendingFit;
        Require(window.ControlPoints[0].Thermal == new Pixel(12, 11), "Direct integer X/Y editing");
        window.Restore(false);
        await window.PendingFit;
        var toReplace = window.ControlPoints[0];
        window.Delete(false);
        Require(window.ControlPoints[0].Thermal is null && window.CurrentFit is null, "Single-point deletion invalidates fit");
        window.Click(toReplace.Thermal!, false);
        await window.PendingFit;
        Require(window.ControlPoints[0] == toReplace && window.CurrentFit is not null, "Missing-side replacement retains pair ID");
        fit = window.CurrentFit!;
        bool rejected = false;
        try { RegistrationProducts.ValidatePoints([new(1, 1), new(2, 2), new(3, 3), new(4, 4)]); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Reject collinear points");
        rejected = false;
        try { RegistrationProducts.ValidatePoints([src[0], src[0], src[2], src[3]]); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Reject duplicate points");

        var products = new RegistrationProducts(frame, optical, fit.Homography);
        var pairs = window.ControlPoints.ToArray();
        var display = new RegistrationDisplay(ThermalPalette.BlueRed, 18, 32, .35, 16, false, false, false);
        string dir = Directory.CreateDirectory(Path.Combine(folder, "registration-products")).FullName;
        products.Save(dir, frame, optical, fit, pairs, display, false, "QA-generated RGB");
        string tiff = Directory.CreateDirectory(Path.Combine(folder, "registration-tiff")).FullName;
        products.Save(tiff, frame, optical, fit, pairs, display, true, "QA-generated RGB");
        Require(products.Mask.Count(v => v == 255) == (w - 4) * (h - 3) &&
            double.IsNaN(products.Values[0]) && products.Values[3 * w + 4] == temperatures[0], "Target grid, mask and nearest temperature");
        foreach (string file in new[] { "original_rgb.png", "registered_thermal.png", "fusion.png", "checkerboard.png", "valid_mask.png" })
        {
            var image = Decode(Path.Combine(dir, file));
            Require(image.PixelWidth == w && image.PixelHeight == h, $"Native image dimensions: {file}");
        }
        Require(Decode(Path.Combine(tiff, "registered_thermal.tiff")).PixelWidth == w, "TIFF encoding");
        using (var npy = new BinaryReader(File.OpenRead(Path.Combine(dir, "registered_temperature.npy"))))
        {
            npy.BaseStream.Position = 8;
            int length = npy.ReadUInt16();
            npy.BaseStream.Position = 10 + length;
            Require(float.IsNaN(npy.ReadSingle()), "Masked NPY pixel is NaN");
        }
        int inside = (20 * w + 20) * 4;
        var blend = Pixels(products.Render("fusion", display, []));
        var heat = Pixels(products.Render("thermal", display, []));
        var original = Pixels(optical);
        for (int j = 0; j < 3; j++)
            Require(blend[inside + j] == Math.Round(original[inside + j] + (heat[inside + j] - original[inside + j]) * .35),
                "Opacity applied to exported native-size pixels");
        Require(Pixels(Decode(Path.Combine(dir, "fusion.png"))).SequenceEqual(blend) && heat[3] == 0 &&
            File.ReadLines(Path.Combine(dir, "registered_temperature.csv")).First().All(c => c == ','),
            "PNG equals preview, thermal transparency and blank CSV pixels");
        int thermTile = (6 * w + 20) * 4, rgbTile = (6 * w + 10) * 4;
        byte[] checker = Pixels(products.Render("checkerboard", display, []));
        Require(checker.AsSpan(thermTile, 4).SequenceEqual(heat.AsSpan(thermTile, 4)) &&
            checker.AsSpan(rgbTile, 4).SequenceEqual(original.AsSpan(rgbTile, 4)), "Checkerboard cells use correct source");
        Require(frame.Values.SequenceEqual(gray.Select(v => 18 + v / 16f)), "Native temperature matrix unchanged");
        Capture(window, Path.Combine(folder, "registration-correspondences.png"));
        window.SelectPreview();
        Capture(window, Path.Combine(folder, "registration-preview.png"));
        var editor = new RegionEditor(frame, [new(Guid.NewGuid(), "Sample ROI", RegionKind.Rectangle, [new(20, 14), new(68, 48)])]);
        Capture(editor, Path.Combine(folder, "roi-editor.png"));
        var roi = new Region(Guid.NewGuid(), "Geometry", RegionKind.Rectangle, [new(10, 9), new(18, 17)]);
        Pixel[] geometry = roi.Vertices;
        var view = new PixelView { Image = ir };
        view.Measure(new Size(360, 210)); view.Arrange(new Rect(0, 0, 360, 210));
        var expected = Regions.FiniteStats(frame.AtIndex, Regions.Mask(roi, w, h), median: true);
        foreach (double zoom in new[] { 1d, 2, 4 })
        {
            view.FitView(); view.ZoomBy(zoom);
            if (zoom > 1) view.FocusPixel(new(15, 14));
            var bounds = view.ImageBounds;
            var hit = geometry.Select(p => PixelView.Hit(bounds, view.Center(p), w, h)!).ToArray();
            Require(hit.SequenceEqual(geometry) && Regions.FiniteStats(frame.AtIndex,
                Regions.Mask(roi with { Vertices = hit }, w, h), median: true) == expected, "Zoom/pan invariance");
        }
        view.NativeScale();
        Near(view.PixelScale, 1, "100 percent pixel scale");
        var moved = RegionEditor.Translate(geometry, 3, 4, w, h);
        var resized = roi with { Vertices = [moved[0], new(moved[1].X + 2, moved[1].Y)] };
        Require(Regions.Mask(roi with { Vertices = moved }, w, h).Length == 81 &&
            Regions.Mask(resized, w, h).Length == 99, "Move and handle resize use native grid");
        return new { fit.Rmse, Width = w, Height = h, validPixels = products.Mask.Count(v => v != 0),
            edits = "move/inverse refit/undo/redo and degenerate rejection", roi = "zoom/pan/scale/edit/mask/statistics" };
    }

    private static object Thermal(string folder)
    {
        double[] times = [0, 10, 25, 40];
        float[] sample = [20, 21, 24, 22.5f];
        var bmp = BitmapSource.Create(3, 1, 96, 96, PixelFormats.Gray8, null, new byte[3], 3);
        bmp.Freeze();
        DateTime start = new(2026, 11, 24, 15, 10, 0);
        var frames = times.Select((t, i) => new ThermalFrame($"{i}.bmt", $"source-{i}", 3, 1,
            [sample[i], 18 + i, 16 + i * .5f], "test", "test", 0, bmp, null, [])
            { Acquisition = new(start.AddSeconds(t), "same", .95, 20) }).ToArray();
        var sources = frames.Select(f => new SessionFrame(f.Path, f.Sha256, f.Acquisition.CameraClock)).ToArray();
        Region[] rois = [Make("Brick", 0, RegionRole.Material), Make("Mortar", 1, RegionRole.Material | RegionRole.Reference),
            Make("Local background", 2, RegionRole.Reference)];
        Region Make(string name, int x, RegionRole role) => new(Guid.NewGuid(), name, RegionKind.Point, [new(x, 0)]) { Role = role };
        var doc = new MonitorDocument(1, sources, new(1, sources[0].Sha256, 3, 1, rois), rois[0].Id, rois[2].Id)
        {
            LocalBackground = rois[1].Id, LocalReferences = new() { [rois[1].Id] = rois[2].Id },
            MaterialReference = rois[1].Id, MaterialTarget = rois[0].Id, AmbientC = 18,
            Setup = new()
            {
                OriginFrameSha256 = sources[1].Sha256, BaselineFrameSha256 = sources[1].Sha256,
                AxisUnit = TimeUnit.Minutes, RateUnit = TimeUnit.Minutes, RegressionSeconds = 26,
                AmbientSamples = MonitorSetup.ParseAmbient("elapsed_seconds,temperature_c\n10,17\n40,20"),
                AmbientSource = "synthetic external sensor", Events = [new(0, "Heating started"), new(25, "Cooling started")]
            }
        };
        var reader = new MemoryReader(frames);
        var result = Monitoring.Run(doc, reader);
        var m = new MonitorMetrics(doc, result);
        Require(m.Clock.Relative(0) == -10 && m.Clock.PlotX(1) == 0 && m.Clock.Baseline == 1, "Independent experimental origin and baseline");
        Require(m.Value(MonitorMetric.InitialChange, 0, 0) == -1 && m.Value(MonitorMetric.InitialChange, 2, 0) == 3,
            "Temperature baseline reselected");
        Require(m.Value(MonitorMetric.LocalContrast, 2, 0) == 4 && m.Value(MonitorMetric.LocalContrast, 2, 1) == 3 &&
            m.Value(MonitorMetric.MaterialPair, 2, 0) == 4, "Per-ROI local and material references");
        Require(m.Value(MonitorMetric.AmbientContrast, 0, 0) is null && m.Ambient(2) == 18.5 &&
            m.Value(MonitorMetric.AmbientContrast, 2, 0) == 5.5, "Linear ambient interpolation without extrapolation");
        Require(new MonitorMetrics(doc with { Setup = doc.Setup with { AmbientSamples = [] } }, result)
            .Value(MonitorMetric.AmbientContrast, 0, 0) == 2, "Constant external ambient");
        Require(m.Value(MonitorMetric.TemperatureRate, 0, 0) is null, "Undefined first derivative");
        var rates = Enumerable.Range(0, 4).Select(i => m.Value(MonitorMetric.TemperatureRate, i, 0)).ToArray();
        for (int i = 1; i < 4; i++)
        {
            double expected = new[] { .1, .2, -.1 }[i - 1];
            Near(rates[i]!.Value, expected, "Irregular finite differences");
            Near(m.DisplayValue(MonitorMetric.TemperatureRate, i, 0)!.Value, expected * 60, "Per-minute conversion");
            Near(new MonitorMetrics(doc with { Setup = doc.Setup with { RateUnit = TimeUnit.Hours, AxisUnit = TimeUnit.Seconds } },
                result).DisplayValue(MonitorMetric.TemperatureRate, i, 0)!.Value, expected * 3600, "Per-hour independent of time axis");
        }
        Near(m.Value(MonitorMetric.RegressionTemperatureRate, 2, 0)!.Value, 155d / 950, "Irregular trailing OLS slope");
        Near(m.Value(MonitorMetric.LocalContrastRate, 2, 0)!.Value, 2d / 15, "Local thermal contrast rate");
        Require(m.Value(MonitorMetric.RegressionTemperatureRate, 3, 0) is null, "Trailing regression minimum samples");
        var phases = MonitorIndicators.Calculate(doc, result);
        var full = phases.Single(s => s.Phase == "Entire experiment" && s.RoiId == rois[0].Id);
        Require(full.MaximumC == 24 && full.MinimumC == 20 && full.TotalChangeC == 2.5 &&
            full.TimeOfMaximumSeconds == 15 && full.MaximumCoolingCPerSecond == -.1 &&
            full.MaximumHeatingCPerSecond == .2 && full.MeanHeatingCPerSecond is double heat && Math.Abs(heat - .15) < 1e-12 &&
            phases.Single(s => s.Phase == "Cooling started" && s.RoiId == rois[0].Id).MaximumHeatingCPerSecond is null,
            "Dynamic indicators and no rate crossing phase start");
        var hidden = doc with { Hidden = [rois[0].Id] };
        string all = Path.Combine(folder, "monitoring-all"), visible = Path.Combine(folder, "monitoring-visible");
        MonitorExport.Write(all, hidden, result, complete: true, phase: null);
        MonitorExport.Write(visible, hidden, result, complete: false, phase: null);
        Require(File.ReadAllBytes(Path.Combine(all, "temperatures.csv")).SequenceEqual(
            File.ReadAllBytes(Path.Combine(visible, "temperatures.csv"))) &&
            !File.ReadAllBytes(Path.Combine(all, "temperatures.png")).SequenceEqual(
            File.ReadAllBytes(Path.Combine(visible, "temperatures.png"))) &&
            File.Exists(Path.Combine(all, "RegressionTemperatureRate.png")), "Separate chart scope with complete CSV");
        var saved = JsonSerializer.Deserialize<MonitorDocument>(File.ReadAllText(Path.Combine(all, "session.json")), Export.Json)!;
        Require(saved.Setup.Events.SequenceEqual(doc.Setup.Events) && saved.LocalReferences[rois[1].Id] == rois[2].Id &&
            saved.Setup.BaselineFrameSha256 == doc.Setup.BaselineFrameSha256, "Save/load new session settings");
        var monitor = new MonitorWindow(reader, []);
        monitor.Install(doc, frames[0]); monitor.ShowAnalysis(result, MonitorMetric.TemperatureRate);
        Capture(monitor, Path.Combine(folder, "monitoring-interface.png"));
        Capture(new MonitorSetupWindow(doc), Path.Combine(folder, "analysis-settings.png"));

        var incomplete = frames.Select((f, i) => i == 1 ? f with { Acquisition = f.Acquisition with { CameraClock = null },
            Values = [float.NaN, float.NaN, 16.5f] } : f).ToArray();
        var unc = doc with { Frames = incomplete.Select(f => new SessionFrame(f.Path, f.Sha256, f.Acquisition.CameraClock)).ToArray() };
        var partial = Monitoring.Run(unc, new MemoryReader(incomplete));
        var missing = new MonitorMetrics(unc, partial);
        Require(missing.Clock.Source.StartsWith("partial") && missing.Clock.Elapsed(1) is null &&
            missing.Clock.Elapsed(2) == 25 && missing.Value(MonitorMetric.TemperatureRate, 2, 0) is null &&
            missing.Value(MonitorMetric.InitialChange, 2, 0) is null &&
            missing.Warnings().Any(s => s.Contains("baseline")), "Missing time and baseline warnings; no gap bridging");
        var reconstructed = new MonitorMetrics(unc with { Setup = unc.Setup with { SamplingSeconds = 10 } }, partial);
        Require(reconstructed.Clock.Source == "reconstructed_filename_order" && reconstructed.Clock.Elapsed(3) == 30 &&
            reconstructed.Value(MonitorMetric.TemperatureRate, 2, 0) is null, "Explicit reconstruction preserves data gap");
        var limit = new MonitorMetrics(doc with { Setup = doc.Setup with { MaximumGapSeconds = 12 } }, result);
        Require(limit.Value(MonitorMetric.TemperatureRate, 1, 0) is not null &&
            limit.Value(MonitorMetric.TemperatureRate, 2, 0) is null, "Maximum time gap");
        var duplicate = incomplete.Select((f, i) => i == 1 ? f with { Acquisition = f.Acquisition with { CameraClock = start } } : f).ToArray();
        var dupDoc = doc with { Frames = duplicate.Select(f => new SessionFrame(f.Path, f.Sha256, f.Acquisition.CameraClock)).ToArray() };
        var dupClock = new MonitorMetrics(dupDoc, Monitoring.Run(dupDoc, new MemoryReader(duplicate))).Clock;
        Require(dupClock.Elapsed(0) is null && dupClock.Elapsed(1) is null && dupClock.Interval(2) is null,
            "Duplicate times cannot contribute to rates");
        return new { times, temperatures = sample, ratesPerSecond = rates,
            ratesPerMinute = Enumerable.Range(0, 4).Select(i => m.DisplayValue(MonitorMetric.TemperatureRate, i, 0)),
            localAt25 = m.Value(MonitorMetric.LocalContrast, 2, 0), ambientAt25 = m.Value(MonitorMetric.AmbientContrast, 2, 0),
            regressionAt25 = m.Value(MonitorMetric.RegressionTemperatureRate, 2, 0),
            phaseCount = phases.Select(p => p.Phase).Distinct().Count() };
    }

    private static BitmapSource Decode(string path)
    {
        using var file = File.OpenRead(path);
        return BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static void Capture(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        if (root is Panel panel) panel.Background = SystemColors.WindowBrush;
        root.Measure(new Size(window.Width, window.Height));
        root.Arrange(new Rect(0, 0, window.Width, window.Height));
        root.UpdateLayout();
        var screenshot = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        screenshot.Render(root);
        MonitorExport.SavePng(path, screenshot);
        window.Content = root;
    }

    private static void Near(double value, double expected, string label) =>
        Require(Math.Abs(value - expected) < 1e-6, $"{label}: expected {expected:R}, got {value:R}");
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private sealed class MemoryReader(ThermalFrame[] frames) : IBmtReader
    { public ThermalFrame Read(string path) => frames.Single(f => f.Path == path); }
}
