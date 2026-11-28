using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BmtExporter;

sealed class MonitorSetupWindow : Window
{
    private readonly MonitorSetup current;
    private readonly ComboBox origin, baseline, timeUnit, rateUnit, statistic;
    private readonly TextBox offset, sampling, gap, window, ambient, events;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private string? importedSource, importedText;
    public MonitorSetup Setup { get; private set; }
    private sealed record Choice(string? Hash, string Label) { public override string ToString() => Label; }

    public MonitorSetupWindow(MonitorDocument doc)
    {
        current = Setup = doc.Setup ?? new();
        Title = "Time, baseline, ambient and derivative settings"; Width = 750; Height = 720;
        MinWidth = 530; MinHeight = 500;
        var root = new DockPanel { Margin = new Thickness(12) };
        var bottom = new StackPanel();
        var actions = new WrapPanel();
        RegionEditor.Add(actions, "Apply settings", Apply);
        RegionEditor.Add(actions, "Cancel", () => DialogResult = false);
        bottom.Children.Add(status); bottom.Children.Add(actions);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var tabs = new TabControl();
        root.Children.Add(tabs); Content = root;
        var clock = Tab(tabs, "Time & baseline");
        var ordering = Monitoring.Order(doc.Frames);
        clock.Children.Add(new TextBlock { Text = ordering.UsesCameraTime
            ? "Camera timestamps determine elapsed time. Their absolute time zone is unverified."
            : "Missing or duplicate camera times. A user-entered interval reconstructs time in filename order.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) });
        var list = ordering.Frames.Select((f, i) =>
            new Choice(f.Sha256, $"#{i + 1}  {f.CameraClock:yyyy-MM-dd HH:mm:ss.fff}  {Path.GetFileName(f.Path)}")).ToArray();
        origin = Frames(list, current.OriginFrameSha256, "Use offset from first frame");
        baseline = Frames(list, current.BaselineFrameSha256, "First frame (default)");
        Field(clock, "Experimental time origin", origin);
        offset = Field(clock, "Origin offset in seconds from elapsed-time zero", Box(current.OriginSeconds));
        baseline.MinWidth = 250; Field(clock, "Temperature baseline frame", baseline);
        sampling = Field(clock, "Reconstructed interval in seconds (blank = unavailable)", Box(current.SamplingSeconds));
        timeUnit = Field(clock, "Chart time-axis unit", EnumBox(current.AxisUnit));
        statistic = Field(clock, "Signal statistic for differences and rates", EnumBox(current.Statistic));

        var rate = Tab(tabs, "Derivative");
        rateUnit = Field(rate, "Chart and additional CSV rate unit", EnumBox(current.RateUnit));
        gap = Field(rate, "Maximum valid adjacent interval (seconds, blank = no time threshold)", Box(current.MaximumGapSeconds));
        window = Field(rate, "Trailing regression window (seconds, blank = disabled)", Box(current.RegressionSeconds));
        rate.Children.Add(new TextBlock { Text = "Both methods use actual elapsed seconds and end at the right-hand frame. " +
            "Regression requires at least three contiguous observations. Finite differences are always retained.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7) });

        var air = Tab(tabs, "Ambient series");
        var airActions = new WrapPanel();
        RegionEditor.Add(airActions, "Import sensor CSV…", Import);
        RegionEditor.Add(airActions, "Clear series", () => { ambient!.Clear(); importedSource = importedText = null; });
        air.Children.Add(airActions);
        ambient = Field(air, "Manual or imported rows: elapsed_seconds,temperature_c. Seconds from elapsed-time zero.",
            Multi(string.Join("\r\n", (current.AmbientSamples ?? []).Select(a =>
                FormattableString.Invariant($"{a.Seconds:R},{a.Celsius:R}")))));
        importedText = ambient.Text;
        importedSource = current.AmbientSource;
        air.Children.Add(new TextBlock { Text = "Linear interpolation between observations. A defined series takes precedence " +
            "over the constant. Frames outside the observed range have no ambient result.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7) });

        var markers = Tab(tabs, "Events");
        events = Field(markers, "One event per line: seconds from elapsed-time zero, event name",
            Multi(string.Join("\r\n", (current.Events ?? []).Select(e =>
                FormattableString.Invariant($"{e.Seconds:R},{e.Name}")))));
        markers.Children.Add(new TextBlock { Text = "Example: 60,Heating started. Event times stay anchored when the time origin changes. " +
            "Each event starts a phase that ends at the next event.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(7) });
    }

    private static StackPanel Tab(TabControl tabs, string header)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = panel } });
        return panel;
    }

    private static T Field<T>(Panel panel, string label, T control) where T : Control
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 7, 5, 2) });
        control.Margin = new Thickness(5, 0, 5, 4);
        panel.Children.Add(control);
        return control;
    }

