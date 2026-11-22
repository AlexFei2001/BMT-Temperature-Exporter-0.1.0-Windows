using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BmtExporter;

record AcquisitionInfo(DateTime? CameraClock, string? SettingsFingerprint, double? Emissivity, double? ReflectedC)
{
    public static AcquisitionInfo FromExtendedHeader(ReadOnlySpan<byte> header)
    {
        DateTime? clock = null;
        try
        {
            var time = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(header[8..]));
            if (time.Year is >= 2000 and <= 2100) clock = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException) { }
        double e = BinaryPrimitives.ReadDoubleLittleEndian(header[120..]);
        double reflected = BinaryPrimitives.ReadDoubleLittleEndian(header[144..]);
        if (!(e > 0 && e <= 1) || !double.IsFinite(reflected) || reflected < -273.15)
            return new(clock, null, null, null);
        return new(clock, Convert.ToHexString(SHA256.HashData(header[120..184])).ToLowerInvariant(), e, reflected);
    }
}
