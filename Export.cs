using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace BmtExporter;

record ReferencePoint(
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] double Celsius);
record Reference(string SourceSha256, string Provenance, string MeasurementSettings,
    bool SettingsAndCoordinatesConfirmed, double ToleranceC, ReferencePoint[] Points);
record Validation(string Status, string? Provenance = null, string? MeasurementSettings = null,
    int Points = 0, double? Mae = null, double? Rmse = null, double? Maximum = null,
    double? Bias = null, double? ToleranceC = null);

static class Export
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static Validation Validate(ThermalFrame frame, Reference? reference)
    {
        if (reference is null) return new("Validation pending");
        if (!string.Equals(reference.SourceSha256, frame.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !reference.SettingsAndCoordinatesConfirmed || string.IsNullOrWhiteSpace(reference.Provenance) ||
            string.IsNullOrWhiteSpace(reference.MeasurementSettings) ||
            !double.IsFinite(reference.ToleranceC) || reference.ToleranceC <= 0 ||
            reference.Points is not { Length: >= 1 } ||
            reference.Points.Any(p => p is null || p.X < 0 || p.X >= frame.Width || p.Y < 0 ||
                                      p.Y >= frame.Height || !double.IsFinite(p.Celsius)) ||
            reference.Points.Select(p => (p.X, p.Y)).Distinct().Count() != reference.Points.Length)
            throw new InvalidDataException("Invalid IRSoft reference. Check the source hash, settings, tolerance and unique pixel coordinates.");
        double[] errors = reference.Points.Select(p => frame.At(p.X, p.Y) - p.Celsius).ToArray();
        double maximum = errors.Max(Math.Abs);
        return new(maximum <= reference.ToleranceC ? "Validation passed" : "Validation failed",
            reference.Provenance, reference.MeasurementSettings, errors.Length, errors.Average(Math.Abs),
            Math.Sqrt(errors.Average(e => e * e)), maximum, errors.Average(), reference.ToleranceC);
    }

    public static string Batch(IEnumerable<string> sources, string root, IBmtReader reader,
        IReadOnlyDictionary<string, Reference> references, Action<int, int>? progress = null)
    {
        var paths = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string prefix = paths.All(FolderSync.IsBmt) ? "BMT" : paths.All(ThermalFileReader.IsJpeg) ? "FLIR" : "Thermal";
        string run = Path.Combine(root, $"{prefix}_{DateTime.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var summary = new List<object>();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            string? folder = null;
            try
            {
                var frame = reader.Read(path);
                Reference? reference = null;
                if (frame.Backend == Testo890Reader.Backend) references.TryGetValue(frame.Sha256, out reference);
                var validation = Validate(frame, reference);
                string name = $"{i + 1:D4}_{Path.GetFileNameWithoutExtension(path)}";
                folder = Path.Combine(run, name);
                Write(frame, folder, validation, reference);
                summary.Add(new { source = path, status = "Extraction successful", output = name,
                    validation = validation.Status, warnings = Warnings(frame, validation),
                    frame.Width, frame.Height, frame.Sha256, frame.Format });
            }
            catch (Exception e)
            {
                if (folder is not null && Directory.Exists(folder))
                {
                    // The per-file folder contains only this export's flat files.
                    try
                    {
                        foreach (string partial in Directory.EnumerateFiles(folder)) File.Delete(partial);
                        Directory.Delete(folder);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                summary.Add(new { source = path, status = e is UnsupportedBmtException ? "Unsupported file" : "Failed",
                    error = e.Message });
            }
            progress?.Invoke(i + 1, paths.Length);
        }
        WriteJson(Path.Combine(run, "summary.json"), summary);
        return run;
    }

    private static string[] Warnings(ThermalFrame frame, Validation validation) =>
        validation.Status == "Validation pending"
            ? frame.Warnings
            : frame.Warnings.Where(w => w != "Independent IRSoft validation pending.").ToArray();

    private static void Write(ThermalFrame frame, string folder, Validation validation, Reference? reference)
    {
        Directory.CreateDirectory(folder);
        string npy = Path.Combine(folder, "temperature.npy");
        using (var bin = new BinaryWriter(File.Create(npy), Encoding.ASCII))
        {
            bin.Write(new byte[] { 0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59, 1, 0 });
            string dtype = frame.PreciseValues is null ? "<f4" : "<f8";
            string header = FormattableString.Invariant($"{{'descr': '{dtype}', 'fortran_order': False, 'shape': ({frame.Height}, {frame.Width}), }}");
            header += new string(' ', (64 - (10 + header.Length + 1) % 64) % 64) + "\n";
            bin.Write((ushort)header.Length);
            bin.Write(Encoding.ASCII.GetBytes(header));
            if (frame.PreciseValues is double[] precise)
                foreach (double value in precise) bin.Write(value);
            else
                foreach (float value in frame.Values) bin.Write(value);
        }
        using (var csv = new StreamWriter(Path.Combine(folder, "temperature.csv"), false, Encoding.ASCII))
        {
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    if (x > 0) csv.Write(',');
                    csv.Write(frame.PreciseValues is null
                        ? frame.Values[y * frame.Width + x].ToString("R", Invariant)
                        : frame.At(x, y).ToString("R", Invariant));
                }
                csv.WriteLine();
            }
        }
        SavePng(frame.Preview, Path.Combine(folder, "thermal_preview.png"));
        if (frame.Visible is not null) SavePng(frame.Visible, Path.Combine(folder, "visible_image.png"));
        if (frame.SourceDisplay is not null) SavePng(frame.SourceDisplay, Path.Combine(folder, "source_display.png"));
        var stats = frame.Statistics();
        WriteJson(Path.Combine(folder, "metadata.json"), new
        {
            frame.Camera, frame.Width, frame.Height, frame.Layout, frame.Format,
            payloadOffset = frame.PreciseValues is null ? (int?)frame.PayloadOffset : null,
            frame.Acquisition, frame.Radiometry, unit = "C",
            coordinateConvention = "Top left origin, zero based; index [y,x]. Preview generated from the same grid.",
            minimumC = stats.Min, maximumC = stats.Max, averageC = stats.Mean
        });
        var hashes = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToDictionary(
            p => Path.GetFileName(p), p => HashFile(p), StringComparer.Ordinal);
        WriteJson(Path.Combine(folder, "extraction_report.json"), new
        {
            source = frame.Path, sourceSha256 = frame.Sha256, backend = frame.Backend,
            extraction = "Extraction successful", validation, referencePoints = reference?.Points.Select(p => new
            {
                p.X, p.Y, referenceC = p.Celsius, extractedC = frame.At(p.X, p.Y),
                errorC = frame.At(p.X, p.Y) - p.Celsius
            }), warnings = Warnings(frame, validation), outputSha256 = hashes
        });
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteJson(string path, object data) =>
        File.WriteAllText(path, JsonSerializer.Serialize(data, Json));
}
