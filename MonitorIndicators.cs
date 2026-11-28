namespace BmtExporter;

record MonitorPhase(string Name, double? StartSeconds, double? EndSeconds)
{
    public bool Contains(double? elapsed) => (!StartSeconds.HasValue && !EndSeconds.HasValue) ||
        elapsed.HasValue && (!StartSeconds.HasValue || elapsed >= StartSeconds) && (!EndSeconds.HasValue || elapsed < EndSeconds);
    public override string ToString() => Name;
}

record RoiIndicators(string Phase, Guid? RoiId, string? Name, Guid? LocalReferenceId,
    int TotalFrames, int ValidFrames, int MissingFrames, double? CompletenessPercent,
    double? MaximumC, double? MinimumC, double? TotalChangeC,
    double? MaximumHeatingCPerSecond, double? MaximumCoolingCPerSecond,
    double? MeanHeatingCPerSecond, double? MeanCoolingCPerSecond,
    double? TimeOfMaximumSeconds, double? TimeOfMaximumHeatingSeconds, double? MaximumLocalContrastC,
    double? TimeOfMaximumAbsoluteLocalContrastSeconds);

static class MonitorIndicators
{
    public static MonitorPhase[] Phases(MonitorDocument doc)
    {
        var events = (doc.Setup?.Events ?? []).OrderBy(e => e.Seconds).ToArray();
        return [new("Entire experiment", null, null), .. events.Select((evt, i) =>
            new MonitorPhase(evt.Name, evt.Seconds, i + 1 < events.Length ? events[i + 1].Seconds : null))];
    }

    public static RoiIndicators[] Calculate(MonitorDocument doc, MonitorResult result)
    {
        var metrics = new MonitorMetrics(doc, result);
        return Phases(doc).SelectMany(phase =>
        {
            int[] frames = Enumerable.Range(0, result.Frames.Length).Where(i => phase.Contains(metrics.Clock.Elapsed(i))).ToArray();
            return doc.Rois.Regions.Select((r, j) =>
            {
                var temperatures = Samples(frames, i => metrics.SignalAt(i, j));
                var rates = Samples(frames.Skip(1), i => metrics.Value(MonitorMetric.TemperatureRate, i, j));
                var heating = rates.Where(x => x.Value > 0).ToArray();
                var cooling = rates.Where(x => x.Value < 0).ToArray();
                var local = Samples(frames, i => metrics.Value(MonitorMetric.LocalContrast, i, j));
                return new RoiIndicators(phase.Name, r?.Id, r?.Name, metrics.LocalId(j), frames.Length, temperatures.Length,
                    frames.Length - temperatures.Length, frames.Length == 0 ? null : 100d * temperatures.Length / frames.Length,
                    temperatures.Length == 0 ? null : temperatures.Max(x => x.Value),
                    temperatures.Length == 0 ? null : temperatures.Min(x => x.Value),
                    temperatures.Length < 2 ? null : temperatures[^1].Value - temperatures[0].Value,
                    heating.Length == 0 ? null : heating.Max(x => x.Value),
                    cooling.Length == 0 ? null : cooling.Min(x => x.Value),
                    heating.Length == 0 ? null : heating.Average(x => x.Value),
                    cooling.Length == 0 ? null : cooling.Average(x => x.Value),
                    PeakTime(temperatures, x => x), PeakTime(heating, x => x),
                    local.Length == 0 ? null : local.Max(x => x.Value), PeakTime(local, Math.Abs));
                double? PeakTime((int Index, double Value)[] samples, Func<double, double> key) =>
                    samples.Length == 0 ? null : metrics.Clock.Relative(samples.MaxBy(p => key(p.Value)).Index);
            });
        }).ToArray();
    }

    private static (int Index, double Value)[] Samples(IEnumerable<int> frames, Func<int, double?> value) =>
        frames.Select(i => (Index: i, Value: value(i))).Where(x => x.Value.HasValue)
            .Select(x => (x.Index, x.Value!.Value)).ToArray();
}
