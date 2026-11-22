using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BmtExporter;

record SessionFrame(string Path, string Sha256, DateTime? CameraClock);
record MonitorDocument(int Version, SessionFrame[] Frames, RegionDocument Rois, Guid? Anomaly, Guid? Reference);
record FrameStats(SessionFrame Source, TemperatureStats[] Regions, double? Similarity, string Notes);
record MonitorResult(bool UsesCameraTime, FrameStats[] Frames, string[] Warnings);

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
        var rois = Regions.Verify(doc.Rois, first);
        if (rois.Length == 0) throw new InvalidDataException("Define at least one ROI or measurement point.");
        var masks = rois.Select(r => Regions.Mask(r, first.Width, first.Height)).ToArray();
        var warnings = new List<string>();
        if (!timed) warnings.Add("Capture times are missing or duplicated. The x-axis uses file order, not elapsed time.");
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
            stats.Add(new(ordered[i], masks.Select(m => Regions.Stats(f.Values, m)).ToArray(), similarity,
                string.Join(" / ", notes)));
        }
        if (stats.Any(s => s.Notes.Length > 0))
            warnings.Add($"{stats.Count(s => s.Notes.Length > 0)} frame(s) have checks to review. See frame notes and CSV.");
        return new(timed, stats.ToArray(), warnings.ToArray());
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
        return aa > 1e-8 && bb > 1e-8 ? Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1) : null;
    }

    public static (int Anomaly, int Reference) Pair(MonitorDocument doc) =>
        (Array.FindIndex(doc.Rois.Regions, r => r.Id == doc.Anomaly), Array.FindIndex(doc.Rois.Regions, r => r.Id == doc.Reference));

    public static double X(MonitorResult result, int i) => result.UsesCameraTime
        ? (result.Frames[i].Source.CameraClock!.Value - result.Frames[0].Source.CameraClock!.Value).TotalSeconds
        : i + 1;

    public static double? Delta(FrameStats frame, int a, int b) =>
        a >= 0 && b >= 0 && a != b ? frame.Regions[a].Mean - frame.Regions[b].Mean : null;

    public static void Csv(TextWriter output, MonitorDocument doc, MonitorResult result)
    {
        output.WriteLine("frame,file,source_sha256,camera_time_unspecified,elapsed_seconds,roi_id,roi_name,count,mean_c,min_c,max_c,population_sd_c,anomaly_roi_id,reference_roi_id,delta_mean_c,static_edge_similarity,frame_notes");
        var (a, b) = Pair(doc);
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
                    result.UsesCameraTime ? N(X(result,i)) : "", r.Id.ToString(), Quote(r.Name),
                    s.Count.ToString(CultureInfo.InvariantCulture), N(s.Mean), N(s.Min), N(s.Max), N(s.StdDev),
                    a >= 0 ? doc.Anomaly.ToString()! : "", b >= 0 ? doc.Reference.ToString()! : "",
                    N(Delta(f,a,b)), N(f.Similarity), Quote(f.Notes)
                }));
            }
        }
    }
    private static string N(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Quote(string v)
    {
        // Neutralize spreadsheet formula prefixes in editable ROI names and source filenames.
        if (v.TrimStart().StartsWith('=') || v.TrimStart().StartsWith('+') ||
            v.TrimStart().StartsWith('-') || v.TrimStart().StartsWith('@')) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
