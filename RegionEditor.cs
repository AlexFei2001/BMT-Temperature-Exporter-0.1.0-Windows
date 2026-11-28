using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BmtExporter;

sealed class RegionEditor : Window
{
    private readonly ThermalFrame frame;
    private readonly PixelView view = new();
    private readonly ListBox list = new();
    private readonly TextBox name = new() { MinWidth = 150 };
    private readonly ComboBox kind = new() { ItemsSource = Enum.GetValues<RegionKind>(), SelectedItem = RegionKind.Polygon };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Pixel> draft = [];
    private bool drawing;
    private int dragged = -1;
    private Pixel[]? editing;
    private Pixel? moveOrigin;
    public Region[] Definitions => list.Items.Cast<Region>().ToArray();

    public RegionEditor(ThermalFrame frame, IEnumerable<Region> initial)
    {
        this.frame = frame;
        Title = $"Thermal ROIs — {Path.GetFileName(frame.Path)}";
        Width = 1040; Height = 660; MinWidth = 680; MinHeight = 480;
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel();
        toolbar.Children.Add(kind); toolbar.Children.Add(name);
        Add(toolbar, "New ROI", () => { drawing = true; draft.Clear(); status.Text = "Click vertices. Use Finish for polygons."; Refresh(); });
        Add(toolbar, "Finish", Finish);
        Add(toolbar, "Cancel drawing", () => { drawing = false; draft.Clear(); Refresh(); });
        Add(toolbar, "Rename", () => { if (list.SelectedItem is Region r) Replace(r with { Name = ReadName(r.Name) }); });
        Add(toolbar, "Delete", () => { if (list.SelectedItem is Region r) list.Items.Remove(r); Refresh(); });
        Add(toolbar, "Load ROIs", Load);
        Add(toolbar, "Save ROIs", Save);
        Add(toolbar, "Use ROIs", () => { DialogResult = true; });
        var navigation = new WrapPanel();
        var scale = new TextBlock { Margin = new Thickness(8) };
        Add(navigation, "Zoom +", () => view.ZoomBy(1.25));
        Add(navigation, "Zoom −", () => view.ZoomBy(.8));
        Add(navigation, "Fit to window", view.FitView);
        Add(navigation, "Reset view", view.FitView);
        Add(navigation, "100%", view.NativeScale);
        Add(navigation, "Focus ROI", () => { if (list.SelectedItem is Region r && r.Vertices is not null &&
            r.Vertices.All(p => p is not null)) view.FocusRegion(r.Vertices); });
        navigation.Children.Add(scale);
        navigation.Children.Add(new TextBlock { Text = "Wheel zoom · middle drag pan · Shift+drag moves selected ROI",
            Margin = new Thickness(8) });
        kind.SelectionChanged += (_, _) =>
        {
            if (!drawing) return;
            draft.Clear(); view.InvalidateVisual();
        };
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        DockPanel.SetDock(navigation, Dock.Top); root.Children.Add(navigation);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        list.Width = 170; list.Margin = new Thickness(0, 8, 8, 8);
        DockPanel.SetDock(list, Dock.Left); root.Children.Add(list);
        view.Image = frame.Preview; view.Margin = new Thickness(0, 8, 0, 8);
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.NearestNeighbor);
        view.Overlay = Draw;
        view.ViewChanged += () => scale.Text = $"{view.PixelScale:P0}";
        view.EnableNavigation(leftDrag: false);
        root.Children.Add(view); Content = root;
        foreach (var r in initial) list.Items.Add(r);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is Region r) name.Text = r.Name; Refresh(); };
        view.MouseLeftButtonDown += Start;
        view.MouseMove += (_, e) =>
        {
            if (dragged >= 0 && editing is not null && view.Hit(e) is Pixel p)
            { editing[dragged] = p; view.InvalidateVisual(); }
            else if (moveOrigin is Pixel origin && list.SelectedItem is Region r && view.Hit(e) is Pixel target)
            {
                editing = Translate(r.Vertices, target.X - origin.X, target.Y - origin.Y, frame.Width, frame.Height);
                view.InvalidateVisual();
            }
        };
        view.MouseLeftButtonUp += (_, _) =>
        {
            if (dragged < 0 && moveOrigin is null) return;
            var vertices = editing;
            dragged = -1; moveOrigin = null; editing = null; view.ReleaseMouseCapture();
            if (vertices is not null && list.SelectedItem is Region r) Safe(() => Replace(r with { Vertices = vertices }));
            view.InvalidateVisual();
        };
        view.LostMouseCapture += (_, _) => { dragged = -1; moveOrigin = null; editing = null; view.InvalidateVisual(); };
        status.Text = "Select an ROI to see its statistics. Drag its visible handles to edit.";
    }

    internal static void Add(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 5, 8, 5) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }
    private void Add(WrapPanel panel, string text, Action action) => Add((Panel)panel, text, () => Safe(action));
    private void Safe(Action action) { try { action(); } catch (Exception e) { status.Text = e.Message; } }
    private string ReadName(string fallback) => string.IsNullOrWhiteSpace(name.Text) ? fallback : name.Text.Trim();
    private void Finish()
    {
        if (!drawing || kind.SelectedItem is not RegionKind type) return;
        var roi = new Region(Guid.NewGuid(), ReadName($"ROI {list.Items.Count + 1}"), type, draft.ToArray());
        Regions.Mask(roi, frame.Width, frame.Height);
        drawing = false; draft.Clear(); list.Items.Add(roi); list.SelectedItem = roi; Refresh();
    }
    private void Start(object sender, MouseButtonEventArgs e)
    {
        if (view.IsMouseCaptured || view.Hit(e) is not Pixel p) return;
        if (drawing)
        {
            if (draft.Count >= 256 || draft.Contains(p)) return;
            draft.Add(p);
            if (kind.SelectedItem is RegionKind.Point || (kind.SelectedItem is RegionKind.Rectangle && draft.Count == 2))
            {
                try { Finish(); } catch (Exception ex) { draft.RemoveAt(draft.Count - 1); status.Text = ex.Message; }
            }
            view.InvalidateVisual();
            return;
        }
        if (list.SelectedItem is not Region r || r.Vertices is not { Length: > 0 } ||
            r.Vertices.Any(p => p is null)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (view.CaptureMouse()) moveOrigin = p;
            return;
        }
        int nearest = Enumerable.Range(0, r.Vertices.Length)
            .MinBy(i => (view.Center(r.Vertices[i]) - e.GetPosition(view)).LengthSquared);
        if ((view.Center(r.Vertices[nearest]) - e.GetPosition(view)).Length > 14) return;
        editing = (Pixel[])r.Vertices.Clone(); dragged = nearest; view.CaptureMouse();
    }

    internal static Pixel[] Translate(Pixel[] points, int dx, int dy, int width, int height)
    {
        dx = Math.Clamp(dx, -points.Min(p => p.X), width - 1 - points.Max(p => p.X));
        dy = Math.Clamp(dy, -points.Min(p => p.Y), height - 1 - points.Max(p => p.Y));
        return points.Select(p => new Pixel(p.X + dx, p.Y + dy)).ToArray();
    }
    private void Replace(Region r)
    {
        Regions.Mask(r, frame.Width, frame.Height);
        int i = list.SelectedIndex; list.Items[i] = r; list.SelectedIndex = i; Refresh();
    }
    private void Refresh()
    {
        view.InvalidateVisual();
        if (!drawing && list.SelectedItem is Region r)
        {
            Safe(() =>
            {
                var s = Regions.FiniteStats(frame.AtIndex, Regions.Mask(r, frame.Width, frame.Height), median: true);
                status.Text = s.Count == 0 ? "ROI has no finite temperature samples." :
                    $"{r.Name}   {s.Count} pixels   Mean {s.Mean:F3} °C   Median {s.Median:F3} °C   " +
                    $"Min {s.Min:F3} °C   Max {s.Max:F3} °C   SD {s.StdDev:F3} °C";
            });
        }
    }
    private void Draw(DrawingContext dc)
    {
        foreach (var r in Definitions)
        {
            if (r.Vertices is null || r.Vertices.Any(p => p is null)) continue;
            bool selected = ReferenceEquals(r, list.SelectedItem);
            DrawShape(dc, r.Kind, selected && editing is not null ? editing : r.Vertices,
                selected ? Brushes.Gold : Brushes.Turquoise, selected, r.Name);
        }
        if (drawing && kind.SelectedItem is RegionKind type) DrawShape(dc, type, draft, Brushes.White, true, "");
    }
    private void DrawShape(DrawingContext dc, RegionKind type, IReadOnlyList<Pixel> vertices, Brush color, bool handles, string title)
    {
        if (vertices.Count == 0) return;
        var pts = vertices.Select(view.Center).ToArray();
        var pen = new Pen(color, 1.6);
        if (type == RegionKind.Rectangle && pts.Length == 2) dc.DrawRectangle(null, pen, new Rect(pts[0], pts[1]));
        else if (type == RegionKind.Polygon && pts.Length > 1)
        {
            for (int i = 1; i < pts.Length; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
            if (pts.Length > 2) dc.DrawLine(pen, pts[^1], pts[0]);
        }
        if (handles || pts.Length == 1) foreach (var p in pts) dc.DrawEllipse(color, null, p, 4, 4);
        if (title.Length > 0) view.Label(dc, title, pts[0] + new Vector(6, 6), color);
    }
    private void Save()
    {
        var d = new SaveFileDialog { Filter = "Thermal ROIs (*.json)|*.json", FileName = "thermal-rois.json" };
        if (d.ShowDialog(this) == true)
            File.WriteAllText(d.FileName, JsonSerializer.Serialize(new RegionDocument(1, frame.Sha256, frame.Width, frame.Height, Definitions), Export.Json));
    }
    private void Load()
    {
        var d = new OpenFileDialog { Filter = "Thermal ROIs (*.json)|*.json" };
        if (d.ShowDialog(this) != true) return;
        var doc = JsonSerializer.Deserialize<RegionDocument>(File.ReadAllText(d.FileName), Export.Json)
            ?? throw new InvalidDataException("Empty ROI document.");
        var loaded = Regions.Verify(doc, frame);
        drawing = false; draft.Clear(); list.Items.Clear();
        foreach (var r in loaded) list.Items.Add(r);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        Refresh();
    }
}
