using System.IO;
using System.Text.Json;
using System.Windows;

namespace BmtExporter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--qa", var input, var output])
        {
            Shutdown(Qa.Run(input, output));
            return;
        }
        if (e.Args is ["--flir-export", var exiftool, var byteOrder, var destination, .. var files] && files.Length > 0)
        {
            var reader = new ThermalFileReader(new FlirOptions(exiftool, byteOrder));
            string run = Export.Batch(files, destination, reader, new Dictionary<string, Reference>());
            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json")));
            Shutdown(summary.RootElement.EnumerateArray().All(r =>
                r.GetProperty("status").GetString() == "Extraction successful") ? 0 : 2);
            return;
        }
        var window = new MainWindow();
        window.Show();
    }
}
