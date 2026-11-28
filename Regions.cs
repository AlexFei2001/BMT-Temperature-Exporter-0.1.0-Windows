using System.IO;

namespace BmtExporter;

record Pixel(int X, int Y);
enum RegionKind { Point, Rectangle, Polygon }
[Flags]
enum RegionRole { Unclassified = 0, Reference = 1, Material = 2 }
record Region(Guid Id, string Name, RegionKind Kind, Pixel[] Vertices)
{
    public RegionRole Role { get; init; }
    public override string ToString() => $"{Name} [{Id.ToString("N")[..8]}]";
}
record RegionDocument(int Version, string SourceSha256, int Width, int Height, Region[] Regions);
record TemperatureStats(int Count, double Mean, double Min, double Max, double StdDev)
{
    public double? Median { get; init; }
}

static class Regions
{
    public static int[] Mask(Region roi, int width, int height)
    {
        if (roi is null || roi.Id == Guid.Empty) throw new InvalidDataException("ROI has no valid ID.");
        if (string.IsNullOrWhiteSpace(roi.Name)) throw new InvalidDataException("ROI name is empty.");
        if (!Enum.IsDefined(roi.Kind)) throw new InvalidDataException("Unknown ROI geometry type.");
        if (roi.Vertices is null || roi.Vertices.Length == 0 || roi.Vertices.Length > 256)
            throw new InvalidDataException("ROI geometry requires 1 to 256 vertices.");
        if (roi.Vertices.Any(p => p is null || p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= height))
            throw new InvalidDataException("ROI vertices must be inside the original thermal grid.");
        var v = roi.Vertices;
        if (roi.Kind == RegionKind.Point && v.Length == 1) return [v[0].Y * width + v[0].X];
        if ((roi.Kind == RegionKind.Rectangle && (v.Length != 2 || v[0].X == v[1].X || v[0].Y == v[1].Y)) ||
            (roi.Kind == RegionKind.Polygon && (v.Length < 3 || v.Distinct().Count() != v.Length ||
                Enumerable.Range(0, v.Length).Sum(i => (long)v[i].X * v[(i + 1) % v.Length].Y -
                    (long)v[i].Y * v[(i + 1) % v.Length].X) == 0)) || roi.Kind == RegionKind.Point)
            throw new InvalidDataException("Draw a nondegenerate ROI with distinct vertices.");
        var mask = new List<int>();
        int xmin = v.Min(p => p.X), xmax = v.Max(p => p.X);
        int ymin = v.Min(p => p.Y), ymax = v.Max(p => p.Y);
        for (int y = ymin; y <= ymax; y++)
            for (int x = xmin; x <= xmax; x++)
                if (roi.Kind == RegionKind.Rectangle || Contains(v, x, y)) mask.Add(y * width + x);
        return mask.ToArray();
    }

    private static bool Contains(Pixel[] v, int x, int y)
    {
        bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            var a = v[j]; var b = v[i];
            long cross = (long)(x - a.X) * (b.Y - a.Y) - (long)(y - a.Y) * (b.X - a.X);
            if (cross == 0 && x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y)) return true;
            if ((a.Y > y) != (b.Y > y) && x < a.X + (double)(y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                inside = !inside;
        }
        return inside;
    }

    public static TemperatureStats Stats(float[] values, int[] mask)
        => Stats(i => values[i], mask);

    public static TemperatureStats Stats(Func<int, double> value, IEnumerable<int> mask)
    {
        var stats = FiniteStats(value, mask);
        return stats.Count > 0 ? stats : throw new InvalidDataException("ROI has no finite temperature samples.");
    }

    public static TemperatureStats FiniteStats(Func<int, double> value, IEnumerable<int> mask, bool median = false)
    {
        double mean = 0, m2 = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        int n = 0;
        List<double>? ordered = median ? [] : null;
        foreach (int i in mask)
        {
            double t = value(i);
            if (!double.IsFinite(t)) continue;
            ordered?.Add(t);
            double d = t - mean;
            mean += d / ++n;
            m2 += d * (t - mean);
            min = Math.Min(min, t); max = Math.Max(max, t);
        }
        if (n == 0) return new(0, double.NaN, double.NaN, double.NaN, double.NaN);
        ordered?.Sort();
        return new(n, mean, min, max, Math.Sqrt(Math.Max(0, m2 / n)))
        { Median = ordered is null ? null : (ordered[n / 2] + ordered[(n - 1) / 2]) / 2 };
    }

    public static Region[] Verify(RegionDocument doc, ThermalFrame frame)
    {
        VerifyGrid(doc, frame);
        foreach (var roi in doc.Regions) Mask(roi, frame.Width, frame.Height);
        if (doc.Regions.Select(r => r.Id).Distinct().Count() != doc.Regions.Length)
            throw new InvalidDataException("The ROI file contains duplicate IDs.");
        return doc.Regions;
    }

    public static void VerifyGrid(RegionDocument doc, ThermalFrame frame)
    {
        if (doc.Version != 1 || doc.SourceSha256 != frame.Sha256 || doc.Width != frame.Width ||
            doc.Height != frame.Height || doc.Regions is null)
            throw new InvalidDataException("This ROI file does not match the current BMT.");
    }
}
