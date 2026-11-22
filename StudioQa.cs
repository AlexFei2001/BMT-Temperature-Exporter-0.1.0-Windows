using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class StudioQa
{
    public static object Run(string dataDirectory)
    {
        Rect fit = PixelView.Fit(200, 200, 10, 5);
        Require(fit == new Rect(0, 50, 200, 100), "Letterbox");
        Require(PixelView.Hit(fit, new(10, 49), 10, 5) is null, "Outside preview");
        Require(PixelView.Hit(fit, new(0, 50), 10, 5) == new Pixel(0, 0), "Top-left pixel");
        Require(PixelView.Hit(fit, new(199.9, 149.9), 10, 5) == new Pixel(9, 4), "Bottom-right pixel");
        Rect zoomed = PixelView.ZoomedBounds(fit, new Size(200, 200), 2, new Vector(40, 0));
        Require(PixelView.Hit(zoomed, new Point(100, 100), 10, 5) == new Pixel(4, 2), "Zoomed and panned selection");

        var rect = new Region(Guid.NewGuid(), "Reference", RegionKind.Rectangle, [new(1, 1), new(2, 2)]);
        var poly = new Region(Guid.NewGuid(), "Triangle", RegionKind.Polygon, [new(0, 0), new(2, 0), new(0, 2)]);
        var point = new Region(Guid.NewGuid(), "Anomaly", RegionKind.Point, [new(3, 2)]);
        var v = Enumerable.Range(0, 12).Select(x => (float)x).ToArray();
        Require(Regions.Mask(rect, 4, 3).SequenceEqual(new[] { 5, 6, 9, 10 }), "Rectangle native mask");
        Require(Regions.Mask(poly, 4, 3).SequenceEqual(new[] { 0, 1, 2, 4, 5, 8 }), "Polygon boundary mask");
        var stat = Regions.Stats(v, Regions.Mask(rect, 4, 3));
        Require(stat == new TemperatureStats(4, 7.5, 5, 10, Math.Sqrt(4.25)), "Population ROI statistics");
        Require(Regions.Stats(v, Regions.Mask(point, 4, 3)) == new TemperatureStats(1, 11, 11, 11, 0), "Point");

        DateTime t = new(2026, 11, 20, 15, 12, 52);
        byte[] header = new byte[248];
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8),
            DateTime.SpecifyKind(t, DateTimeKind.Utc).ToFileTimeUtc());
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(120), .95);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(144), 20);
        var acq = AcquisitionInfo.FromExtendedHeader(header);
        Require(acq.CameraClock == t && acq.CameraClock.Value.Kind == DateTimeKind.Unspecified && acq.Emissivity == .95, "Embedded metadata");
        var bmp = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Gray8, null, new byte[12], 4); bmp.Freeze();
        ThermalFrame f0 = new("z.bmt", "hash-first", 4, 3, v, "t890-2", "test", 0, bmp, null, []) { Acquisition = acq };
        ThermalFrame f1 = f0 with { Path = "a.bmt", Sha256 = "hash-last", Values = v.Select(x => x + 2).ToArray(),
            Acquisition = acq with { CameraClock = t.AddSeconds(30), SettingsFingerprint = "changed" } };
        SessionFrame[] list = [new(f1.Path, f1.Sha256, f1.Acquisition.CameraClock), new(f0.Path, f0.Sha256, t)];
        var (ordered, timed) = Monitoring.Order(list);
        Require(timed && ordered[0].Path == f0.Path, "Timestamp sorting");
        Require(!Monitoring.Order(list.Select(f => f with { CameraClock = t })).UsesCameraTime, "Duplicate times");
        Require(!Monitoring.Order([list[0] with { CameraClock = null }, list[1]]).UsesCameraTime, "Missing times");
        var doc = new MonitorDocument(1, ordered, new(1, f0.Sha256, 4, 3, [rect, point]), point.Id, rect.Id);
        var reloaded = JsonSerializer.Deserialize<MonitorDocument>(JsonSerializer.Serialize(doc, Export.Json), Export.Json)!;
        var result = Monitoring.Run(reloaded, new FakeReader([f0, f1]));
        Require(result.Frames[1].Notes.Contains("settings differ") && result.Frames[1].Notes.Contains("alignment unverified"), "Sequence quality flags");
        Require(Monitoring.X(result, 1) == 30 && Monitoring.Delta(result.Frames[1], 1, 0) == 3.5, "Time and delta");
        using var csv = new StringWriter(CultureInfo.GetCultureInfo("it-IT"));
        Monitoring.Csv(csv, reloaded, result);
        string[] rows = csv.ToString().Trim().Split('\n');
        string[] row = rows[^1].Split(',');
        Require(rows.Length == 5 && row[4] == "30" && row[8] == "13" && row[14] == "3.5", "CSV numerical consistency");
        Require(MonitorPlot.Draw(reloaded, result).PixelWidth == 1200, "PNG plot rendering");
        Require(f0.Values.SequenceEqual(Enumerable.Range(0, 12).Select(x => (float)x)), "Radiometry untouched");
        try { Monitoring.Run(reloaded, new FakeReader([f0, f1 with { Width = 3 }])); throw new InvalidOperationException("Dimension check"); }
        catch (InvalidDataException) { }

        var reader = new Testo890Reader();
        var actual = Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
            .Where(FolderSync.IsBmt).Take(24).Select(reader.Read).ToArray();
        var rgb = actual.FirstOrDefault(f => f.Visible is not null);
        if (rgb?.Visible is BitmapSource image)
        {
            byte[] colors = new byte[image.PixelWidth * image.PixelHeight * 3];
            image.CopyPixels(colors, image.PixelWidth * 3, 0);
            Require(image.DpiX == 96 && image.Format == PixelFormats.Bgr24 && colors.Any(x => x > 0), "Embedded RGB pixels");
        }
        var timedSequence = actual.Where(f => f.Acquisition.CameraClock.HasValue &&
            Path.GetFileName(f.Path).Contains("-26-11-20-", StringComparison.Ordinal))
            .GroupBy(f => Path.GetDirectoryName(f.Path))
            .Select(group => group.OrderBy(f => f.Acquisition.CameraClock).Take(3).ToArray())
            .FirstOrDefault(group => group.Length == 3);
        int realMonitorFrames = 0;
        if (timedSequence is not null)
        {
            var f = timedSequence[0];
            var targets = new[] { rect, point };
            var realDoc = new MonitorDocument(1, timedSequence.Reverse()
                .Select(x => new SessionFrame(x.Path, x.Sha256, x.Acquisition.CameraClock)).ToArray(),
                new(1, f.Sha256, f.Width, f.Height, targets), point.Id, rect.Id);
            var done = Monitoring.Run(realDoc, reader);
            Require(done.UsesCameraTime && done.Frames.Length == 3 && Monitoring.X(done, 2) > Monitoring.X(done, 1)
                && done.Frames[0].Regions[1].Mean == f.At(3, 2), "Real chronological BMT sequence");
            realMonitorFrames = done.Frames.Length;
        }
        string worker = Path.Combine(AppContext.BaseDirectory, "registration", "BmtRegistrationWorker.exe");
        bool registration = File.Exists(worker);
        if (registration)
        {
            var sample = BitmapSource.Create(80, 80, 96, 96, PixelFormats.Bgr24, null, new byte[80 * 80 * 3], 80 * 3);
            sample.Freeze();
            Pixel[] src = [new(8, 8), new(60, 8), new(60, 60), new(8, 60), new(30, 20)];
            Pixel[] dst = src.Select(p => new Pixel(p.X + 4, p.Y + 7)).ToArray();
            var fitted = Task.Run(() => RegistrationWorker.Fit(sample, sample, src, dst)).GetAwaiter().GetResult();
            Require(Math.Abs(fitted.Homography[0][2] - 4) < 1e-6 && Math.Abs(fitted.Homography[1][2] - 7) < 1e-6 &&
                fitted.Residuals.Max() < 1e-6 && fitted.Inliers.All(v => v), "Packaged thesis registration");
            for (int i = 0; i < src.Length; i++)
            {
                Point projected = RegistrationWindow.Project(fitted.Homography, src[i])!.Value;
                double length = (projected - new Point(dst[i].X, dst[i].Y)).Length;
                Require(Math.Abs(length - fitted.Residuals[i]) < 1e-8, "Overlay residual agrees with Python");
            }
        }
        return new { embeddedRgb = rgb?.Visible is not null, packagedRegistration = registration,
            roiAndMonitoring = "passed", realMonitorFrames };
    }
    private static void Require(bool valid, string label) { if (!valid) throw new InvalidOperationException(label); }
    private sealed class FakeReader(ThermalFrame[] frames) : IBmtReader
    {
        public ThermalFrame Read(string path) => frames.Single(f => f.Path == path);
    }
}
