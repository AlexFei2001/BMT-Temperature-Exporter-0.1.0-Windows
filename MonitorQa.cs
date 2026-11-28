using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;

namespace BmtExporter;

static class MonitorQa
{
    public static object Run(string[] sessions)
    {
        const int size = 12;
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, new byte[size * size], size);
        image.Freeze();
        DateTime start = new(2026, 11, 20, 15, 0, 0);
        var frames = Enumerable.Range(0, 3).Select(i => new ThermalFrame($"{i}.bmt", $"hash{i}", size, size,
            Enumerable.Range(0, size * size).Select(j => (float)(20 + j + i * (j + 1))).ToArray(),
            "t890-2", "test", 0, image, null, [])
        { Acquisition = AcquisitionInfo.FromExtendedHeader(new byte[248]) with { CameraClock = start.AddSeconds(i * 2) } }).ToArray();
        var sources = frames.Reverse().Select(f => new SessionFrame(f.Path, f.Sha256, f.Acquisition.CameraClock)).ToArray();
        var all = Enumerable.Range(0, 101).Select(j =>
            new Region(Guid.NewGuid(), j < 2 ? "Duplicate name" : $"ROI {j + 1}", RegionKind.Point,
                [new(j % size, j / size)]) { Role = j == 0 ? RegionRole.Material | RegionRole.Reference : RegionRole.Material }).ToArray();
        var reader = new MemoryReader(frames);
        MonitorDocument Make(Region[] rois) => new(1, sources, new(1, frames[0].Sha256, size, size, rois), rois[^1].Id, rois[0].Id)
            { LocalBackground = rois[0].Id, MaterialReference = rois[0].Id, AmbientC = 19 };

