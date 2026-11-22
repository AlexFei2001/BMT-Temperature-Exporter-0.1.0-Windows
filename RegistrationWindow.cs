using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

sealed class RegistrationWindow : Window
{
    private readonly ThermalFrame frame;
    private readonly PixelView thermal = new(), rgb = new(), overlay = new();
    private readonly List<Pixel> src = [], dst = [];
    private readonly ListBox errors = new() { MinWidth = 200 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6) };
    private readonly Slider opacity = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 120 };
    private BitmapSource? warped;
    private RegistrationFit? fit;
    private bool working;
    private int selected = -1;

    public RegistrationWindow(ThermalFrame frame)
    {
        this.frame = frame;
        Title = "RGB–thermal registration"; Width = 1180; Height = 680; MinWidth = 780; MinHeight = 470;
        var root = new DockPanel { Margin = new Thickness(12) };
        var bar = new WrapPanel();
        var tabs = new TabControl();
        var overlayTab = new TabItem { Header = "Overlay", Content = overlay };
        RegionEditor.Add(bar, "Open external RGB…", OpenRgb);
        RegionEditor.Add(bar, "Use embedded RGB", () => { if (!working) SetRgb(frame.Visible); });
        RegionEditor.Add(bar, "Undo point", Undo);
        RegionEditor.Add(bar, "Delete selected pair", DeletePair);
        RegionEditor.Add(bar, "Fit views", () => { thermal.FitView(); rgb.FitView(); overlay.FitView(); });
        RegionEditor.Add(bar, "Compute homography", Compute);
        RegionEditor.Add(bar, "Save fit report…", Save);
        bar.Children.Add(new TextBlock { Text = "Overlay opacity", Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(opacity);
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var results = new DockPanel { Width = 218, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(summary, Dock.Top); results.Children.Add(summary); results.Children.Add(errors);
        DockPanel.SetDock(results, Dock.Right); root.Children.Add(results);
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
        var thermalPane = Panel("1. Thermal", thermal);
        var rgbPane = Panel("2. RGB", rgb);
        Grid.SetColumn(rgbPane, 1); pair.Children.Add(thermalPane); pair.Children.Add(rgbPane);
        tabs.Items.Add(new TabItem { Header = "Correspondences", Content = pair });
        tabs.Items.Add(overlayTab);
        root.Children.Add(tabs); Content = root;
        foreach (var view in new[] { thermal, rgb, overlay }) view.EnableNavigation();
        thermal.Image = frame.Preview;
        SetRgb(frame.Visible);
        thermal.PixelClicked += p =>
        {
            if (working || rgb.Image is null || src.Count != dst.Count || src.Count >= 100) return;
            src.Add(p); ResetFit(); status.Text = $"Pair {src.Count}: click its RGB match.";
        };
        rgb.PixelClicked += p =>
        {
            if (working || src.Count != dst.Count + 1) return;
            dst.Add(p); ResetFit(); status.Text = $"{dst.Count} pairs. Add another thermal point or compute.";
        };
        thermal.Overlay = dc => Mark(dc, thermal, src);
        rgb.Overlay = dc => Mark(dc, rgb, dst);
        overlay.Overlay = dc =>
        {
            if (warped is null) return;
            dc.PushOpacity(opacity.Value);
            dc.DrawImage(warped, overlay.ImageBounds);
            dc.Pop();
            Mark(dc, overlay, dst);
        };
        opacity.ValueChanged += (_, _) => overlay.InvalidateVisual();
        errors.SelectionChanged += (_, _) =>
        {
            selected = errors.SelectedIndex;
            if (selected >= 0 && selected < dst.Count)
            {
                thermal.FocusPixel(src[selected]);
                rgb.FocusPixel(dst[selected]);
                overlay.FocusPixel(dst[selected]);
            }
            thermal.InvalidateVisual(); rgb.InvalidateVisual(); overlay.InvalidateVisual();
        };
        var help = new TextBlock
        {
            Text = "Wheel: zoom at cursor  •  Left drag: pan  •  Click: add thermal point, then RGB match",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 6)
        };
        DockPanel.SetDock(help, Dock.Top); root.Children.Insert(1, help);

        async void Compute()
        {
            if (working || rgb.Image is not BitmapSource target || src.Count < 4 || src.Count != dst.Count)
            { if (!working) status.Text = "Add at least four complete, well-spread pairs."; return; }
            working = true; ResetFit(); status.Text = "Computing thermal-to-RGB homography…";
            try
            {
                var result = await Task.Run(() => RegistrationWorker.Fit(frame.Preview, target, src.ToArray(), dst.ToArray()));
                using var png = new MemoryStream(Convert.FromBase64String(result.Overlay));
                warped = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                fit = result;
                RefreshPairs();
                double[] inlierErrors = result.Residuals.Where((_, i) => result.Inliers[i]).ToArray();
                double[] outlierErrors = result.Residuals.Where((_, i) => !result.Inliers[i]).ToArray();
                summary.Text = $"Reprojection error (RGB px)\nInliers: {inlierErrors.Length}/{src.Count}\n" +
                    $"Inlier mean {inlierErrors.Average():F2} · max {inlierErrors.Max():F2}\nRMSE {result.Rmse:F2}" +
                    (outlierErrors.Length == 0 ? "" : $"\nOutlier mean {outlierErrors.Average():F2}");
                status.Text = "Select a pair to zoom to it. Crosses show projected positions. These are fit residuals.";
                overlay.InvalidateVisual(); tabs.SelectedItem = overlayTab;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { working = false; }
        }
    }

    private static DockPanel Panel(string text, PixelView view)
    {
        var panel = new DockPanel { Margin = new Thickness(5) };
        var label = new TextBlock { Text = text, Margin = new Thickness(4) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(view); return panel;
    }
    private void Mark(DrawingContext dc, PixelView view, List<Pixel> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Brush color = fit is null ? Brushes.Gold : fit.Residuals[i] < 3 ? Brushes.Lime :
                fit.Residuals[i] < 8 ? Brushes.Gold : Brushes.OrangeRed;
            var p = view.Center(points[i]);
            if (i == selected) dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 8, 8);
            dc.DrawEllipse(color, new Pen(Brushes.Black, 1), p, 4, 4);
            view.Label(dc, (i + 1).ToString(), p + new Vector(6, 6), color);
            if (view == thermal || fit is null || Project(fit.Homography, src[i]) is not Point projected ||
                view.Image is null || projected.X < 0 || projected.X >= view.Image.PixelWidth ||
                projected.Y < 0 || projected.Y >= view.Image.PixelHeight) continue;
            var q = view.Center(projected.X, projected.Y);
            var pen = new Pen(color, 1.5);
            dc.DrawLine(pen, p, q);
            dc.DrawLine(pen, q + new Vector(-5, -5), q + new Vector(5, 5));
            dc.DrawLine(pen, q + new Vector(-5, 5), q + new Vector(5, -5));
        }
    }

    internal static Point? Project(double[][] h, Pixel p)
    {
        double w = h[2][0] * p.X + h[2][1] * p.Y + h[2][2];
        if (Math.Abs(w) < 1e-12) return null;
        double x = (h[0][0] * p.X + h[0][1] * p.Y + h[0][2]) / w;
        double y = (h[1][0] * p.X + h[1][1] * p.Y + h[1][2]) / w;
        return double.IsFinite(x) && double.IsFinite(y) ? new Point(x, y) : null;
    }

    private void RefreshPairs()
    {
        errors.Items.Clear();
        for (int i = 0; i < dst.Count; i++)
            errors.Items.Add(fit is null ? $"Pair {i + 1}" :
                $"Pair {i + 1}: {fit.Residuals[i]:F2} px  {(fit.Inliers[i] ? "inlier" : "outlier")}");
    }
    private void ResetFit()
    {
        fit = null; warped = null; summary.Text = "Control points";
        RefreshPairs();
        thermal.InvalidateVisual(); rgb.InvalidateVisual(); overlay.InvalidateVisual();
    }
    private void SetRgb(BitmapSource? image)
    {
        src.Clear(); dst.Clear(); rgb.Image = overlay.Image = image; ResetFit();
        status.Text = image is null ? "No embedded RGB available. Open an external RGB image." :
            $"RGB {image.PixelWidth} × {image.PixelHeight}. Alternate thermal and RGB clicks.";
    }
    private void Undo()
    {
        if (working || src.Count == 0) return;
        if (src.Count == dst.Count) dst.RemoveAt(dst.Count - 1);
        src.RemoveAt(src.Count - 1); ResetFit(); status.Text = $"{dst.Count} complete pairs.";
    }
    private void DeletePair()
    {
        if (working || selected < 0 || selected >= dst.Count) return;
        src.RemoveAt(selected); dst.RemoveAt(selected);
        ResetFit();
        status.Text = $"{dst.Count} complete pairs. Compute again after editing.";
    }
    private void OpenRgb()
    {
        if (working) return;
        var d = new OpenFileDialog { Filter = "RGB images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff" };
        if (d.ShowDialog(this) != true) return;
        try { using var stream = File.OpenRead(d.FileName); SetRgb(VisibleImage.Decode(stream)); }
        catch (Exception e) { status.Text = e.Message; }
    }
    private void Save()
    {
        if (fit is null || rgb.Image is null) return;
        var d = new SaveFileDialog { Filter = "Fit report (*.json)|*.json", FileName = "registration.json" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            var report = new { frame.Sha256, direction = "thermal_to_rgb", coordinateOrigin = "top-left pixel centre (0,0)",
                thermalPoints = src, rgbPoints = dst, rgbWidth = rgb.Image.PixelWidth, rgbHeight = rgb.Image.PixelHeight,
                fit.Homography, fit.Residuals, fit.Inliers, inlierFitRmsePx = fit.Rmse };
            File.WriteAllText(d.FileName, JsonSerializer.Serialize(report, Export.Json));
        }
        catch (Exception e) { status.Text = e.Message; }
    }
}
