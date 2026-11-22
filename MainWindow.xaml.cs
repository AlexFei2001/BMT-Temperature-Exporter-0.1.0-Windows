using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BmtExporter;

public partial class MainWindow : Window
{
    private record Item(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }
    private readonly IBmtReader reader = new Testo890Reader();
    private readonly Dictionary<string, Region[]> regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Reference> references = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private FolderSync? sync;
    private bool scanning;
    private ThermalFrame? current;
    private int selectionVersion;
    private string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMT Exports");

    public MainWindow()
    {
        InitializeComponent();
        OutputText.Text = output;
        timer.Tick += SyncTick;
        Closed += (_, _) => timer.Stop();
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var known = Files.Items.Cast<Item>().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Item? first = null;
        foreach (string path in paths.Where(FolderSync.IsBmt).Select(Path.GetFullPath))
        {
            if (!known.Add(path)) continue;
            var item = new Item(path);
            Files.Items.Add(item);
            first ??= item;
        }
        if (Files.SelectedItem is null && first is not null) Files.SelectedItem = first;
        else if (first is not null) Status.Text = $"{Files.Items.Count} files available. Use Ctrl or Shift to select multiple files.";
    }

    private void OpenFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Testo thermal images (*.bmt)|*.bmt", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Import a folder of BMT files") is not string folder) return;
        try
        {
            var paths = Directory.GetFiles(folder).Where(FolderSync.IsBmt).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) Status.Text = "No BMT files in this folder.";
            else AddFiles(paths);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (!paths.Any(FolderSync.IsBmt)) Status.Text = "Drop one or more .bmt files.";
        else AddFiles(paths);
    }

    private async void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        int version = ++selectionVersion;
        current = null;
        Thermal.Source = Visible.Source = null;
        NoVisible.Visibility = Visibility.Visible;
        ValidationText.Text = "";
        Pixel.Text = "Move over the thermal image to read a pixel.";
        if (Files.SelectedItem is not Item item) { Details.Text = "No image selected"; return; }
        Details.Text = $"Opening {item.Name}…";
        try
        {
            var frame = await Task.Run(() => reader.Read(item.Path));
            if (version != selectionVersion) return;
            current = frame;
            Thermal.Source = frame.Preview;
            Visible.Source = frame.Visible;
            NoVisible.Visibility = frame.Visible is null ? Visibility.Visible : Visibility.Collapsed;
            Details.Text = $"{item.Name}   |   {frame.Camera}   |   {frame.Width} × {frame.Height}\n" +
                $"Min {frame.Values.Min():F2} °C   ·   Max {frame.Values.Max():F2} °C   ·   Mean {frame.Values.Average(v => (double)v):F2} °C";
            ShowValidation();
            Status.Text = "Extraction successful. Select the files you want to export.";
        }
        catch (Exception ex)
        {
            if (version != selectionVersion) return;
            Details.Text = item.Name;
            ValidationText.Text = ex is UnsupportedBmtException ? "Unsupported file" : "Could not open file";
            Status.Text = ex.Message;
        }
    }

    private void ShowValidation()
    {
        if (current is null) return;
        references.TryGetValue(current.Sha256, out var reference);
        var v = Export.Validate(current, reference);
        ValidationText.Text = v.Points == 0 ? v.Status :
            $"{v.Status} · {v.Points} IRSoft points · MAE {v.Mae:F3} °C · max error {v.Maximum:F3} °C";
        var warnings = current.Warnings.Where(w => w != "Independent IRSoft validation pending.").ToArray();
        if (warnings.Length > 0) ValidationText.Text += "\n" + string.Join(" ", warnings);
    }

    private void EditRegions(object sender, RoutedEventArgs e)
    {
        if (current is not ThermalFrame frame) { Status.Text = "Open a BMT first."; return; }
        regions.TryGetValue(frame.Sha256, out var saved);
        var editor = new RegionEditor(frame, saved ?? []) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            regions[frame.Sha256] = editor.Definitions;
            Status.Text = $"{editor.Definitions.Length} ROI(s) defined for {Path.GetFileName(frame.Path)}.";
        }
    }

    private void RegisterImage(object sender, RoutedEventArgs e)
    {
        if (current is not ThermalFrame frame) { Status.Text = "Open a BMT first."; return; }
        new RegistrationWindow(frame) { Owner = this }.ShowDialog();
    }

    private void MonitorSequence(object sender, RoutedEventArgs e) =>
        new MonitorWindow(reader, Files.SelectedItems.Cast<Item>().Select(x => x.Path).ToArray()) { Owner = this }.ShowDialog();

    private void OnPixel(object sender, MouseEventArgs e)
    {
        if (current is null) return;
        var p = e.GetPosition(Thermal);
        double scale = Math.Min(Thermal.ActualWidth / current.Width, Thermal.ActualHeight / current.Height);
        if (scale <= 0) return;
        int x = (int)Math.Floor((p.X - (Thermal.ActualWidth - current.Width * scale) / 2) / scale);
        int y = (int)Math.Floor((p.Y - (Thermal.ActualHeight - current.Height * scale) / 2) / scale);
        if (x >= 0 && x < current.Width && y >= 0 && y < current.Height)
            Pixel.Text = $"X {x}     Y {y}     {current.At(x, y):F2} °C";
        else LeavePixel(sender, e);
    }

    private void LeavePixel(object sender, MouseEventArgs e) => Pixel.Text = "Move over the thermal image to read a pixel.";

    private void LoadReference(object sender, RoutedEventArgs e)
    {
        if (current is null) { Status.Text = "Open the matching BMT first."; return; }
        var dialog = new OpenFileDialog { Filter = "IRSoft reference JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var reference = JsonSerializer.Deserialize<Reference>(File.ReadAllText(dialog.FileName), Export.Json)
                ?? throw new InvalidDataException("Missing reference data.");
            Export.Validate(current, reference);
            references[current.Sha256] = reference;
            ShowValidation();
            Status.Text = "IRSoft reference loaded for this file.";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void ChooseOutput(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose export destination") is string folder)
        {
            output = folder;
            OutputText.Text = folder;
        }
    }

    private void SelectAll(object sender, RoutedEventArgs e) => Files.SelectAll();

    private async void ExportSelected(object sender, RoutedEventArgs e)
    {
        string[] selected = Files.SelectedItems.Cast<Item>().Select(i => i.Path).ToArray();
        if (selected.Length == 0) { Status.Text = "Select at least one BMT file."; return; }
        ExportButton.IsEnabled = false;
        Progress.Value = 0;
        Progress.Maximum = selected.Length;
        Status.Text = $"Exporting {selected.Length} files…";
        string destination = output;
        var snapshot = new Dictionary<string, Reference>(references, StringComparer.OrdinalIgnoreCase);
        var progress = new Progress<int>(n => { Progress.Value = n; Status.Text = $"Processed {n} of {selected.Length}."; });
        try
        {
            string folder = await Task.Run(() => Export.Batch(selected, destination, reader, snapshot,
                (n, _) => ((IProgress<int>)progress).Report(n)));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "summary.json")));
            int ok = report.RootElement.EnumerateArray().Count(r => r.GetProperty("status").GetString() == "Extraction successful");
            Status.Text = $"Exported {ok} of {selected.Length}. Details in summary.json. {folder}";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { ExportButton.IsEnabled = true; }
    }

    private void ChooseSync(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Choose a folder for Folder Sync") is not string folder) return;
        sync = new FolderSync(folder, reader);
        SyncStatus.Text = $"Folder Sync on · {folder}";
        StopButton.IsEnabled = true;
        timer.Start();
    }

    private void StopSync(object sender, RoutedEventArgs e)
    {
        timer.Stop();
        sync = null;
        StopButton.IsEnabled = false;
        SyncStatus.Text = "Folder Sync off";
    }

    private async void SyncTick(object? sender, EventArgs e)
    {
        if (scanning || sync is not FolderSync active) return;
        scanning = true;
        try
        {
            var result = await Task.Run(active.Scan);
            if (sync != active) return;
            AddFiles(result.Frames.Select(f => f.Path));
            if (current is not null && result.Frames.Any(f => f.Path == current.Path && f.Sha256 != current.Sha256))
                OnSelection(this, new SelectionChangedEventArgs(ListBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
            SyncStatus.Text = $"Folder Sync on · {result.Pending} pending · {result.Unsupported} unreadable · {active.Folder}";
        }
        catch (Exception ex) { if (sync == active) SyncStatus.Text = $"Folder Sync waiting · {ex.Message}"; }
        finally { scanning = false; }
    }
}
