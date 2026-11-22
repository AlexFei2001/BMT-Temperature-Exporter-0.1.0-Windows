using System.IO;

namespace BmtExporter;

record Pixel(int X, int Y);
enum RegionKind { Point, Rectangle, Polygon }
record Region(Guid Id, string Name, RegionKind Kind, Pixel[] Vertices)
{
    public override string ToString() => Name;
}
record RegionDocument(int Version, string SourceSha256, int Width, int Height, Region[] Regions);
record TemperatureStats(int Count, double Mean, double Min, double Max, double StdDev);

static class Regions
{
    public static int[] Mask(Region roi, int width, int height)
    {
        if (roi is null || roi.Id == Guid.Empty || string.IsNullOrWhiteSpace(roi.Name) ||
            roi.Vertices is null || !Enum.IsDefined(roi.Kind) ||
            roi.Vertices.Length < 1 || roi.Vertices.Length > 256 ||
            roi.Vertices.Any(p => p is null || p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= height))
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
    {
        if (mask.Length == 0) throw new InvalidDataException("ROI has no pixels.");
        double mean = 0, m2 = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        int n = 0;
        foreach (int i in mask)
        {
            double t = values[i], d = t - mean;
            mean += d / ++n;
            m2 += d * (t - mean);
            min = Math.Min(min, t); max = Math.Max(max, t);
        }
        return new(n, mean, min, max, Math.Sqrt(Math.Max(0, m2 / n)));
    }

    public static Region[] Verify(RegionDocument doc, ThermalFrame frame)
    {
        if (doc.Version != 1 || doc.SourceSha256 != frame.Sha256 || doc.Width != frame.Width ||
            doc.Height != frame.Height || doc.Regions is null || doc.Regions.Length > 100)
            throw new InvalidDataException("This ROI file does not match the current BMT.");
        foreach (var roi in doc.Regions) Mask(roi, frame.Width, frame.Height);
        if (doc.Regions.Select(r => r.Id).Distinct().Count() != doc.Regions.Length)
            throw new InvalidDataException("The ROI file contains duplicate IDs.");
        return doc.Regions;
    }
}
