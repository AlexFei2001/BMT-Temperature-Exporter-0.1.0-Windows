using System.IO;
using System.Text.Json;
using System.Windows;

namespace BmtExporter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--update-qa", var folder, .. var realSessions])
        {
            RunUpdateQa(folder, realSessions);
            return;
        }
        if (e.Args is ["--roi-qa", var report, .. var sessions])
        {
            try
            {
                File.WriteAllText(report, JsonSerializer.Serialize(MonitorQa.Run(sessions), Export.Json));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
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

    private async void RunUpdateQa(string folder, string[] sessions)
    {
        Directory.CreateDirectory(folder);
        string report = Path.Combine(folder, "qa.json");
        try
        {
            var results = await UpdateQa.Run(folder, sessions);
            File.WriteAllText(report, JsonSerializer.Serialize(results, Export.Json));
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, ex.ToString());
            Shutdown(1);
        }
    }
}
