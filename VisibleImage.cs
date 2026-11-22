using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

static class VisibleImage
{
    public static BitmapSource? FromBmt(byte[] bytes, int start, int limit, List<string> warnings)
    {
        // Embedded JPEGs begin near the metadata boundary. Avoid searching the radiometric grid.
        ReadOnlySpan<byte> marker = [255, 216, 255];
        int end = Math.Min(limit, start + 8192);
        for (int attempt = 0; attempt < 4 && start < end; attempt++)
        {
            int found = bytes.AsSpan(start, end - start).IndexOf(marker);
            if (found < 0) break;
            int offset = start + found;
            try
            {
                using var stream = new MemoryStream(bytes, offset, limit - offset, false);
                return Decode(stream);
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException or IOException or InvalidDataException)
            {
                start = offset + marker.Length;
                if (attempt == 0) warnings.Add("An embedded JPEG could not be decoded.");
            }
        }
        return null;
    }

    public static BitmapSource Decode(Stream stream)
    {
        var original = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        int w = original.PixelWidth, h = original.PixelHeight;
        if (w <= 0 || h <= 0 || (long)w * h > 30_000_000)
            throw new InvalidDataException("RGB dimensions are outside the supported range.");
        var converted = new FormatConvertedBitmap(original, PixelFormats.Bgr24, null, 0);
        byte[] pixels = new byte[checked(w * h * 3)];
        converted.CopyPixels(pixels, w * 3, 0);
        // Materialize pixels independently of the decoder, stream and its source DPI.
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3);
        result.Freeze();
        return result;
    }
}
