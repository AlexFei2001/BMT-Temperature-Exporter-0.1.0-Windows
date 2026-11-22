using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

record FlirOptions(string ExifTool, string ByteOrder)
{
    private static string OptionsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BMT Thermal Analysis Studio", "flir-settings.json");

    public static FlirOptions? Load()
    {
        try { return JsonSerializer.Deserialize<FlirOptions>(File.ReadAllText(OptionsPath), Export.Json); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath)!);
        File.WriteAllText(OptionsPath, JsonSerializer.Serialize(this, Export.Json));
    }
    public void Validate()
    {
        if (ByteOrder is not ("swap16" or "native")) throw new InvalidOperationException("Choose the FLIR byte order in FLIR settings.");
        if (string.IsNullOrWhiteSpace(ExifTool) || !Path.IsPathFullyQualified(ExifTool) || !File.Exists(ExifTool))
            throw new InvalidOperationException("Select your ExifTool executable in FLIR settings.");
    }
}

sealed class ThermalFileReader(FlirOptions? options) : IBmtReader
{
    public static bool SupportedPath(string path) => FolderSync.IsBmt(path) || IsJpeg(path);
    public static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
    public ThermalFrame Read(string path)
    {
        if (FolderSync.IsBmt(path)) return new Testo890Reader().Read(path);
        if (!IsJpeg(path)) throw new UnsupportedBmtException("Choose a Testo BMT or a FLIR radiometric JPEG.");
        if (options is null) throw new InvalidOperationException("Open FLIR settings to choose ExifTool and the byte order.");
        return FlirReader.Read(path, options);
    }
}

static class FlirReader
{
    private record Response(int Width, int Height, JsonElement Metadata, string Temperature);

    public static ThermalFrame Read(string path, FlirOptions options)
    {
        options.Validate();
        path = Path.GetFullPath(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is < 4 or > 80L * 1024 * 1024 || source.ReadByte() != 255 || source.ReadByte() != 216)
            throw new UnsupportedBmtException("Expected a supported-size radiometric JPEG.");
        source.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        Response result;
        try
        {
            result = Task.Run(() => RegistrationWorker.Invoke<Response>(new
            {
                operation = "flir", path, exiftool = options.ExifTool, byteOrder = options.ByteOrder
            })).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no raw thermal image", StringComparison.Ordinal))
        {
            throw new UnsupportedBmtException("The JPEG has no FLIR raw thermal data. Open an original FLIR radiometric JPEG.");
        }
        long count = (long)result.Width * result.Height;
        if (result.Width <= 0 || result.Height <= 0 || count > 8_000_000)
            throw new InvalidDataException("Invalid FLIR thermal dimensions.");
        byte[] data = Convert.FromBase64String(result.Temperature);
        if (data.LongLength != count * sizeof(double)) throw new InvalidDataException("Incomplete FLIR temperature matrix.");
        var values = new double[(int)count];
        Buffer.BlockCopy(data, 0, values, 0, data.Length);
        if (values.Any(t => !double.IsFinite(t))) throw new InvalidDataException("Invalid FLIR temperature values.");
        var metadata = result.Metadata.GetProperty("effective_parameters");
        string camera = metadata.TryGetProperty("Model", out var model) ? model.ToString() : "FLIR";
        double min = values.Min(), max = values.Max();
        var gray = new byte[values.Length];
        if (max > min)
            for (int i = 0; i < values.Length; i++)
                gray[i] = (byte)Math.Clamp(Math.Round(255 * (values[i] - min) / (max - min)), 0, 255);
        var preview = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Gray8, null, gray, result.Width);
        preview.Freeze();
        source.Position = 0;
        return new(path, hash, result.Width, result.Height, [], camera, "FLIR radiometric JPEG", 0,
            preview, null, ["Comparison with FLIR software is pending."])
        {
            Backend = "FLIR-ExifTool-Planck",
            Format = "FLIR R-JPEG",
            PreciseValues = values,
            SourceDisplay = VisibleImage.Decode(source),
            Radiometry = result.Metadata
        };
    }
}
