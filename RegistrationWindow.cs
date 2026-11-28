using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

sealed class RegistrationEdits
{
    public ControlPair[] Pairs { get; private set; } = [];
    private readonly Stack<ControlPair[]> undo = new(), redo = new();

    public void Apply(ControlPair[] pairs) { undo.Push(Pairs); Pairs = pairs; redo.Clear(); }
    public bool Undo() => Restore(undo, redo);
    public bool Redo() => Restore(redo, undo);
    private bool Restore(Stack<ControlPair[]> from, Stack<ControlPair[]> to)
    {
        if (!from.TryPop(out var previous)) return false;
        to.Push(Pairs);
        Pairs = previous;
        return true;
    }
}

sealed class RegistrationWindow : Window
{
    private readonly ThermalFrame frame;
    private readonly PixelView thermal = new(), rgb = new(), overlay = new();
    private RegistrationEdits edits = new();
    private readonly ListBox errors = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6) };
    private readonly Slider opacity = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 100 };
    private readonly ComboBox side = new() { ItemsSource = new[] { "Thermal", "RGB" }, SelectedIndex = 0 };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "fusion", "checkerboard", "thermal" }, SelectedIndex = 0 };
    private readonly TabControl tabs = new();
    private readonly ComboBox palette = new() { ItemsSource = Enum.GetValues<ThermalPalette>(), SelectedIndex = 0 };
    private readonly TextBox x = new() { Width = 54 }, y = new() { Width = 54 };
    private readonly TextBox low = new() { Width = 65 }, high = new() { Width = 65 }, cell = new() { Width = 58, Text = "48" };
    private readonly CheckBox colorBar = new() { Content = "Colour bar" }, points = new() { Content = "Points" },
        labels = new() { Content = "Labels" }, tiff = new() { Content = "RGB and IR as TIFF" };
    private RegistrationProducts? products;
    private RegistrationFit? fit;
    private ControlPair[] fitted = [];
    private bool working, binding;
    private Guid? selected;
    private string? rgbSource;
    private Pixel? dragPoint;
    private RegistrationDisplay display;
    internal IReadOnlyList<ControlPair> ControlPoints => edits.Pairs;
    internal RegistrationFit? CurrentFit => fit;
    internal Task PendingFit { get; private set; } = Task.CompletedTask;

    public RegistrationWindow(ThermalFrame frame)
    {
        this.frame = frame;
        var temperature = frame.Statistics();
        display = new(ThermalPalette.Inferno, temperature.Min, Math.Max(temperature.Min + .001, temperature.Max), .5, 48,
            false, false, false);
        low.Text = display.Low.ToString("R", CultureInfo.InvariantCulture);
        high.Text = display.High.ToString("R", CultureInfo.InvariantCulture);
        Title = "RGB–thermal registration"; Width = 1260; Height = 760; MinWidth = 780; MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel();
        var bar = new WrapPanel();
        RegionEditor.Add(bar, "Open external RGB…", OpenRgb);
        RegionEditor.Add(bar, "Use embedded RGB", () => { if (!working) SetRgb(frame.Visible, "embedded"); });
        RegionEditor.Add(bar, "Undo", () => Restore(false));
        RegionEditor.Add(bar, "Redo", () => Restore(true));
        RegionEditor.Add(bar, "Delete point", () => Delete(false));
        RegionEditor.Add(bar, "Delete pair", () => Delete(true));
        RegionEditor.Add(bar, "Fit views", () => { thermal.FitView(); rgb.FitView(); overlay.FitView(); });
        RegionEditor.Add(bar, "Compute homography", Compute);
        RegionEditor.Add(bar, "Export registration…", Save);
        top.Children.Add(bar);
        var coordinates = new WrapPanel();
        coordinates.Children.Add(side);
        AddInput(coordinates, "X", x); AddInput(coordinates, "Y", y);
        RegionEditor.Add(coordinates, "Apply pixel", ApplyCoordinates);
        RegionEditor.Add(coordinates, "Focus pair", FocusPair);
        coordinates.Children.Add(new TextBlock { Text = "Click empty space to add/replace · drag a point to edit · arrow keys nudge",
            Margin = new Thickness(6) });
        top.Children.Add(coordinates);
        var settings = new WrapPanel();
        AddInput(settings, "Preview", mode); AddInput(settings, "Palette", palette);
        AddInput(settings, "Min °C", low); AddInput(settings, "Max °C", high);
        AddInput(settings, "Opacity", opacity); AddInput(settings, "Cell px", cell);
        foreach (var option in new[] { colorBar, points, labels, tiff }) { option.Margin = new Thickness(5); settings.Children.Add(option); }
        RegionEditor.Add(settings, "Apply display", ChangeDisplay);
        top.Children.Add(settings);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var results = new DockPanel { Width = 255, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(summary, Dock.Top); results.Children.Add(summary); results.Children.Add(errors);
        DockPanel.SetDock(results, Dock.Right); root.Children.Add(results);
        var views = new Grid();
        views.ColumnDefinitions.Add(new ColumnDefinition()); views.ColumnDefinitions.Add(new ColumnDefinition());
        var right = Panel("RGB", rgb);
        Grid.SetColumn(right, 1);
        views.Children.Add(Panel("Thermal", thermal)); views.Children.Add(right);
        tabs.Items.Add(new TabItem { Header = "Correspondences", Content = views });
        tabs.Items.Add(new TabItem { Header = "Registered preview", Content = overlay });
        root.Children.Add(tabs); Content = root;
        Configure(thermal, false); Configure(rgb, true); overlay.EnableNavigation();
        thermal.Image = frame.Preview;
        side.SelectionChanged += (_, _) => ShowCoordinates();
        errors.SelectionChanged += (_, _) =>
        {
            if (binding || errors.SelectedIndex < 0) return;
            Select(edits.Pairs[errors.SelectedIndex].Id, side.SelectedIndex == 1);
        };
        mode.SelectionChanged += (_, _) => RefreshPreview();
        opacity.ValueChanged += (_, _) => { display = display with { Opacity = opacity.Value }; RefreshPreview(); };
        SetRgb(frame.Visible, "embedded");
    }

    private static void AddInput(Panel panel, string title, Control input)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(5) });
        panel.Children.Add(input);
    }

    private static DockPanel Panel(string text, PixelView view)
    {
        var panel = new DockPanel { Margin = new Thickness(5) };
        var label = new TextBlock { Text = text, Margin = new Thickness(4) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(view); return panel;
    }

    private void Configure(PixelView view, bool isRgb)
    {
        view.Focusable = true;
        view.EnableNavigation();
        view.PixelClicked += p => Click(p, isRgb);
        view.Overlay = dc => Mark(dc, view, isRgb);
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (working || view.Image is null || view.IsMouseCaptured) return;
            view.Focus();
            var mouse = e.GetPosition(view);
            var closest = edits.Pairs.Select(p => new { Pair = p, Point = PointOf(p, isRgb) })
                .Where(p => p.Point is not null)
                .OrderBy(p => (view.Center(p.Point!) - mouse).LengthSquared).FirstOrDefault();
            if (closest is null || (view.Center(closest.Point!) - mouse).Length > 10 || !view.CaptureMouse()) return;
            Select(closest.Pair.Id, isRgb);
            dragPoint = closest.Point;
            e.Handled = true;
        };
        view.MouseMove += (_, e) =>
        {
            if (dragPoint is null || !view.IsMouseCaptured || view.Hit(e) is not Pixel p) return;
            dragPoint = p;
            view.InvalidateVisual();
        };
        view.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (dragPoint is not Pixel p || !view.IsMouseCaptured) return;
            dragPoint = null; view.ReleaseMouseCapture();
            if (SelectedPair is ControlPair pair) Move(pair, isRgb, p);
            e.Handled = true;
        };
        view.LostMouseCapture += (_, _) => { dragPoint = null; view.InvalidateVisual(); };
        view.KeyDown += (_, e) =>
        {
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => (0, 0)
            };
            if (working || (dx == 0 && dy == 0) || SelectedPair is not ControlPair pair || PointOf(pair, isRgb) is not Pixel p)
                return;
            Move(pair, isRgb, new(p.X + dx, p.Y + dy));
            e.Handled = true;
        };
    }

    private ControlPair? SelectedPair => edits.Pairs.FirstOrDefault(p => p.Id == selected);
    private static Pixel? PointOf(ControlPair pair, bool isRgb) => isRgb ? pair.Rgb : pair.Thermal;
    private static ControlPair WithPoint(ControlPair pair, bool isRgb, Pixel? point) =>
        isRgb ? pair with { Rgb = point } : pair with { Thermal = point };

    internal void Click(Pixel p, bool isRgb)
    {
        if (working || rgb.Image is null) return;
        var pair = SelectedPair;
        if (pair is not null && PointOf(pair, isRgb) is null) { Move(pair, isRgb, p); return; }
        var created = WithPoint(new(Guid.NewGuid(), null, null), isRgb, p);
        selected = created.Id;
        side.SelectedIndex = isRgb ? 1 : 0;
        edits.Apply([.. edits.Pairs, created]);
        Changed();
    }

    private void ApplyCoordinates()
    {
        ApplyCoordinates(x.Text, y.Text);
    }

    internal void ApplyCoordinates(string xText, string yText)
    {
        if (working || SelectedPair is not ControlPair pair) return;
        if (!int.TryParse(xText, out int px) || !int.TryParse(yText, out int py))
        { status.Text = "Enter integer pixel X and Y."; return; }
        Move(pair, side.SelectedIndex == 1, new(px, py));
    }

    internal void Move(ControlPair pair, bool isRgb, Pixel p)
    {
        var image = (isRgb ? rgb : thermal).Image;
        if (image is null || p.X < 0 || p.Y < 0 || p.X >= image.PixelWidth || p.Y >= image.PixelHeight)
        { status.Text = "Point must be inside its image."; return; }
        if (PointOf(pair, isRgb) == p) return;
        var next = WithPoint(pair, isRgb, p);
        edits.Apply(edits.Pairs.Select(v => v.Id == pair.Id ? next : v).ToArray());
        Select(pair.Id, isRgb);
        Changed();
    }

    internal void Delete(bool whole)
    {
        if (working || SelectedPair is not ControlPair pair) return;
        bool isRgb = side.SelectedIndex == 1;
        if (!whole && PointOf(pair, isRgb) is null) return;
        var next = WithPoint(pair, isRgb, null);
        edits.Apply(whole || (next.Rgb is null && next.Thermal is null)
            ? edits.Pairs.Where(p => p.Id != pair.Id).ToArray()
            : edits.Pairs.Select(p => p.Id == pair.Id ? next : p).ToArray());
        Changed();
    }

    internal void Restore(bool forward)
    {
        if (!working && (forward ? edits.Redo() : edits.Undo())) Changed();
    }

    private void Select(Guid id, bool isRgb)
    {
        selected = id;
        side.SelectedIndex = isRgb ? 1 : 0;
        binding = true;
        errors.SelectedIndex = Array.FindIndex(edits.Pairs, p => p.Id == id);
        binding = false;
        ShowCoordinates();
        thermal.InvalidateVisual(); rgb.InvalidateVisual();
    }

    private void ShowCoordinates()
    {
        var p = SelectedPair is ControlPair pair ? PointOf(pair, side.SelectedIndex == 1) : null;
        x.Text = p?.X.ToString(CultureInfo.InvariantCulture) ?? "";
        y.Text = p?.Y.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void FocusPair()
    {
        if (SelectedPair is not ControlPair pair) return;
        if (pair.Thermal is Pixel t) thermal.FocusPixel(t);
        if (pair.Rgb is Pixel r) { rgb.FocusPixel(r); overlay.FocusPixel(r); }
    }

    private void Mark(DrawingContext dc, PixelView view, bool isRgb)
    {
        for (int i = 0; i < edits.Pairs.Length; i++)
        {
            var pair = edits.Pairs[i];
            if (PointOf(pair, isRgb) is not Pixel point) continue;
            bool active = pair.Id == selected;
            var p = view.Center(active && view.IsMouseCaptured && dragPoint is Pixel moving ? moving : point);
            int j = Array.FindIndex(fitted, f => f.Id == pair.Id);
            Brush brush = fit is null || j < 0 ? Brushes.Gold : fit.Inliers[j] ? Brushes.Lime : Brushes.OrangeRed;
            if (active) dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 8, 8);
            dc.DrawEllipse(brush, new Pen(Brushes.Black, 1), p, 4, 4);
            view.Label(dc, (i + 1).ToString(), p + new Vector(6, 6), brush);
            if (!isRgb || fit is null || j < 0 || Project(fit.Homography, pair.Thermal!) is not Point projected) continue;
            var q = view.Center(projected.X, projected.Y);
            dc.DrawLine(new Pen(brush, 1.5), p, q);
            dc.DrawLine(new Pen(brush, 1.5), q + new Vector(-4, -4), q + new Vector(4, 4));
            dc.DrawLine(new Pen(brush, 1.5), q + new Vector(4, -4), q + new Vector(-4, 4));
        }
    }

    internal static Point? Project(double[][] h, Pixel p)
    {
        double w = h[2][0] * p.X + h[2][1] * p.Y + h[2][2];
        if (Math.Abs(w) < 1e-12) return null;
        double px = (h[0][0] * p.X + h[0][1] * p.Y + h[0][2]) / w;
        double py = (h[1][0] * p.X + h[1][1] * p.Y + h[1][2]) / w;
        return double.IsFinite(px) && double.IsFinite(py) ? new Point(px, py) : null;
    }

    private void RefreshPairs()
    {
        binding = true;
        errors.Items.Clear();
        for (int i = 0; i < edits.Pairs.Length; i++)
        {
            var pair = edits.Pairs[i];
            int j = Array.FindIndex(fitted, f => f.Id == pair.Id);
            string detail = pair.Thermal is null ? "needs thermal" : pair.Rgb is null ? "needs RGB" :
                fit is null || j < 0 ? "paired" : $"{fit.Residuals[j]:F2} px · {(fit.Inliers[j] ? "inlier" : "outlier")}";
            errors.Items.Add($"#{i + 1} [{pair.Id.ToString("N")[..8]}] {detail}");
        }
        errors.SelectedIndex = Array.FindIndex(edits.Pairs, p => p.Id == selected);
        binding = false;
        ShowCoordinates();
        thermal.InvalidateVisual(); rgb.InvalidateVisual();
    }

    private void Changed()
    {
        fit = null; products = null; fitted = [];
        summary.Text = "Control points";
        overlay.Image = rgb.Image;
        RefreshPairs();
        int count = edits.Pairs.Count(p => p.Thermal is not null && p.Rgb is not null);
        status.Text = $"{count} complete pairs. Select a pair to replace a missing point.";
        if (count >= 4) Compute();
    }

    private void Compute()
    {
        if (!working) PendingFit = ComputeFit();
    }

    private async Task ComputeFit()
    {
        if (working || rgb.Image is not BitmapSource target) return;
        var pairs = edits.Pairs.Where(p => p.Thermal is not null && p.Rgb is not null).ToArray();
        var src = pairs.Select(p => p.Thermal!).ToArray();
        var dst = pairs.Select(p => p.Rgb!).ToArray();
        try
        {
            RegistrationProducts.ValidatePoints(src); RegistrationProducts.ValidatePoints(dst);
            working = true;
            status.Text = "Refitting thermal-to-RGB homography…";
            var built = await Task.Run(async () =>
            {
                var result = await RegistrationWorker.Fit(frame.Preview, target, src, dst);
                return (result, products: new RegistrationProducts(frame, target, result.Homography));
            });
            ShowFit(built.result, built.products, pairs);
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { working = false; }
    }

    private void ShowFit(RegistrationFit result, RegistrationProducts built, ControlPair[] pairs)
    {
        fit = result; fitted = pairs; products = built;
        var residuals = fit.Residuals.Where((_, i) => fit.Inliers[i]).ToArray();
        summary.Text = $"Fitting residuals (RGB pixels)\nComplete pairs: {pairs.Length}\nInliers: {residuals.Length}\n" +
            $"Inlier mean {residuals.Average():F2} · RMSE {fit.Rmse:F2} · max {residuals.Max():F2}\n" +
            $"All-pair max {fit.Residuals.Max():F2}\nIndependent accuracy: unmeasured";
        RefreshPairs(); RefreshPreview();
        status.Text = "Fit updated. Open Registered preview to inspect the current transform.";
    }

    internal void SelectPreview() => tabs.SelectedIndex = 1;

    private void SetRgb(BitmapSource? image, string? source)
    {
        edits = new(); selected = null; rgbSource = source;
        rgb.Image = image; Changed();
        status.Text = image is null ? "No embedded RGB available. Open an external RGB image." :
            $"RGB {image.PixelWidth} × {image.PixelHeight}. Click each image to create a pair.";
    }

    private void OpenRgb()
    {
        if (working) return;
        var d = new OpenFileDialog { Filter = "RGB images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" };
        if (d.ShowDialog(this) != true) return;
        try { using var stream = File.OpenRead(d.FileName); SetRgb(VisibleImage.Decode(stream), d.FileName); }
        catch (Exception e) { status.Text = e.Message; }
    }

    private bool TryDisplay()
    {
        if (!Parse(low.Text, out double min) || !Parse(high.Text, out double max) || min >= max || !double.IsFinite(max - min) ||
            !int.TryParse(cell.Text, out int size) || size < 1)
        { status.Text = "Enter an increasing finite temperature range and a positive checkerboard cell size."; return false; }
        display = new((ThermalPalette)palette.SelectedItem, min, max, opacity.Value, size,
            colorBar.IsChecked == true, points.IsChecked == true, labels.IsChecked == true);
        return true;
    }

    private static bool Parse(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) && double.IsFinite(value);

    private void ChangeDisplay() { if (TryDisplay()) RefreshPreview(); }
    private void RefreshPreview()
    {
        if (products is not null) overlay.ReplacePreview(products.Render((string)mode.SelectedItem, display, fitted));
    }

    private void Save()
    {
        if (working || fit is null || products is null || rgb.Image is not BitmapSource target || !TryDisplay()) return;
        RefreshPreview();
        var d = new OpenFolderDialog { Title = "Choose a destination for registration products" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(d.FolderName, $"Registration_{Guid.NewGuid():N}")).FullName;
            products.Save(folder, frame, target, fit, fitted, display, tiff.IsChecked == true, rgbSource);
            status.Text = $"Exported registration and image products to {folder}";
        }
        catch (Exception e) { status.Text = e.Message; }
    }
}
