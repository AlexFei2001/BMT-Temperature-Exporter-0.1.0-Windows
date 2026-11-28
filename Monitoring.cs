using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BmtExporter;

record SessionFrame(string Path, string Sha256, DateTime? CameraClock);
record MonitorDocument(int Version, SessionFrame[] Frames, RegionDocument Rois, Guid? Anomaly, Guid? Reference)
{
    public Guid[] Hidden { get; init; } = [];
    public Guid? LocalBackground { get; init; }
    public Guid? MaterialReference { get; init; }
    public double? AmbientC { get; init; }
    public MonitorSetup Setup { get; init; } = new();
    public Dictionary<Guid, Guid?> LocalReferences { get; init; } = [];
    public Guid? MaterialTarget { get; init; }
}
record FrameStats(SessionFrame Source, TemperatureStats[] Regions, double? Similarity, string Notes);
record MonitorResult(bool UsesCameraTime, FrameStats[] Frames, string[] Warnings, RoiOutcome[] Outcomes);
record RoiOutcome(int ValidFrames, int ExpectedPixels, string Notes);

static partial class Monitoring
{
    [GeneratedRegex(@"-(\d{2}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2})$")]
    private static partial Regex FilenameClock();

    public static (SessionFrame[] Frames, bool UsesCameraTime) Order(IEnumerable<SessionFrame> frames)
    {
        var a = frames.ToArray();
        if (a.Length < 2) throw new InvalidDataException("Choose at least two BMT files from the same session.");
        if (a.Select(f => f.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.Length)
            throw new InvalidDataException("The sequence contains duplicate source content.");
        bool timed = a.All(f => f.CameraClock.HasValue) && a.Select(f => f.CameraClock).Distinct().Count() == a.Length;
        return (timed ? a.OrderBy(f => f.CameraClock).ToArray()
            : a.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(), timed);
    }

    public static MonitorResult Run(MonitorDocument doc, IBmtReader reader)
    {
        if (doc.Version != 1 || doc.Frames is null || doc.Rois is null)
            throw new InvalidDataException("Invalid monitoring session.");
        var (ordered, timed) = Order(doc.Frames);
        var first = ReadChecked(ordered[0], reader);
        Regions.VerifyGrid(doc.Rois, first);
        var rois = doc.Rois.Regions;
        if (rois.Length == 0) throw new InvalidDataException("Define at least one ROI or measurement point.");
        var duplicates = rois.Where(r => r is not null).GroupBy(r => r.Id)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var issues = new string[rois.Length];
        var masks = new int[rois.Length][];
        for (int j = 0; j < rois.Length; j++)
        {
            try
            {
                if (rois[j] is null) throw new InvalidDataException("Missing ROI definition.");
                if (duplicates.Contains(rois[j].Id)) throw new InvalidDataException("Duplicate ROI ID.");
                masks[j] = Regions.Mask(rois[j], first.Width, first.Height);
            }
            catch (InvalidDataException e) { masks[j] = []; issues[j] = e.Message; }
        }
        var warnings = new List<string>();
        if (!timed) warnings.Add("Capture times are missing or duplicated. Frames remain in filename order; check the reported time source.");
        else warnings.Add("Time uses the embedded camera clock; its time zone and absolute accuracy are unverified.");
        warnings.Add("Fixed-camera geometry remains a user assumption. The image check compares stationary thermal edges and can be affected by real temperature change.");
        var stats = new List<FrameStats>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            var f = i == 0 ? first : ReadChecked(ordered[i], reader);
            if (f.Width != first.Width || f.Height != first.Height)
                throw new InvalidDataException($"Native dimensions differ in {Path.GetFileName(f.Path)}. No analysis was performed.");
            if (!string.Equals(f.Camera, first.Camera, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Camera model differs in {Path.GetFileName(f.Path)}.");
            var notes = new List<string>();
            if (first.Acquisition.SettingsFingerprint is null || f.Acquisition.SettingsFingerprint is null)
                notes.Add("Measurement settings unverified");
            else if (first.Acquisition.SettingsFingerprint != f.Acquisition.SettingsFingerprint)
                notes.Add("Measurement settings differ from first frame");
            var nameClock = FilenameClock().Match(Path.GetFileNameWithoutExtension(f.Path));
            if (nameClock.Success && f.Acquisition.CameraClock is DateTime embedded &&
                DateTime.TryParseExact(nameClock.Groups[1].Value, "yy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var named) && Math.Abs((named - embedded).TotalSeconds) > 2)
                notes.Add($"Filename and camera clock differ by {(named - embedded).TotalMinutes:+0.0;-0.0} min");
            double? similarity = i == 0 ? null : StaticEdgeSimilarity(first, f);
            if (i > 0 && (similarity is null || similarity < .65))
                notes.Add("Approximate alignment unverified");
            stats.Add(new(ordered[i], masks.Select(m => Regions.FiniteStats(f.AtIndex, m, median: true)).ToArray(), similarity,
                string.Join(" / ", notes)));
        }
        if (stats.Any(s => s.Notes.Length > 0))
            warnings.Add($"{stats.Count(s => s.Notes.Length > 0)} frame(s) have checks to review. See frame notes and CSV.");
        var outcomes = masks.Select((mask, j) =>
        {
            int valid = stats.Count(s => s.Regions[j].Count > 0);
            int incomplete = stats.Count(s => s.Regions[j].Count < mask.Length);
            string note = issues[j] ?? (valid == 0 ? "No finite temperature samples in any frame."
                : incomplete > 0 ? $"Missing temperature samples in {incomplete} frame(s); {valid}/{stats.Count} frames have data."
                : "");
            if (note.Length > 0) warnings.Add($"ROI #{j + 1} ({rois[j]?.Name}): {note}");
            return new RoiOutcome(valid, mask.Length, note);
        }).ToArray();
        return new(timed, stats.ToArray(), warnings.ToArray(), outcomes);
    }

    public static ThermalFrame ReadChecked(SessionFrame source, IBmtReader reader)
    {
        var frame = reader.Read(source.Path);
        if (frame.Sha256 != source.Sha256 || frame.Acquisition.CameraClock != source.CameraClock)
            throw new InvalidDataException($"A source changed after import: {Path.GetFileName(source.Path)}.");
        return frame;
    }

    private static double? StaticEdgeSimilarity(ThermalFrame a, ThermalFrame b)
    {
        // Cosine similarity of signed horizontal/vertical differences at the same native positions.
        // No displacement search, geometric transform, resampling or correction is performed.
        double dot = 0, aa = 0, bb = 0;
        for (int y = 4; y < a.Height - 4; y += 4)
            for (int x = 4; x < a.Width - 4; x += 4)
            {
                double ax = a.At(x + 2, y) - a.At(x - 2, y), ay = a.At(x, y + 2) - a.At(x, y - 2);
                double bx = b.At(x + 2, y) - b.At(x - 2, y), by = b.At(x, y + 2) - b.At(x, y - 2);
                dot += ax * bx + ay * by; aa += ax * ax + ay * ay; bb += bx * bx + by * by;
            }
        return double.IsFinite(dot) && double.IsFinite(aa) && double.IsFinite(bb) && aa > 1e-8 && bb > 1e-8
            ? Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1) : null;
    }

    public static (int Anomaly, int Reference) Pair(MonitorDocument doc) =>
        (Index(doc, doc.Anomaly), Index(doc, doc.Reference));

    public static int Index(MonitorDocument doc, Guid? id)
    {
        if (id is null || id == Guid.Empty) return -1;
        int first = Array.FindIndex(doc.Rois.Regions, r => r?.Id == id);
        return first >= 0 && Array.FindLastIndex(doc.Rois.Regions, r => r?.Id == id) == first ? first : -1;
    }

    public static double X(MonitorResult result, int i) => result.UsesCameraTime
        ? (result.Frames[i].Source.CameraClock!.Value - result.Frames[0].Source.CameraClock!.Value).TotalSeconds
        : i + 1;

    public static double? Delta(FrameStats frame, int a, int b) =>
        a >= 0 && b >= 0 && a != b && frame.Regions[a].Count > 0 && frame.Regions[b].Count > 0
            ? frame.Regions[a].Mean - frame.Regions[b].Mean : null;

    public static void Csv(TextWriter output, MonitorDocument doc, MonitorResult result)
    {
        output.WriteLine("frame,file,source_sha256,camera_time_unspecified,elapsed_seconds,roi_id,roi_name,count,mean_c,min_c,max_c,population_sd_c,anomaly_roi_id,reference_roi_id,delta_mean_c,static_edge_similarity,frame_notes,roi_number,roi_role,roi_notes,local_background_roi_id,material_reference_roi_id,external_ambient_c,relative_c,local_contrast_c,ambient_contrast_c,material_contrast_c,temperature_rate_c_per_s,local_contrast_rate_c_per_s,expected_pixels,median_c,experimental_seconds,time_source,signal_statistic,baseline_frame_sha256,selected_signal_c,initial_change_c,ambient_used_c,ambient_source,material_target_roi_id,material_pair_c,regression_temperature_rate_c_per_s,regression_local_rate_c_per_s,rate_unit,temperature_rate_selected_unit,local_rate_selected_unit,regression_temperature_rate_selected_unit,regression_local_rate_selected_unit,frame_sample_status");
        var (a, b) = Pair(doc);
        var metrics = new MonitorMetrics(doc, result);
        var setup = doc.Setup ?? new();
        for (int i = 0; i < result.Frames.Length; i++)
        {
            var f = result.Frames[i];
            for (int j = 0; j < f.Regions.Length; j++)
            {
                var s = f.Regions[j];
                var r = doc.Rois.Regions[j];
                output.WriteLine(string.Join(",", new[] {
                    (i+1).ToString(CultureInfo.InvariantCulture), Quote(Path.GetFileName(f.Source.Path)), f.Source.Sha256,
                    f.Source.CameraClock?.ToString("O",CultureInfo.InvariantCulture) ?? "",
                    N(metrics.Clock.Elapsed(i)), r?.Id.ToString() ?? "", Quote(r?.Name ?? ""),
                    s.Count.ToString(CultureInfo.InvariantCulture), N(s.Mean), N(s.Min), N(s.Max), N(s.StdDev),
                    doc.Anomaly?.ToString() ?? "", doc.Reference?.ToString() ?? "",
                    N(Delta(f,a,b)), N(f.Similarity), Quote(f.Notes),
                    (j+1).ToString(CultureInfo.InvariantCulture), Quote(r?.Role.ToString() ?? ""),
                    Quote(result.Outcomes[j].Notes), metrics.LocalId(j)?.ToString() ?? "",
                    doc.MaterialReference?.ToString() ?? "", N(doc.AmbientC)
                }.Concat(MonitorMetrics.Derived.Select(metric => N(metrics.Value(metric, i, j))))
                    .Concat([
                        result.Outcomes[j].ExpectedPixels.ToString(CultureInfo.InvariantCulture),
                        N(s.Median), N(metrics.Clock.Relative(i)), metrics.Clock.Source, setup.Statistic.ToString(),
                        metrics.Clock.Baseline >= 0 ? result.Frames[metrics.Clock.Baseline].Source.Sha256 : "",
                        N(metrics.SignalAt(i, j)), N(metrics.Value(MonitorMetric.InitialChange, i, j)),
                        N(metrics.Ambient(i)), Quote((setup.AmbientSamples?.Length ?? 0) > 0
                            ? (setup.AmbientSource ?? "external ambient series") : doc.AmbientC.HasValue ? "external constant" : ""),
                        doc.MaterialTarget?.ToString() ?? "", N(metrics.Value(MonitorMetric.MaterialPair, i, j)),
                        N(metrics.Value(MonitorMetric.RegressionTemperatureRate, i, j)),
                        N(metrics.Value(MonitorMetric.RegressionLocalContrastRate, i, j)),
                        $"C/{MonitorSetup.Symbol(setup.RateUnit)}",
                        N(metrics.DisplayValue(MonitorMetric.TemperatureRate, i, j)),
                        N(metrics.DisplayValue(MonitorMetric.LocalContrastRate, i, j)),
                        N(metrics.DisplayValue(MonitorMetric.RegressionTemperatureRate, i, j)),
                        N(metrics.DisplayValue(MonitorMetric.RegressionLocalContrastRate, i, j)),
                        s.Count == 0 ? "missing" : s.Count < result.Outcomes[j].ExpectedPixels ? "partial" : "complete"
                    ])));
            }
        }
    }
    private static string N(double? v) => v is double n && double.IsFinite(n) ? n.ToString("R", CultureInfo.InvariantCulture) : "";
    private static string Quote(string v)
    {
        // Neutralize spreadsheet formula prefixes in editable ROI names and source filenames.
        if (v.TrimStart().StartsWith('=') || v.TrimStart().StartsWith('+') ||
            v.TrimStart().StartsWith('-') || v.TrimStart().StartsWith('@')) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
