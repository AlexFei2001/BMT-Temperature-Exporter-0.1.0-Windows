using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

interface IBmtReader { ThermalFrame Read(string path); }

record ThermalFrame(string Path, string Sha256, int Width, int Height, float[] Values,
    string Camera, string Layout, int PayloadOffset, BitmapSource Preview, BitmapSource? Visible,
    string[] Warnings)
{
    public float At(int x, int y) => Values[checked(y * Width + x)];
    public AcquisitionInfo Acquisition { get; init; } = new(null, null, null, null);
}

sealed class UnsupportedBmtException(string message) : Exception(message);

sealed class Testo890Reader : IBmtReader
{
    public const string Backend = "testo-890-2-record-layout-v1";
    private static readonly byte[] Standard = Convert.FromHexString("1C00000008C0120080020000E0010000");
    private static readonly byte[] Large = Convert.FromHexString("6C00000008004B0000050000C0030000");

    public ThermalFrame Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 54 || stream.Length > 80L * 1024 * 1024)
            throw new UnsupportedBmtException("BMT size is outside the supported limits.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return Decode(path, bytes);
    }

    internal static ThermalFrame Decode(string path, byte[] bytes)
    {
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new UnsupportedBmtException("Expected a Testo BMT with a BMP header.");
        int end = Int(bytes, 2), offset = Int(bytes, 10), w = Int(bytes, 18), h = Int(bytes, 22);
        if ((w, h) is not ((640, 480) or (1280, 960)) || offset != 54 || Int(bytes, 14) != 40 ||
            Int(bytes, 26) != 0x00180001 || Int(bytes, 30) != 0)
            throw new UnsupportedBmtException("Unrecognized native dimensions or BMT header.");
        int start = offset + 3 * w * h;
        if (end < start + 248 || end > bytes.Length || bytes.Length - end is not (0 or 16))
            throw new InvalidDataException("The declared BMT size is inconsistent. The file may be incomplete.");
        var ext = bytes.AsSpan(start, end - start);
        bool legacy = Int(bytes, start) == 56 && Int(bytes, start + 4) == 7110;
        bool cameraLayout = Int(bytes, start) == 6 && Int(bytes, start + 4) == 8;
        int metadataSize = legacy ? 7170 : 248;
        if ((!legacy && !cameraLayout) || ext.Length < metadataSize)
            throw new UnsupportedBmtException("Unrecognized Testo BMT layout.");
        string header = Encoding.Unicode.GetString(ext[..metadataSize]);
        string camera = Regex.Match(header, @"t890-2", RegexOptions.IgnoreCase).Value;
        if (camera.Length == 0)
            camera = Regex.Match(Encoding.Unicode.GetString(ext.Slice(1, metadataSize - 1)),
                @"t890-2", RegexOptions.IgnoreCase).Value;
        if (camera.Length == 0)
            throw new UnsupportedBmtException("This reader requires a recognized Testo 890-2 record.");

        var candidates = new List<(int Width, int Height, int Offset)>();
        foreach (int width in w == 640 ? new[] { 640, 1280 } : new[] { 1280 })
        {
            int height = width * 3 / 4;
            var marker = width == 640 ? Standard : Large;
            int scan = metadataSize;
            while (scan < ext.Length)
            {
                int index = ext[scan..].IndexOf(marker);
                if (index < 0) break;
                int pos = scan + index + marker.Length;
                scan = pos;
                if (pos + 4 * width * height + 8 > ext.Length) continue;
                int after = start + pos + 4 * width * height;
                bool validTrailer = width == 640
                    ? (Int(bytes, after), Int(bytes, after + 4)) is (29, 20) or (24, 8)
                    : (Int(bytes, after), Int(bytes, after + 4)) == (10004, 29);
                if (validTrailer) candidates.Add((width, height, start + pos));
            }
        }
        float headerMin = BitConverter.Int32BitsToSingle(Int(bytes, start + 184));
        float headerMax = BitConverter.Int32BitsToSingle(Int(bytes, start + 188));
        bool multiple = candidates.Count > 1;
        if (multiple && cameraLayout)
            candidates = candidates.Where(c => MatchesExtrema(
                ReadValues(bytes, c.Offset, c.Width * c.Height), headerMin, headerMax, 0.001f)).ToList();
        if (candidates.Count != 1 || (multiple && !cameraLayout))
            throw new UnsupportedBmtException("Could not identify a unique supported temperature record.");
        (w, h, int payload) = candidates[0];
        var values = ReadValues(bytes, payload, w * h);

        var warnings = new List<string> { "Independent IRSoft validation pending." };
        if (cameraLayout && !MatchesExtrema(values, headerMin, headerMax, 0.02f))
            warnings.Add("Stored header extrema differ from the extracted grid.");
        BitmapSource? visible = VisibleImage.FromBmt(bytes, start + metadataSize, payload, warnings);
        return new ThermalFrame(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            w, h, values, camera, multiple ? "890-2 dual grid, header extrema matched" :
            legacy ? "890-2 legacy header" : "890-2 extended header",
            payload, MakePreview(values, w, h), visible, warnings.ToArray())
        { Acquisition = cameraLayout ? AcquisitionInfo.FromExtendedHeader(ext[..248]) : new(null, null, null, null) };
    }

    private static bool MatchesExtrema(float[] values, float min, float max, float tolerance) =>
        Math.Abs(min - values.Min()) <= tolerance && Math.Abs(max - values.Max()) <= tolerance;

    private static float[] ReadValues(byte[] bytes, int payload, int length)
    {
        var values = new float[length];
        for (int i = 0; i < length; i++)
        {
            float value = BitConverter.Int32BitsToSingle(Int(bytes, payload + 4 * i));
            if (!float.IsFinite(value) || value < -273.15f)
                throw new InvalidDataException("The temperature grid contains an invalid value.");
            values[i] = value;
        }
        return values;
    }

    private static int Int(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static BitmapSource MakePreview(float[] values, int width, int height)
    {
        float min = values.Min(), max = values.Max();
        var pixels = new byte[values.Length];
        if (max > min)
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)Math.Clamp(Math.Round(255 * ((double)values[i] - min) / ((double)max - min)), 0, 255);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }
}
