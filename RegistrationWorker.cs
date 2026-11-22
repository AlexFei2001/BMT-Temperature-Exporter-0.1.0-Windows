using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace BmtExporter;

record RegistrationFit(double[][] Homography, double[] Residuals, bool[] Inliers, double Rmse, string Overlay);

static class RegistrationWorker
{
    public static async Task<RegistrationFit> Fit(BitmapSource thermal, BitmapSource rgb, Pixel[] src, Pixel[] dst)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "registration", "BmtRegistrationWorker.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException("The registration component is missing. Use the complete registered Studio package.");
        var request = JsonSerializer.Serialize(new
        {
            thermal = Encode(thermal), rgb = Encode(rgb),
            thermalPoints = src.Select(p => new[] { p.X, p.Y }),
            rgbPoints = dst.Select(p => new[] { p.X, p.Y })
        });
        using var process = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start registration.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteAsync(request.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            string response = await output;
            if (response.Length == 0) throw new InvalidOperationException("Registration worker did not return a result. " + await stderr);
            using var json = JsonDocument.Parse(response);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            if (process.ExitCode != 0) throw new InvalidOperationException("Registration worker exited unexpectedly.");
            return json.RootElement.Deserialize<RegistrationFit>(Export.Json)
                ?? throw new InvalidOperationException("Registration response was incomplete.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Registration did not finish within 90 seconds.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static string Encode(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        png.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
}
