using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

record ControlPair(Guid Id, Pixel? Thermal, Pixel? Rgb);
enum ThermalPalette { Inferno, Grayscale, BlueRed }
record RegistrationDisplay(ThermalPalette Palette, double Low, double High, double Opacity, int CellPixels,
    bool ColorBar, bool Points, bool Labels);

sealed class RegistrationProducts
{
    public int Width { get; }
    public int Height { get; }
    public double[] Values { get; }
    public byte[] Mask { get; }
    private readonly byte[] rgb;

    public RegistrationProducts(ThermalFrame frame, BitmapSource target, double[][] homography)
    {
        Width = target.PixelWidth;
        Height = target.PixelHeight;
        int n = checked(Width * Height);
        rgb = new byte[checked(n * 4)];
        new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0).CopyPixels(rgb, Width * 4, 0);
        Values = new double[n];
        Mask = new byte[n];
        var inverse = Inverse(homography);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                Values[i] = double.NaN;
                if (RegistrationWindow.Project(inverse, new Pixel(x, y)) is not Point p ||
                    p.X < -.5 || p.Y < -.5 || p.X >= frame.Width - .5 || p.Y >= frame.Height - .5) continue;
                double c = frame.At((int)Math.Floor(p.X + .5), (int)Math.Floor(p.Y + .5));
                if (!double.IsFinite(c)) continue;
                Values[i] = c;
                Mask[i] = 255;
            }
    }

    public static void ValidatePoints(Pixel[] points)
    {
        if (points.Length < 4 || points.Distinct().Count() != points.Length)
            throw new InvalidDataException("Use at least four distinct points on each image.");
        static long Cross(Pixel a, Pixel b, Pixel c) =>
            (long)(b.X - a.X) * (c.Y - a.Y) - (long)(b.Y - a.Y) * (c.X - a.X);
        if (points.Skip(2).All(p => Cross(points[0], points[1], p) == 0) ||
            (points.Length == 4 && Enumerable.Range(0, 4).Any(i =>
            {
                var three = points.Where((_, j) => i != j).ToArray();
                return Cross(three[0], three[1], three[2]) == 0;
            })))
            throw new InvalidDataException("Control points are collinear or cannot constrain a projective fit. Spread them across the image.");
    }

    private static double[][] Inverse(double[][] h)
    {
        var a = h[0]; var b = h[1]; var c = h[2];
        // Adjugate is proportional to the inverse, which suffices for homogeneous projection.
        double[][] inv = [
            [b[1]*c[2]-b[2]*c[1], a[2]*c[1]-a[1]*c[2], a[1]*b[2]-a[2]*b[1]],
            [b[2]*c[0]-b[0]*c[2], a[0]*c[2]-a[2]*c[0], a[2]*b[0]-a[0]*b[2]],
            [b[0]*c[1]-b[1]*c[0], a[1]*c[0]-a[0]*c[1], a[0]*b[1]-a[1]*b[0]]];
        double determinant = a[0]*inv[0][0] + a[1]*inv[1][0] + a[2]*inv[2][0];
        if (!double.IsFinite(determinant) || determinant == 0) throw new InvalidDataException("Singular homography.");
        double scale = inv.SelectMany(row => row).Max(Math.Abs);
        return inv.Select(row => row.Select(v => v / scale).ToArray()).ToArray();
    }

    public BitmapSource Render(string mode, RegistrationDisplay settings, ControlPair[] pairs)
    {
        bool thermal = mode == "thermal";
        var data = thermal ? new byte[rgb.Length] : (byte[])rgb.Clone();
        Color[] table = Enumerable.Range(0, 256).Select(i => ColorAt(settings.Palette, i / 255d)).ToArray();
        for (int i = 0; i < Values.Length; i++)
        {
            if (Mask[i] == 0) continue;
            if (mode == "checkerboard" && ((i % Width / settings.CellPixels + i / Width / settings.CellPixels) & 1) == 0)
                continue;
            int index = (int)(255 * Math.Clamp((Values[i] - settings.Low) / (settings.High - settings.Low), 0, 1));
            var color = table[index];
            double opacity = mode == "fusion" ? settings.Opacity : 1;
            int offset = i * 4;
            data[offset] = Mix(data[offset], color.B, opacity);
            data[offset + 1] = Mix(data[offset + 1], color.G, opacity);
            data[offset + 2] = Mix(data[offset + 2], color.R, opacity);
            data[offset + 3] = 255;
        }
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, data, Width * 4);
        if (!settings.ColorBar && !settings.Points && !settings.Labels) { image.Freeze(); return image; }
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, Width, Height));
            if (settings.Points || settings.Labels)
                for (int i = 0; i < pairs.Length; i++)
                    if (pairs[i].Rgb is Pixel point)
                    {
                        var p = new Point(point.X + .5, point.Y + .5);
                        if (settings.Points) dc.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 1), p, 4, 4);
                        if (settings.Labels) Text(dc, $"#{i + 1}", p + new Vector(5, 5));
                    }
            if (settings.ColorBar && Width >= 75 && Height >= 65)
            {
                int x = Width - 74, start = 14, height = Height - 28;
                dc.DrawRectangle(Brushes.Black, null, new Rect(x - 4, 0, 78, Height));
                for (int y = 0; y < height; y++)
                    dc.DrawRectangle(new SolidColorBrush(table[255 - (y * 255 / (height - 1))]), null,
                        new Rect(x, start + y, 12, 1));
                Text(dc, FormattableString.Invariant($"{settings.High:0.##} °C"), new Point(x + 15, start));
                Text(dc, FormattableString.Invariant($"{settings.Low:0.##} °C"), new Point(x + 15, start + height - 14));
            }
        }
        var rendered = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual); rendered.Freeze(); return rendered;
    }

    private static void Text(DrawingContext dc, string label, Point p) =>
        dc.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, 1), p);

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
    private static Color ColorAt(ThermalPalette palette, double t)
    {
        Color[] stops = palette switch
        {
            ThermalPalette.Grayscale => [Colors.Black, Colors.White],
            ThermalPalette.BlueRed => [Colors.Navy, Colors.Cyan, Colors.Yellow, Colors.DarkRed],
            _ => [Color.FromRgb(0, 0, 4), Color.FromRgb(87, 16, 110), Color.FromRgb(188, 55, 84),
                Color.FromRgb(249, 142, 9), Color.FromRgb(252, 255, 164)]
        };
        double pos = t * (stops.Length - 1);
        int i = Math.Min((int)pos, stops.Length - 2);
        var a = stops[i]; var b = stops[i + 1];
        return Color.FromRgb(Mix(a.R, b.R, pos - i), Mix(a.G, b.G, pos - i), Mix(a.B, b.B, pos - i));
    }

    public void Save(string folder, ThermalFrame frame, BitmapSource target, RegistrationFit fit,
        ControlPair[] pairs, RegistrationDisplay display, bool tiff, string? rgbSource)
    {
        void SaveImage(string name, BitmapSource image, bool useTiff = false)
        {
            BitmapEncoder encoder = useTiff ? new TiffBitmapEncoder() : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(folder, name + (useTiff ? ".tiff" : ".png")));
            encoder.Save(stream);
        }
        SaveImage("original_rgb", target, tiff);
        SaveImage("registered_thermal", Render("thermal", display, pairs), tiff);
        foreach (string mode in new[] { "fusion", "checkerboard" }) SaveImage(mode, Render(mode, display, pairs));
        SaveImage("valid_mask", BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Gray8, null, Mask, Width));
        Export.WriteMatrix(Path.Combine(folder, "registered_temperature"), Width, Height, i => Values[i], frame.PreciseValues is not null);
        File.WriteAllLines(Path.Combine(folder, "homography.csv"), fit.Homography.Select(row =>
            string.Join(",", row.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));
        var points = pairs.Select((p, i) => new { p.Id, p.Thermal, p.Rgb, residualPx = fit.Residuals[i], inlier = fit.Inliers[i] }).ToArray();
        File.WriteAllLines(Path.Combine(folder, "control_points.csv"), new[] { "id,thermal_x,thermal_y,rgb_x,rgb_y,fit_residual_px,inlier" }
            .Concat(points.Select(p => FormattableString.Invariant($"{p.Id},{p.Thermal!.X},{p.Thermal.Y},{p.Rgb!.X},{p.Rgb.Y},{p.residualPx:R},{p.inlier}"))));
        File.WriteAllText(Path.Combine(folder, "registration.json"), JsonSerializer.Serialize(new
        {
            sourceSha256 = frame.Sha256, rgbSource, Width, Height, targetGrid = "RGB pixel centres, zero based",
            direction = "thermal_to_rgb", sampling = "inverse map from target to original thermal; nearest neighbour",
            invalid = "NaN in NPY, blank in CSV, 0 in mask; transparent in thermal; RGB fallback in composite",
            valid = "255 in mask", unit = "°C", fit.Homography, points, inlierFitRmsePx = fit.Rmse,
            validation = "Fit residuals only; independent checkpoints were not measured", display
        }, Export.Json));
    }
}
