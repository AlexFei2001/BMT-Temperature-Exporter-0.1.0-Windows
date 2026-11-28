using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class MonitorExport
{
    public static int Write(string folder, MonitorDocument doc, MonitorResult result, bool complete, MonitorPhase? phase)
    {
        Directory.CreateDirectory(folder);
        using (var writer = new StreamWriter(Path.Combine(folder, "temperatures.csv"), false, new UTF8Encoding(true)))
            Monitoring.Csv(writer, doc, result);
        var seriesCounts = new Dictionary<MonitorMetric, int>();
        foreach (var kind in Enum.GetValues<MonitorMetric>())
        {
            int count = MonitorPlot.Series(doc, result, kind, complete, phase).Length;
            seriesCounts[kind] = count;
            if (kind != MonitorMetric.Mean && count == 0) continue;
            SavePng(Path.Combine(folder, kind == MonitorMetric.Mean ? "temperatures.png" : $"{kind}.png"),
                MonitorPlot.Draw(doc, result, kind, complete, phase));
        }
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(doc, Export.Json));
        int exported = result.Outcomes.Count(o => o.ValidFrames > 0);
        var metrics = new MonitorMetrics(doc, result);
        File.WriteAllText(Path.Combine(folder, "dynamic-indicators.json"),
            JsonSerializer.Serialize(MonitorIndicators.Calculate(doc, result), Export.Json));
        File.WriteAllText(Path.Combine(folder, "analysis-metadata.json"), JsonSerializer.Serialize(new
        {
            timeSource = metrics.Clock.Source, originSecondsFromElapsedZero = metrics.Clock.Origin,
            elapsedZero = "First usable camera timestamp, or first file for reconstructed time",
            doc.Setup, doc.AmbientC, doc.Reference, doc.Anomaly, doc.LocalBackground, doc.LocalReferences,
            doc.MaterialTarget, doc.MaterialReference,
            csv = "All defined ROI/frame combinations", chartCurves = complete ? "all eligible" : "visible only",
            chartPhase = phase, chartSeriesCounts = seriesCounts,
            ambientInterpolation = "Linear within external series only; no extrapolation",
            finiteDifference = "Adjacent valid frames; rate assigned to right-hand frame",
            regression = "Trailing ordinary least squares, minimum three consecutive valid observations; assigned to right-hand frame",
            indicators = "Per phase from selected statistic; no rates crossing phase boundaries. Heating > 0, cooling < 0; arithmetic means."
        }, Export.Json));
        var report = new[] { $"Defined: {doc.Rois.Regions.Length}, analyzed: {exported}, " +
            $"failed/excluded: {doc.Rois.Regions.Length - exported}, exported with data: {exported}.",
            "CSV includes every defined ROI and frame, with blank statistics where unavailable." };
        File.WriteAllLines(Path.Combine(folder, "frame-checks.txt"), report.Concat(metrics.Warnings()).Concat(result.Warnings)
            .Concat(result.Frames.Where(f => f.Notes.Length > 0).Select(f => $"{Path.GetFileName(f.Source.Path)}: {f.Notes}"))
            .Concat(seriesCounts.Select(pair => $"{metrics.Title(pair.Key)} | {pair.Value} series with data")));
        return exported;
    }

    internal static void SavePng(string path, BitmapSource image)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