    private static TextBox Box(double? value) => new() { Text = value?.ToString("R", CultureInfo.InvariantCulture) ?? "" };
    private static TextBox Multi(string content) => new() { Text = content, AcceptsReturn = true, Height = 260,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static ComboBox EnumBox<T>(T value) where T : struct, Enum =>
        new() { ItemsSource = Enum.GetValues<T>(), SelectedItem = value };
    private static ComboBox Frames(Choice[] list, string? selected, string defaultLabel)
    {
        var items = list.Prepend(new Choice(null, defaultLabel)).ToList();
        if (selected is not null && !items.Any(a => a.Hash == selected)) items.Add(new(selected, "Missing saved frame"));
        return new() { ItemsSource = items, SelectedItem = items.First(a => a.Hash == selected) };
    }

    private void Import()
    {
        var file = new OpenFileDialog { Filter = "CSV sensor data (*.csv)|*.csv" };
        if (file.ShowDialog(this) != true) return;
        try
        {
            string text = File.ReadAllText(file.FileName);
            MonitorSetup.ParseAmbient(text);
            ambient.Text = importedText = text;
            importedSource = Path.GetFileName(file.FileName) + " · SHA-256 " + Export.HashFile(file.FileName);
            status.Text = $"Loaded {Path.GetFileName(file.FileName)}.";
        }
        catch (Exception e) { status.Text = e.Message; }
    }

    private void Apply()
    {
        try
        {
            if (!MonitorSetup.Number(offset.Text, out double originOffset))
                throw new InvalidDataException("Enter a finite origin offset in seconds.");
            double? Positive(TextBox field, string name)
            {
                if (string.IsNullOrWhiteSpace(field.Text)) return null;
                if (!MonitorSetup.Number(field.Text, out double value) || value <= 0)
                    throw new InvalidDataException($"{name} must be a positive decimal number.");
                return value;
            }
            var samples = MonitorSetup.ParseAmbient(ambient.Text);
            var parsedEvents = events.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line =>
                {
                    var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || !MonitorSetup.Number(parts[0], out double seconds) || parts[1].Length == 0)
                        throw new InvalidDataException("Enter event rows as seconds,event name.");
                    return new ExperimentEvent(seconds, parts[1]);
                }).OrderBy(evt => evt.Seconds).ToArray();
            if (parsedEvents.Select(e => e.Seconds).Distinct().Count() != parsedEvents.Length)
                throw new InvalidDataException("Each event that starts a phase requires a distinct time.");
            Setup = current with
            {
                OriginSeconds = originOffset, OriginFrameSha256 = ((Choice)origin.SelectedItem).Hash,
                BaselineFrameSha256 = ((Choice)baseline.SelectedItem).Hash, SamplingSeconds = Positive(sampling, "Sampling interval"),
                MaximumGapSeconds = Positive(gap, "Maximum interval"), RegressionSeconds = Positive(window, "Regression window"),
                AxisUnit = (TimeUnit)timeUnit.SelectedItem, RateUnit = (TimeUnit)rateUnit.SelectedItem,
                Statistic = (SignalStatistic)statistic.SelectedItem, AmbientSamples = samples,
                AmbientSource = samples.Length == 0 ? null : ambient.Text == importedText && importedSource is not null
                    ? importedSource : "manually entered external ambient series",
                Events = parsedEvents
            };
            DialogResult = true;
        }
        catch (Exception e) { status.Text = e.Message; }
    }
}
