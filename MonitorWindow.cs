using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BmtExporter;

sealed class MonitorWindow : Window
{
    private readonly IBmtReader reader;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox anomaly = new() { Width = 150 }, reference = new() { Width = 150 };
    private readonly ComboBox local = new() { Width = 150 }, material = new() { Width = 150 };
    private readonly ComboBox materialTarget = new() { Width = 150 }, roiReference = new() { Width = 175 };
    private readonly ComboBox phase = new() { MinWidth = 120 };
    private readonly ComboBox metric = new() { ItemsSource = MetricChoices, SelectedIndex = 0 };
    private readonly ComboBox role = new() { ItemsSource = new[] { RegionRole.Unclassified, RegionRole.Reference,
        RegionRole.Material, RegionRole.Reference | RegionRole.Material } };
    private readonly TextBox ambient = new() { Width = 85 }, rename = new() { Width = 170 };
    private readonly ListBox roiList = new();
    private readonly TextBlock counts = new() { TextWrapping = TextWrapping.Wrap };
    private int exported;
    private readonly ListBox notes = new();
    private readonly Image plot = new();
    private ThermalFrame? first;
    private MonitorDocument? doc;
    private MonitorResult? result;
    private bool busy, binding;

    public MonitorWindow(IBmtReader reader, string[] paths)
    {
        this.reader = reader;
        Title = "Fixed-camera thermal monitoring"; Width = 1120; Height = 760; MinWidth = 720; MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        var bar = new WrapPanel();
        RegionEditor.Add(bar, "Open sequence…", Open);
        RegionEditor.Add(bar, "Edit first-frame ROIs…", Edit);
        RegionEditor.Add(bar, "Analyze", Run);
        RegionEditor.Add(bar, "Export all curves…", () => SaveResults(true));
        RegionEditor.Add(bar, "Export visible curves…", () => SaveResults(false));
        RegionEditor.Add(bar, "Analysis settings…", Configure);
        RegionEditor.Add(bar, "Save session…", SaveSession);
        RegionEditor.Add(bar, "Load session…", LoadSession);
        top.Children.Add(bar);
        var pair = new WrapPanel();
        Labeled(pair, "Anomaly ROI", anomaly);
        Labeled(pair, "Reference ROI", reference);
        Labeled(pair, "Local background ROI", local);
        Labeled(pair, "Material reference ROI", material);
        Labeled(pair, "Material target ROI", materialTarget);
        var inputs = new WrapPanel();
        Labeled(inputs, "Chart metric", metric);
        Labeled(inputs, "Phase", phase);
        top.Children.Add(inputs);
        var referenceInputs = new WrapPanel();
        Labeled(referenceInputs, "External ambient °C", ambient);
        RegionEditor.Add(referenceInputs, "Apply ambient", ApplyAmbient);
        pair.Children.Add(referenceInputs);
        top.Children.Add(counts);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var tabs = new TabControl { Margin = new Thickness(0, 8, 0, 8) };
        tabs.Items.Add(new TabItem { Header = "Chart", Content = new ScrollViewer { Content = plot } });
        var roiPanel = new DockPanel();
        var actions = new WrapPanel();
        RegionEditor.Add(actions, "Toggle selected", () => VisibilityChange(null));
        RegionEditor.Add(actions, "Show all", () => VisibilityChange(true));
        RegionEditor.Add(actions, "Hide all", () => VisibilityChange(false));
        Labeled(actions, "Name", rename);
        Labeled(actions, "Role", role);
        RegionEditor.Add(actions, "Apply name and role", UpdateRoi);
        Labeled(actions, "Selected ROI's background", roiReference);
        RegionEditor.Add(actions, "Apply background", AssignReference);
        RegionEditor.Add(actions, "Use default background", RestoreDefaultReference);
        DockPanel.SetDock(actions, Dock.Top);
        roiPanel.Children.Add(actions);
        roiPanel.Children.Add(roiList);
        tabs.Items.Add(new TabItem { Header = "ROI visibility and status", Content = roiPanel });
        tabs.Items.Add(new TabItem { Header = "Reference settings", Content = pair });
        tabs.Items.Add(new TabItem { Header = "Frame checks", Content = notes });
        root.Children.Add(tabs); Content = root;
        anomaly.SelectionChanged += (_, _) => PairChanged();
        reference.SelectionChanged += (_, _) => PairChanged();
        local.SelectionChanged += (_, _) => PairChanged();
        material.SelectionChanged += (_, _) => PairChanged();
        materialTarget.SelectionChanged += (_, _) => PairChanged();
        metric.SelectionChanged += (_, _) => RefreshChart();
        phase.SelectionChanged += (_, _) => { if (!binding) RefreshChart(); };
        roiList.SelectionChanged += (_, _) =>
        {
            if (binding || doc is null || roiList.SelectedIndex < 0) return;
            var selected = doc.Rois.Regions[roiList.SelectedIndex];
            rename.Text = selected.Name;
            role.SelectedItem = selected.Role;
            Fill(roiReference, doc.LocalReferences?.TryGetValue(selected.Id, out var assigned) == true ? assigned : doc.LocalBackground);
        };
        status.Text = "Open BMT files from one acquisition session.";
        if (paths.Length >= 2) Loaded += async (_, _) => await Import(paths);
    }

