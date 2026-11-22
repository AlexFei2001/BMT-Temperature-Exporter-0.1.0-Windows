using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace BmtExporter;

sealed class FlirSettingsWindow : Window
{
    public FlirOptions? Options { get; private set; }

    public FlirSettingsWindow(FlirOptions? settings)
    {
        Title = "FLIR settings"; Width = 635; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(16) };
        var path = new TextBox { Text = settings?.ExifTool ?? "", Margin = new Thickness(0, 4, 0, 4) };
        var order = new ComboBox { ItemsSource = new[] { "swap16", "native" }, SelectedItem = settings?.ByteOrder,
            HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 125, Margin = new Thickness(0, 5, 0, 6) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        root.Children.Add(new TextBlock { Text = "ExifTool executable" });
        root.Children.Add(path);
        var browse = new WrapPanel();
        RegionEditor.Add(browse, "Browse…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "ExifTool executable|exiftool.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName;
        });
        root.Children.Add(browse);
        root.Children.Add(new TextBlock { Text = "Raw thermal byte order" });
        root.Children.Add(order);
        root.Children.Add(new TextBlock
        {
            Text = "swap16 matches the four previously tested T1020 images. Verify byte order for other files against FLIR software. Keep the ExifTool exe and its exiftool_files folder together.",
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new WrapPanel();
        RegionEditor.Add(buttons, "Save settings", () =>
        {
            try
            {
                var options = new FlirOptions(path.Text.Trim(), order.SelectedItem as string ?? "");
                options.Validate(); options.Save(); Options = options; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        });
        RegionEditor.Add(buttons, "Cancel", () => DialogResult = false);
        root.Children.Add(buttons); root.Children.Add(status); Content = root;
    }
}
