namespace BmtExporter;

enum MonitorMetric
{
    Mean, Minimum, Maximum, StandardDeviation, Relative, LocalContrast, AmbientContrast,
    MaterialContrast, TemperatureRate, LocalContrastRate, PairDelta, Median, InitialChange, MaterialPair,
    RegressionTemperatureRate, RegressionLocalContrastRate
}

sealed class MonitorMetrics(MonitorDocument doc, MonitorResult result)
{
    public static readonly MonitorMetric[] Derived = [
        MonitorMetric.Relative, MonitorMetric.LocalContrast, MonitorMetric.AmbientContrast,
        MonitorMetric.MaterialContrast, MonitorMetric.TemperatureRate, MonitorMetric.LocalContrastRate];

    private readonly int reference = Monitoring.Index(doc, doc.Reference);
    private readonly int local = Monitoring.Index(doc, doc.LocalBackground);
    private readonly int material = Monitoring.Index(doc, doc.MaterialReference);
    private readonly int anomaly = Monitoring.Index(doc, doc.Anomaly);
    private readonly int materialTarget = Monitoring.Index(doc, doc.MaterialTarget);
    private readonly MonitorSetup setup = doc.Setup ?? new();
    public MonitorClock Clock { get; } = new(result, doc.Setup ?? new());
    private readonly Dictionary<Guid, Guid?> localReferences = doc.LocalReferences ?? [];
    private readonly Dictionary<Guid, int> roiIndex = doc.Rois.Regions.Select((r, i) => (r, i))
        .Where(x => x.r is not null && x.r.Id != Guid.Empty).GroupBy(x => x.r.Id)
        .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().i);
    private readonly TimedAmbient[] ambientSamples = (doc.Setup?.AmbientSamples ?? []).OrderBy(s => s.Seconds).ToArray();

    public Guid? LocalId(int j) => doc.Rois.Regions[j] is Region r && localReferences.TryGetValue(r.Id, out var id)
        ? id : doc.LocalBackground;
    private int LocalIndex(int j) => LocalId(j) is Guid id && roiIndex.TryGetValue(id, out int index) ? index : -1;
    public string LocalLabel(int j) => Label(LocalIndex(j));

    public static bool IsRate(MonitorMetric metric) => metric is MonitorMetric.TemperatureRate or MonitorMetric.LocalContrastRate
        or MonitorMetric.RegressionTemperatureRate or MonitorMetric.RegressionLocalContrastRate;
    public static bool ZeroLine(MonitorMetric metric) => IsRate(metric) || metric is MonitorMetric.InitialChange or
        MonitorMetric.Relative or MonitorMetric.LocalContrast or MonitorMetric.AmbientContrast or MonitorMetric.MaterialContrast
        or MonitorMetric.PairDelta or MonitorMetric.MaterialPair;
    public double? DisplayValue(MonitorMetric metric, int i, int j) =>
        Value(metric, i, j) * (IsRate(metric) ? MonitorSetup.Scale(setup.RateUnit) : 1);

    public string Title(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Mean => "ROI mean temperature (°C)",
        MonitorMetric.Minimum => "ROI minimum temperature (°C)",
        MonitorMetric.Maximum => "ROI maximum temperature (°C)",
        MonitorMetric.Median => "ROI median temperature (°C)",
        MonitorMetric.StandardDeviation => "ROI population standard deviation (°C)",
        MonitorMetric.Relative => $"ROI-reference temperature difference (°C) | reference: {Label(reference)}",
        MonitorMetric.InitialChange => $"Temperature change from baseline (°C) | frame {(Clock.Baseline >= 0 ? (Clock.Baseline + 1).ToString() : "unavailable")}",
        MonitorMetric.LocalContrast => $"Local thermal contrast (°C) | default: {Label(local)}; see curve references",
        MonitorMetric.AmbientContrast => ambientSamples.Length > 0
            ? $"Ambient contrast (°C) | {setup.AmbientSource ?? "external series"} · linear interpolation"
            : doc.AmbientC is double ambient
            ? FormattableString.Invariant($"Ambient contrast (°C) | external constant: {ambient:R} °C")
            : "Ambient contrast (°C) | enter an external ambient temperature",
        MonitorMetric.MaterialContrast => $"Material contrast (°C) | material reference: {Label(material)}" +
            (material >= 0 && !IsMaterial(material) ? " (requires Material role)" : ""),
        MonitorMetric.MaterialPair => $"Material pair (°C) | {Label(materialTarget)} − {Label(material)}",
        MonitorMetric.TemperatureRate => "Temperature change rate · finite difference" + RateLabel(),
        MonitorMetric.LocalContrastRate => "Local contrast rate · finite difference" + RateLabel(),
        MonitorMetric.RegressionTemperatureRate => "Temperature rate · trailing linear regression" + RateLabel(),
        MonitorMetric.RegressionLocalContrastRate => "Local contrast rate · trailing linear regression" + RateLabel(),
        _ => $"ΔT (°C) | {Label(anomaly)} − {Label(reference)}"
    };

    private string Label(int index) => index >= 0 ? $"#{index + 1} {doc.Rois.Regions[index]}" +
        (result.Outcomes[index].ValidFrames == 0 ? " (no data)" : "") : "unavailable";
    private string RateLabel() => $" (°C/{MonitorSetup.Symbol(setup.RateUnit)})" + (Clock.HasTime ? "" : " | elapsed time unavailable");

    public double? Value(MonitorMetric metric, int i, int j)
    {
        var frame = result.Frames[i];
        var sample = frame.Regions[j];
        if (sample.Count == 0) return null;
        return metric switch
        {
            MonitorMetric.Mean => sample.Mean,
            MonitorMetric.Minimum => sample.Min,
            MonitorMetric.Maximum => sample.Max,
            MonitorMetric.Median => sample.Median,
            MonitorMetric.StandardDeviation => sample.StdDev,
            MonitorMetric.Relative => Signal(sample) - SignalAt(i, reference),
            MonitorMetric.InitialChange => Signal(sample) - SignalAt(Clock.Baseline, j),
            MonitorMetric.LocalContrast => Signal(sample) - SignalAt(i, LocalIndex(j)),
            MonitorMetric.AmbientContrast => Signal(sample) - Ambient(i),
            MonitorMetric.MaterialContrast => IsMaterial(j) && material >= 0 && IsMaterial(material)
                ? Signal(sample) - SignalAt(i, material) : null,
            MonitorMetric.MaterialPair => j == materialTarget && j != material && IsMaterial(j) && material >= 0 && IsMaterial(material)
                ? Signal(sample) - SignalAt(i, material) : null,
            MonitorMetric.TemperatureRate => Rate(null, i, j),
            MonitorMetric.LocalContrastRate => Rate(MonitorMetric.LocalContrast, i, j),
            MonitorMetric.RegressionTemperatureRate => Regression(null, i, j),
            MonitorMetric.RegressionLocalContrastRate => Regression(MonitorMetric.LocalContrast, i, j),
            MonitorMetric.PairDelta => j == anomaly && j != reference ? Signal(sample) - SignalAt(i, reference) : null,
            _ => null
        };
    }

    private bool IsMaterial(int index) => (doc.Rois.Regions[index].Role & RegionRole.Material) != 0;

    public double? SignalAt(int i, int j) => i >= 0 && j >= 0 && result.Frames[i].Regions[j] is { Count: > 0 } sample
        ? Signal(sample) : null;

    private double? Signal(TemperatureStats sample) => setup.Statistic switch
    {
        SignalStatistic.Median => sample.Median, SignalStatistic.Minimum => sample.Min,
        SignalStatistic.Maximum => sample.Max, _ => sample.Mean
    };

    public double? Ambient(int i)
    {
        if (ambientSamples.Length == 0) return doc.AmbientC;
        if (Clock.Elapsed(i) is not double time || time < ambientSamples[0].Seconds || time > ambientSamples[^1].Seconds)
            return null;
        int right = Array.BinarySearch(ambientSamples, new TimedAmbient(time, 0),
            Comparer<TimedAmbient>.Create((a, b) => a.Seconds.CompareTo(b.Seconds)));
        if (right >= 0) return ambientSamples[right].Celsius;
        right = ~right;
        var a = ambientSamples[right - 1]; var b = ambientSamples[right];
        return a.Celsius + (b.Celsius - a.Celsius) * ((time - a.Seconds) / (b.Seconds - a.Seconds));
    }

    private double? Data(MonitorMetric? metric, int i, int j) => metric is MonitorMetric mode ? Value(mode, i, j) : SignalAt(i, j);
    private double? Rate(MonitorMetric? metric, int i, int j) => Clock.Interval(i) is double dt
        ? (Data(metric, i, j) - Data(metric, i - 1, j)) / dt : null;

    private double? Regression(MonitorMetric? metric, int i, int j)
    {
        if (setup.RegressionSeconds is not double seconds || !(seconds > 0) ||
            Clock.Elapsed(i) is not double end || Data(metric, i, j) is not double last) return null;
        double mx = 0, my = last, covariance = 0, variance = 0;
        int count = 1;
        for (int k = i - 1; k >= 0 && Clock.Interval(k + 1) is not null; k--)
        {
            double x = Clock.Elapsed(k)!.Value - end;
            if (x < -seconds || Data(metric, k, j) is not double y) break;
            double dx = x - mx, dy = y - my;
            mx += dx / ++count; my += dy / count;
            variance += dx * (x - mx); covariance += dx * (y - my);
        }
        return count >= 3 && variance > 0 ? covariance / variance : null;
    }

    public IEnumerable<string> Warnings()
    {
        if (!Clock.HasTime)
            yield return "Elapsed time is unavailable. Rates and time-based inputs require unique camera times or explicit reconstructed sampling.";
        else if (Clock.Source == "reconstructed_filename_order")
            yield return $"Elapsed time is reconstructed in filename order ({setup.SamplingSeconds:R} s per frame).";
        else if (!result.UsesCameraTime)
            yield return "Only unique camera timestamps consistent with filename order have elapsed time. Rates do not bridge untimed frames.";
        if (setup.OriginFrameSha256 is not null && !Clock.Origin.HasValue && Clock.HasTime)
            yield return "The selected time-origin frame is missing. The chart falls back to frame index.";
        for (int j = 0; j < doc.Rois.Regions.Length; j++)
        {
            if (result.Outcomes[j].ValidFrames == 0) continue;
            if (SignalAt(Clock.Baseline, j) is null)
                yield return $"ROI #{j + 1}: selected baseline measurement is unavailable.";
            if (LocalId(j) is not null)
            {
                int missing = Enumerable.Range(0, result.Frames.Length).Count(i => SignalAt(i, j).HasValue &&
                    !SignalAt(i, LocalIndex(j)).HasValue);
                if (missing > 0) yield return $"ROI #{j + 1}: local reference missing in {missing} target frame(s).";
            }
        }
        if (ambientSamples.Length > 0)
        {
            int missing = Enumerable.Range(0, result.Frames.Length).Count(i => Ambient(i) is null);
            if (missing > 0) yield return $"External ambient series does not cover {missing} frame(s). No extrapolation is used.";
        }
        foreach (var (id, j, name) in new[] { (doc.Reference, reference, "Reference ROI"),
            (doc.MaterialReference, material, "Material reference ROI") })
        {
            if (id is null) continue;
            if (j < 0) yield return $"{name} {id} is missing or ambiguous.";
            else if (result.Outcomes[j].ValidFrames < result.Frames.Length)
                yield return $"{name} {Label(j)} is missing measurements in {result.Frames.Length - result.Outcomes[j].ValidFrames} frame(s).";
        }
        if (material >= 0 && !IsMaterial(material))
            yield return "The selected material reference needs the Material role.";
    }
}