    private static void Labeled(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6) });
        panel.Children.Add(control);
    }

    private sealed record MetricChoice(MonitorMetric Metric, string Label) { public override string ToString() => Label; }
    private static readonly MetricChoice[] MetricChoices =
    [
        new(MonitorMetric.Mean, "Absolute · Mean"), new(MonitorMetric.Median, "Absolute · Median"),
        new(MonitorMetric.Minimum, "Absolute · Minimum"), new(MonitorMetric.Maximum, "Absolute · Maximum"),
        new(MonitorMetric.StandardDeviation, "Statistics · Population SD"),
        new(MonitorMetric.InitialChange, "Relative · Initial change"),
        new(MonitorMetric.Relative, "Contrast · ROI reference"), new(MonitorMetric.LocalContrast, "Contrast · Local background"),
        new(MonitorMetric.AmbientContrast, "Contrast · Ambient air"), new(MonitorMetric.MaterialContrast, "Contrast · All materials"),
        new(MonitorMetric.MaterialPair, "Contrast · Selected material pair"), new(MonitorMetric.PairDelta, "Contrast · Anomaly pair"),
        new(MonitorMetric.TemperatureRate, "Rate · Temperature"), new(MonitorMetric.LocalContrastRate, "Rate · Local contrast"),
        new(MonitorMetric.RegressionTemperatureRate, "Rate · Temperature regression + raw"),
        new(MonitorMetric.RegressionLocalContrastRate, "Rate · Local contrast regression + raw")
    ];
    private MonitorMetric Metric => (metric.SelectedItem as MetricChoice)?.Metric ?? MonitorMetric.Mean;
    private MonitorPhase? Phase => phase.SelectedItem as MonitorPhase;

    private void Configure()
    {
        if (busy || doc is null) return;
        var dialog = new MonitorSetupWindow(doc) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        doc = doc with { Setup = dialog.Setup };
        exported = 0;
        Bind();
    }

    private void AssignReference() => SetLocalReference(useDefault: false);
    private void RestoreDefaultReference() => SetLocalReference(useDefault: true);
    private void SetLocalReference(bool useDefault)
    {
        if (busy || doc is null || roiList.SelectedIndex < 0) return;
        var id = doc.Rois.Regions[roiList.SelectedIndex].Id;
        var map = new Dictionary<Guid, Guid?>(doc.LocalReferences ?? []);
        if (useDefault) map.Remove(id);
        else map[id] = Selection(roiReference, map.GetValueOrDefault(id));
        doc = doc with { LocalReferences = map };
        exported = 0;
        RefreshChart();
    }

    private void ApplyAmbient()
    {
        if (busy || doc is null) return;
        double? value = null;
        if (!string.IsNullOrWhiteSpace(ambient.Text))
        {
            if ((!double.TryParse(ambient.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) &&
                !double.TryParse(ambient.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) ||
                !double.IsFinite(parsed))
            { status.Text = "Enter a finite external ambient temperature in Celsius, or leave it blank."; return; }
            value = parsed;
        }
        doc = doc with { AmbientC = value };
        exported = 0;
        RefreshChart();
    }

    private void VisibilityChange(bool? show)
    {
        if (busy || doc is null) return;
        var hidden = (doc.Hidden ?? []).ToHashSet();
        if (show is true) hidden.Clear();
        else if (show is false) hidden.UnionWith(doc.Rois.Regions.Select(r => r.Id));
        else if (roiList.SelectedIndex >= 0)
        {
            var id = doc.Rois.Regions[roiList.SelectedIndex].Id;
            if (!hidden.Remove(id)) hidden.Add(id);
        }
        doc = doc with { Hidden = hidden.ToArray() };
        RefreshChart();
    }

    private void UpdateRoi()
    {
        if (busy || doc is null || roiList.SelectedIndex < 0 || string.IsNullOrWhiteSpace(rename.Text)) return;
        int index = roiList.SelectedIndex;
        var rois = (Region[])doc.Rois.Regions.Clone();
        rois[index] = rois[index] with { Name = rename.Text.Trim(), Role = (RegionRole)(role.SelectedItem ?? RegionRole.Unclassified) };
        doc = doc with { Rois = doc.Rois with { Regions = rois } };
        exported = 0;
        Bind();
    }

    private async void Open()
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "Testo BMT (*.bmt)|*.bmt", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await Import(dialog.FileNames);
    }
    private async Task Import(string[] paths)
    {
        if (busy) return;
        busy = true; status.Text = $"Checking {paths.Length} BMT files…";
        try
        {
            var imported = await Task.Run(() =>
            {
                var sequence = paths.Select(p =>
                {
                    var f = reader.Read(p);
                    return new SessionFrame(Path.GetFullPath(p), f.Sha256, f.Acquisition.CameraClock);
                }).ToArray();
                var order = Monitoring.Order(sequence);
                return (order.Frames, first: Monitoring.ReadChecked(order.Frames[0], reader));
            });
            var f = imported.first;
            Install(new(1, imported.Frames, new(1, f.Sha256, f.Width, f.Height, []), null, null), f);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; }
    }
    internal void Install(MonitorDocument newDoc, ThermalFrame f)
    {
        first = f; doc = newDoc; ResetResults();
        ambient.Text = doc.AmbientC?.ToString(CultureInfo.CurrentCulture) ?? "";
        Bind();
        foreach (var item in doc.Frames)
            notes.Items.Add($"{item.CameraClock?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "Unknown clock"}   {Path.GetFileName(item.Path)}");
        status.Text = $"{doc.Frames.Length} files. First frame: {Path.GetFileName(f.Path)}. Edit ROIs, then analyze.";
    }
    private void Edit()
    {
        if (busy || doc is null || first is null) return;
        var edit = new RegionEditor(first, doc.Rois.Regions) { Owner = this };
        if (edit.ShowDialog() != true) return;
        doc = doc with { Rois = doc.Rois with { Regions = edit.Definitions },
            Hidden = (doc.Hidden ?? []).Intersect(edit.Definitions.Select(r => r.Id)).ToArray() };
        ResetResults(); Bind();
        status.Text = $"{edit.Definitions.Length} ROI(s) defined. Click Analyze to apply them to every frame.";
    }
    private void Bind()
    {
        if (doc is null) return;
        binding = true;
        try
        {
            Fill(anomaly, doc.Anomaly);
            Fill(reference, doc.Reference);
            Fill(local, doc.LocalBackground);
            Fill(material, doc.MaterialReference, r => (r.Role & RegionRole.Material) != 0);
            Fill(materialTarget, doc.MaterialTarget, r => (r.Role & RegionRole.Material) != 0);
            var phases = MonitorIndicators.Phases(doc);
            var previous = Phase;
            phase.ItemsSource = phases;
            phase.SelectedItem = phases.FirstOrDefault(p => p == previous) ?? phases[0];
        }
        finally { binding = false; }
        RefreshChart();
    }

    private void Fill(ComboBox combo, Guid? id, Func<Region, bool>? filter = null)
    {
        if (doc is null) return;
        var current = doc;
        var choices = current.Rois.Regions.GroupBy(r => r.Id).Where(g => g.Key != Guid.Empty && g.Count() == 1)
            .Select(g => g.First()).Where(r => r.Id == id || filter is null || filter(r)).ToArray();
        string? missing = id.HasValue && !choices.Any(r => r.Id == id) ? $"Missing or ambiguous: {id}" : null;
        combo.ItemsSource = (missing is null ? new object[] { "None" } : new object[] { "None", missing }).Concat(choices);
        combo.SelectedItem = (object?)choices.FirstOrDefault(r => r.Id == id) ?? missing ?? "None";
    }
    private void PairChanged()
    {
        if (binding || busy || doc is null) return;
        doc = doc with { Anomaly = Selection(anomaly, doc.Anomaly), Reference = Selection(reference, doc.Reference),
            LocalBackground = Selection(local, doc.LocalBackground), MaterialReference = Selection(material, doc.MaterialReference),
            MaterialTarget = Selection(materialTarget, doc.MaterialTarget) };
        exported = 0;
        RefreshChart();
    }

    private static Guid? Selection(ComboBox combo, Guid? previous) =>
        combo.SelectedItem is Region r ? r.Id : Equals(combo.SelectedItem, "None") ? null : previous;

    private void RefreshChart()
    {
        if (doc is null) return;
        var metrics = result is null ? null : new MonitorMetrics(doc, result);
        if (result is not null) RefreshNotes(metrics!);
        var visible = result is null ? new HashSet<int>() :
            MonitorPlot.Series(doc, result, Metric, phase: Phase).Select(s => s.Index).ToHashSet();
        if (result is not null && Metric is MonitorMetric.RegressionTemperatureRate or MonitorMetric.RegressionLocalContrastRate)
            visible.UnionWith(MonitorPlot.Series(doc, result, Metric == MonitorMetric.RegressionTemperatureRate
                ? MonitorMetric.TemperatureRate : MonitorMetric.LocalContrastRate, phase: Phase).Select(s => s.Index));
        var hidden = (doc.Hidden ?? []).ToHashSet();
        int analyzed = result?.Outcomes.Count(o => o.ValidFrames > 0) ?? 0;
        int failed = result is null ? 0 : result.Outcomes.Length - analyzed;
        counts.Text = $"Defined: {doc.Rois.Regions.Length}    Analyzed: {analyzed}    Failed/excluded: {failed}    " +
            $"Displayed: {visible.Count}    Exported with data: {exported}";
        int selected = roiList.SelectedIndex;
        roiList.ItemsSource = doc.Rois.Regions.Select((r, i) =>
        {
            var outcome = result?.Outcomes[i];
            string state = hidden.Contains(r.Id) ? "Hidden" :
                result is null ? "Pending" : visible.Contains(i) ? "Displayed" :
                outcome?.ValidFrames > 0 ? "Metric unavailable or inapplicable" : "Excluded";
            string completeness = outcome is null || result is null ? "" :
                $"{outcome.ValidFrames}/{result.Frames.Length} valid, {result.Frames.Length - outcome.ValidFrames} missing " +
                $"({100d * outcome.ValidFrames / result.Frames.Length:F1}%)";
            string basis = metrics is null ? "" : $"Local reference: {metrics.LocalLabel(i)}";
            return $"#{i + 1} {r} | {r.Role} | {state} | {completeness} | {basis} | {outcome?.Notes}";
        }).ToArray();
        if (selected >= 0 && selected < roiList.Items.Count) roiList.SelectedIndex = selected;
        plot.Source = result is null ? null : MonitorPlot.Draw(doc, result, Metric, phase: Phase);
        if (result is not null) plot.MaxHeight = plot.Source!.Height;
    }

    private void RefreshNotes(MonitorMetrics metrics)
    {
        if (result is null) return;
        notes.Items.Clear();
        foreach (var warning in metrics.Warnings().Concat(result.Warnings)) notes.Items.Add(warning);
        foreach (var f in result.Frames)
            notes.Items.Add($"{f.Source.CameraClock?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "Unknown clock"}   " +
                $"{Path.GetFileName(f.Source.Path)}   Edge similarity {f.Similarity?.ToString("F3") ?? "—"}   {f.Notes}");
    }

    private void ResetResults() { result = null; exported = 0; plot.Source = null; notes.Items.Clear(); RefreshChart(); }
    private async void Run()
    {
        if (busy || doc is not MonitorDocument input) return;
        busy = true; IsEnabled = false; ResetResults(); status.Text = "Reading native frames and calculating ROI statistics…";
        try
        {
            ShowAnalysis(await Task.Run(() => Monitoring.Run(input, reader)), Metric);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { IsEnabled = true; busy = false; }
    }

    internal void ShowAnalysis(MonitorResult analyzed, MonitorMetric selectedMetric)
    {
        result = analyzed;
        metric.SelectedItem = MetricChoices.First(c => c.Metric == selectedMetric);
        RefreshChart();
        status.Text = "Analysis finished. Review ROI visibility and status and Frame checks for warnings.";
    }
    private void SaveResults(bool complete)
    {
        if (busy || doc is null || result is null) { if (!busy) status.Text = "Run the analysis first."; return; }
        var dialog = new OpenFolderDialog { Title = "Choose a folder for monitoring results" };
        if (dialog.ShowDialog(this) != true) return;
        Safe(() =>
        {
            string folder = Path.Combine(dialog.FolderName, $"Thermal-monitoring_{DateTime.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}");
            exported = MonitorExport.Write(folder, doc, result, complete, Phase);
            RefreshChart();
            status.Text = $"Exported CSV, PNG and session to {folder}";
        });
    }
    private void SaveSession()
    {
        if (busy || doc is null) return;
        var d = new SaveFileDialog { Filter = "Thermal session (*.json)|*.json", FileName = "thermal-session.json" };
        if (d.ShowDialog(this) == true) Safe(() => File.WriteAllText(d.FileName, JsonSerializer.Serialize(doc, Export.Json)));
    }
    private async void LoadSession()
    {
        if (busy) return;
        var d = new OpenFileDialog { Filter = "Thermal session (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        busy = true;
        try
        {
            var loaded = JsonSerializer.Deserialize<MonitorDocument>(File.ReadAllText(d.FileName), Export.Json)
                ?? throw new InvalidDataException("Empty session.");
            if (loaded.Version != 1 || loaded.Frames is null || loaded.Rois is null) throw new InvalidDataException("Invalid session.");
            var order = Monitoring.Order(loaded.Frames);
            var f = await Task.Run(() => Monitoring.ReadChecked(order.Frames[0], reader));
            Regions.VerifyGrid(loaded.Rois, f);
            loaded = loaded with { Rois = loaded.Rois with { Regions = loaded.Rois.Regions
                .Select(r => r ?? new Region(Guid.Empty, "Missing ROI definition", RegionKind.Point, [])).ToArray() } };
            Install(loaded with { Frames = order.Frames }, f);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; }
    }
    private void Safe(Action action) { try { action(); } catch (Exception e) { status.Text = e.Message; } }
}
