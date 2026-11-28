using System.Globalization;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace BmtExporter;

enum TimeUnit { Seconds, Minutes, Hours }
enum SignalStatistic { Mean, Median, Minimum, Maximum }
record TimedAmbient(double Seconds, double Celsius);
record ExperimentEvent(double Seconds, string Name);

record MonitorSetup
{
    public double OriginSeconds { get; init; }
    public string? OriginFrameSha256 { get; init; }
    public string? BaselineFrameSha256 { get; init; }
    public double? SamplingSeconds { get; init; }
    public double? MaximumGapSeconds { get; init; }
    public double? RegressionSeconds { get; init; }
    public TimeUnit AxisUnit { get; init; }
    public TimeUnit RateUnit { get; init; } = TimeUnit.Minutes;
    public SignalStatistic Statistic { get; init; }
    public TimedAmbient[] AmbientSamples { get; init; } = [];
    public string? AmbientSource { get; init; }
    public ExperimentEvent[] Events { get; init; } = [];

    public static double Scale(TimeUnit unit) => unit switch { TimeUnit.Minutes => 60, TimeUnit.Hours => 3600, _ => 1 };
    public static string Symbol(TimeUnit unit) => unit switch { TimeUnit.Minutes => "min", TimeUnit.Hours => "h", _ => "s" };

    public static TimedAmbient[] ParseAmbient(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF'))) { Delimiters = [","] };
        var samples = new List<TimedAmbient>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!;
            if (samples.Count == 0 && row.Length == 2 && row[0].Equals("elapsed_seconds", StringComparison.OrdinalIgnoreCase)
                && row[1].Equals("temperature_c", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Length != 2 || !Number(row[0], out double seconds) || !Number(row[1], out double c))
                throw new InvalidDataException("Ambient input requires elapsed_seconds,temperature_c with finite decimal values.");
            samples.Add(new(seconds, c));
        }
        if (samples.Select(s => s.Seconds).Distinct().Count() != samples.Count)
            throw new InvalidDataException("Ambient input contains duplicate timestamps.");
        return samples.OrderBy(s => s.Seconds).ToArray();
    }

    public static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

sealed class MonitorClock
{
    public string Source { get; }
    public bool HasTime => Source != "frame_index";
    public double? Origin { get; }
    public int Baseline { get; }
    private readonly MonitorSetup settings;
    private readonly double?[] elapsed;

    public MonitorClock(MonitorResult result, MonitorSetup settings)
    {
        this.settings = settings;
        elapsed = new double?[result.Frames.Length];
        var usable = result.Frames.Select((f, i) => (Clock: f.Source.CameraClock, Index: i))
            .Where(f => f.Clock.HasValue).GroupBy(f => f.Clock).Where(g => g.Count() == 1)
            .Select(g => g.Single()).ToArray();
        bool monotonic = usable.Zip(usable.Skip(1), (a, b) => a.Clock < b.Clock).All(v => v);
        if (!result.UsesCameraTime && settings.SamplingSeconds is double step && double.IsFinite(step) && step > 0 &&
            double.IsFinite(step * (result.Frames.Length - 1)))
        {
            Source = "reconstructed_filename_order";
            for (int i = 0; i < elapsed.Length; i++) elapsed[i] = i * step;
        }
        else if (usable.Length > 0 && monotonic)
        {
            Source = result.UsesCameraTime ? "camera_clock" : "partial_camera_clock_filename_order";
            DateTime first = usable[0].Clock!.Value;
            foreach (var f in usable) elapsed[f.Index] = (f.Clock!.Value - first).TotalSeconds;
        }
        else Source = "frame_index";
        Baseline = Frame(settings.BaselineFrameSha256);
        Origin = settings.OriginFrameSha256 is null ? settings.OriginSeconds : Elapsed(Frame(settings.OriginFrameSha256));

        int Frame(string? hash) => hash is null ? 0 : Array.FindIndex(result.Frames, f => f.Source.Sha256 == hash);
    }

    public double? Elapsed(int i) => i >= 0 ? elapsed[i] : null;
    public double? Relative(int i) => Elapsed(i) - Origin;
    public double PlotX(int i) => !HasTime || Origin is null ? i + 1 :
        Relative(i) / MonitorSetup.Scale(settings.AxisUnit) ?? double.NaN;
    public double? EventX(ExperimentEvent evt) => HasTime ? (evt.Seconds - Origin) / MonitorSetup.Scale(settings.AxisUnit) : null;

    public double? Interval(int i)
    {
        double? delta = i > 0 ? Elapsed(i) - Elapsed(i - 1) : null;
        return delta > 0 && (settings.MaximumGapSeconds is not double max || delta <= max) ? delta : null;
    }
}