        var scale = new[] { 1, 5, 6, 10, 20, 50, 101 }.Select(count =>
        {
            var doc = Make(all[..count]);
            var saved = JsonSerializer.Deserialize<MonitorDocument>(JsonSerializer.Serialize(doc, Export.Json), Export.Json)!;
            var result = Monitoring.Run(saved, reader);
            Require(result.Outcomes.All(o => o.ValidFrames == 3), $"Analyzed {count}");
            foreach (var metric in Enum.GetValues<MonitorMetric>())
            {
                int expected = metric switch
                {
                    MonitorMetric.PairDelta => count == 1 ? 0 : 1,
                    MonitorMetric.MaterialPair or MonitorMetric.RegressionTemperatureRate or MonitorMetric.RegressionLocalContrastRate => 0,
                    _ => count
                };
                Require(MonitorPlot.Series(saved, result, metric).Length == expected, $"Series {metric}, {count}");
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(MonitorPlot.Draw(saved, result, metric)));
                using var bytes = new MemoryStream();
                png.Save(bytes);
                Require(bytes.Length > 0, $"PNG {metric}, {count}");
            }
            var complete = Csv(saved, result);
            Require(complete.Length == 1 + 3 * count && result.Frames[0].Regions[^1].Mean == 19 + count, $"CSV and ordering {count}");
            Require(complete.Skip(1).Select(row => row.Split(',')[5]).Distinct().Count() == count, "Distinct IDs in export");
            var hidden = saved with { Hidden = saved.Rois.Regions.Select(r => r.Id).ToArray() };
            Require(MonitorPlot.Series(hidden, result, MonitorMetric.Mean).Length == 0 &&
                MonitorPlot.Series(hidden, result, MonitorMetric.Mean, complete: true).Length == count &&
                Csv(hidden, result).SequenceEqual(complete), "Visibility does not affect complete export");
            using var parser = new TextFieldParser(new StringReader(complete[^1])) { Delimiters = [","] };
            var columns = parser.ReadFields()!;
            double final = 20 + (count - 1) + 2 * count;
            Require(columns[8] == final.ToString(CultureInfo.InvariantCulture) &&
                columns[23] == (final - 22).ToString(CultureInfo.InvariantCulture) &&
                columns[24] == columns[23] && columns[26] == columns[23] &&
                columns[25] == (final - 19).ToString(CultureInfo.InvariantCulture) &&
                columns[27] == (count / 2d).ToString(CultureInfo.InvariantCulture) &&
                columns[28] == ((count - 1) / 2d).ToString(CultureInfo.InvariantCulture), "Derived CSV values");
            return new { defined = count, analyzed = result.Outcomes.Length, meanSeries = count, csvRows = complete.Length - 1 };
        }).ToArray();

        var six = all[..6];
        var overlap = (Region[])six.Clone();
        overlap[5] = overlap[5] with { Vertices = overlap[0].Vertices };
        var overlapDoc = Make(overlap);
        var overlapResult = Monitoring.Run(overlapDoc, reader);
        var curves = MonitorPlot.Series(overlapDoc, overlapResult, MonitorMetric.Mean);
        Require(curves.Length == 6 && curves[0].Values.SequenceEqual(curves[5].Values), "Six series, five distinct paths");
        Require(MonitorPlot.Series(overlapDoc with { Hidden = [overlap[0].Id] }, overlapResult, MonitorMetric.Mean)
            .Any(s => s.Index == 5), "Isolate overlapping ROI");
        var deleted = Make(six[..5]) with { Reference = six[5].Id };
        var removed = Monitoring.Run(deleted, reader);
        Require(MonitorPlot.Series(deleted, removed, MonitorMetric.Mean).Length == 5 &&
            MonitorPlot.Series(deleted, removed, MonitorMetric.Relative).Length == 0, "Deleted reference");
        var bad = Make([six[0], six[1] with { Vertices = [new(-1, 0)] }, six[2], six[3], six[4],
            six[5] with { Id = six[4].Id }]);
        frames[1].Values[0] = float.NaN;
        foreach (var frame in frames) frame.Values[2] = float.PositiveInfinity;
        var issues = Monitoring.Run(bad, reader);
        Require(issues.Outcomes.Select(o => o.ValidFrames).SequenceEqual(new[] { 2, 0, 0, 3, 0, 0 }), "Invalid, missing and duplicate exclusions");
        Require(issues.Outcomes.Count(o => o.Notes.Length > 0) == 5, "Explicit warning per affected ROI");
        Require(Csv(bad, issues).Length == 19, "Excluded ROI audit rows");
        var contrast = new MonitorMetrics(bad, issues);
        Require(contrast.Value(MonitorMetric.LocalContrast, 1, 3) is null &&
            contrast.Value(MonitorMetric.LocalContrastRate, 2, 3) is null, "Missing reference data and adjacent rate gap");
        var noClockFrames = frames.Select(f => f with { Acquisition = f.Acquisition with { CameraClock = null } }).ToArray();
        var noClock = bad with { Frames = sources.Select(s => s with { CameraClock = null }).ToArray() };
        var untimed = Monitoring.Run(noClock, new MemoryReader(noClockFrames));
        Require(MonitorPlot.Series(noClock, untimed, MonitorMetric.TemperatureRate).Length == 0, "No rate without timestamps");
        var legacy = JsonSerializer.Deserialize<MonitorDocument>(JsonSerializer.Serialize(new
            { bad.Version, bad.Frames, bad.Rois, bad.Anomaly, bad.Reference }, Export.Json), Export.Json)!;
        Require(legacy.Hidden.Length == 0 && legacy.AmbientC is null && Monitoring.Run(legacy, reader).Outcomes.Length == 6,
            "Legacy session without new properties");
        var real = sessions.Select(path =>
        {
            var doc = JsonSerializer.Deserialize<MonitorDocument>(File.ReadAllText(path), Export.Json)!;
            var result = Monitoring.Run(doc, new Testo890Reader());
            int defined = doc.Rois.Regions.Length;
            var series = MonitorPlot.Series(doc, result, MonitorMetric.Mean);
            Require(series.Length == defined, "Complete chart for saved BMT experiment");
            var (a, b) = Monitoring.Pair(doc);
            int[] oldIndices = a >= 0 && b >= 0 && a != b ? [a, b] :
                Enumerable.Range(0, defined).Take(6).ToArray();
            string[] rows = Csv(doc, result);
            string archive = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "temperatures.csv");
            bool compared = File.Exists(archive);
            if (compared)
                Require(ReadLegacyColumns(File.ReadAllText(archive)).SequenceEqual(ReadLegacyColumns(string.Join("\n", rows))),
                    "Original CSV identity, order and temperature statistics");
            return new { defined, frames = result.Frames.Length, legacyMeanSeries = oldIndices.Length,
                legacyOmitted = doc.Rois.Regions.Where((_, j) => !oldIndices.Contains(j)).Select(r => r.Name),
                meanSeries = series.Length, csvRows = rows.Length - 1, comparedArchive = compared };
        }).ToArray();
        return new { passed = true, scale, real, cases = new[] { "duplicate names", "duplicate IDs", "deleted reference",
            "invalid geometry", "missing samples", "hidden curves", "overlapping paths", "legacy JSON", "untimed rates" } };
    }

    private static string[] Csv(MonitorDocument doc, MonitorResult result)
    {
        using var writer = new StringWriter(CultureInfo.GetCultureInfo("it-IT"));
        Monitoring.Csv(writer, doc, result);
        return writer.ToString().TrimEnd().Split('\n');
    }

    private static IEnumerable<string> ReadLegacyColumns(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF'))) { Delimiters = [","] };
        parser.ReadFields();
        while (!parser.EndOfData)
            yield return string.Join("\t", parser.ReadFields()!.Take(12));
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }

    private sealed class MemoryReader(ThermalFrame[] frames) : IBmtReader
    { public ThermalFrame Read(string path) => frames.Single(f => f.Path == path); }
}
