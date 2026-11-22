using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace BmtExporter;

sealed class MonitorWindow : Window
{
    private readonly IBmtReader reader;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox anomaly = new() { Width = 150 }, reference = new() { Width = 150 };
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
        RegionEditor.Add(bar, "Export CSV + PNG…", SaveResults);
        RegionEditor.Add(bar, "Save session…", SaveSession);
        RegionEditor.Add(bar, "Load session…", LoadSession);
        top.Children.Add(bar);
        var pair = new WrapPanel();
        pair.Children.Add(new TextBlock { Text = "Anomaly ROI", Margin = new Thickness(6) });
        pair.Children.Add(anomaly);
        pair.Children.Add(new TextBlock { Text = "Reference ROI", Margin = new Thickness(6) });
        pair.Children.Add(reference); top.Children.Add(pair);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var tabs = new TabControl { Margin = new Thickness(0, 8, 0, 8) };
        tabs.Items.Add(new TabItem { Header = "Temperature and ΔT", Content = plot });
        tabs.Items.Add(new TabItem { Header = "Frame checks", Content = notes });
        root.Children.Add(tabs); Content = root;
        anomaly.SelectionChanged += (_, _) => PairChanged();
        reference.SelectionChanged += (_, _) => PairChanged();
        status.Text = "Open BMT files from one acquisition session.";
        if (paths.Length >= 2) Loaded += async (_, _) => await Import(paths);
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
    private void Install(MonitorDocument newDoc, ThermalFrame f)
    {
        first = f; doc = newDoc; ResetResults();
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
        doc = doc with { Rois = doc.Rois with { Regions = edit.Definitions } };
        ResetResults(); Bind();
        status.Text = $"{edit.Definitions.Length} ROI(s) defined. Click Analyze to apply them to every frame.";
    }
    private void Bind()
    {
        if (doc is null) return;
        binding = true;
        try
        {
            anomaly.ItemsSource = reference.ItemsSource = doc.Rois.Regions;
            anomaly.SelectedItem = doc.Rois.Regions.FirstOrDefault(r => r.Id == doc.Anomaly) ?? doc.Rois.Regions.FirstOrDefault();
            reference.SelectedItem = doc.Rois.Regions.FirstOrDefault(r => r.Id == doc.Reference) ?? doc.Rois.Regions.Skip(1).FirstOrDefault();
        }
        finally { binding = false; }
        PairChanged();
    }
    private void PairChanged()
    {
        if (binding || doc is null) return;
        doc = doc with { Anomaly = (anomaly.SelectedItem as Region)?.Id, Reference = (reference.SelectedItem as Region)?.Id };
        if (result is not null) plot.Source = MonitorPlot.Draw(doc, result);
    }
    private void ResetResults() { result = null; plot.Source = null; notes.Items.Clear(); }
    private async void Run()
    {
        if (busy || doc is not MonitorDocument input) return;
        busy = true; ResetResults(); status.Text = "Reading native frames and calculating ROI statistics…";
        try
        {
            result = await Task.Run(() => Monitoring.Run(input, reader));
            foreach (var f in result.Frames)
                notes.Items.Add($"{f.Source.CameraClock?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "Unknown clock"}   " +
                    $"{Path.GetFileName(f.Source.Path)}   Edge similarity {f.Similarity?.ToString("F3") ?? "—"}   {f.Notes}");
            plot.Source = MonitorPlot.Draw(doc!, result);
            status.Text = string.Join("\n", result.Warnings);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; }
    }
    private void SaveResults()
    {
        if (busy || doc is null || result is null) { if (!busy) status.Text = "Run the analysis first."; return; }
        var dialog = new OpenFolderDialog { Title = "Choose a folder for monitoring results" };
        if (dialog.ShowDialog(this) != true) return;
        Safe(() =>
        {
            string folder = Path.Combine(dialog.FolderName, $"Thermal-monitoring_{DateTime.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            using (var writer = new StreamWriter(Path.Combine(folder, "temperatures.csv"), false, new UTF8Encoding(true)))
                Monitoring.Csv(writer, doc, result);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(MonitorPlot.Draw(doc, result)));
            using (var file = File.Create(Path.Combine(folder, "temperatures.png"))) png.Save(file);
            File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(doc, Export.Json));
            File.WriteAllLines(Path.Combine(folder, "frame-checks.txt"), result.Warnings);
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
            Regions.Verify(loaded.Rois, f);
            Install(loaded with { Frames = order.Frames }, f);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { busy = false; }
    }
    private void Safe(Action action) { try { action(); } catch (Exception e) { status.Text = e.Message; } }
}
