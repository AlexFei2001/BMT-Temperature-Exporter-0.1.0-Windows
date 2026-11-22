using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace BmtExporter;

static class Qa
{
    public static int Run(string input, string output)
    {
        Directory.CreateDirectory(output);
        string report = Path.Combine(output, "qa.json");
        try
        {
            var reader = new Testo890Reader();
            string[] paths = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                .Where(FolderSync.IsBmt).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var accepted = new List<string>();
            var scanned = new List<object>();
            foreach (string path in paths)
            {
                try
                {
                    var frame = reader.Read(path);
                    accepted.Add(path);
                    scanned.Add(new { path, frame.Width, frame.Height, frame.Layout, frame.PayloadOffset,
                        min = frame.Values.Min(), max = frame.Values.Max(), visible = frame.Visible is not null,
                        warnings = frame.Warnings });
                }
                catch (Exception e) { scanned.Add(new { path, error = e.Message, type = e.GetType().Name }); }
            }
            Check(accepted.Count > 0, "No supported real sample.");
            string source = accepted[0];
            var frame0 = reader.Read(source);
            var frame1 = reader.Read(source);
            Check(frame0.Values.SequenceEqual(frame1.Values) && frame0.Sha256 == frame1.Sha256, "Repeatability");
            byte[] sourceBytes = File.ReadAllBytes(source);
            byte[] corrupt = (byte[])sourceBytes.Clone();
            corrupt[0] = (byte)'Z';
            Reject<UnsupportedBmtException>(() => Testo890Reader.Decode(source, corrupt));
            sourceBytes.CopyTo(corrupt, 0);
            BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(18), 641);
            Reject<UnsupportedBmtException>(() => Testo890Reader.Decode(source, corrupt));
            Reject<InvalidDataException>(() => Testo890Reader.Decode(source, sourceBytes[..^32]));
            sourceBytes.CopyTo(corrupt, 0);
            BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(frame0.PayloadOffset), 0x7fc00000);
            Reject<InvalidDataException>(() => Testo890Reader.Decode(source, corrupt));
            byte[] pixels = new byte[frame0.Values.Length];
            frame0.Preview.CopyPixels(pixels, frame0.Width, 0);
            float min = frame0.Values.Min(), max = frame0.Values.Max();
            foreach ((int x, int y) in new[] { (0, 0), (12, 25), (frame0.Width - 1, frame0.Height - 1) })
            {
                int i = y * frame0.Width + x;
                float expected = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    sourceBytes.AsSpan(frame0.PayloadOffset + 4 * i)));
                Check(expected == frame0.At(x, y), "Coordinate correspondence");
                byte gray = max > min ? (byte)Math.Round(255 * ((double)expected - min) / ((double)max - min)) : (byte)0;
                Check(gray == pixels[i], "Preview orientation");
            }
            var reference = new Reference(frame0.Sha256, "Synthetic QA reference", "Synthetic settings",
                true, 0.1, [new(0, 0, frame0.At(0, 0) + 0.25), new(1, 0, frame0.At(1, 0) - 0.25)]);
            var result = Export.Validate(frame0, reference);
            Check(result.Status == "Validation failed" && result.Mae == 0.25 && result.Rmse == 0.25 &&
                  result.Maximum == 0.25 && result.Bias == 0, "Validation metrics");
            Check(Export.Validate(frame0, reference with { ToleranceC = 0.25 }).Status == "Validation passed",
                "Validation pass");
            Check(Export.Validate(frame0, null).Status == "Validation pending", "Validation pending");
            Reject<InvalidDataException>(() => Export.Validate(frame0, reference with { SourceSha256 = "wrong" }));

            string syncRoot = Path.Combine(output, "sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(syncRoot);
            string syncPath = Path.Combine(syncRoot, "sample.bmt");
            var sync = new FolderSync(syncRoot, reader);
            using (var locked = new FileStream(syncPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                locked.Write(sourceBytes, 0, sourceBytes.Length / 2);
                locked.Flush();
                for (int i = 0; i < 4; i++) Check(sync.Scan().Frames.Count == 0, "Locked partial file imported");
                locked.Write(sourceBytes, sourceBytes.Length / 2, sourceBytes.Length - sourceBytes.Length / 2);
            }
            Check(sync.Scan().Frames.Count == 0 && sync.Scan().Frames.Count == 0 &&
                  sync.Scan().Frames.Count == 1, "Completed file not imported");
            Check(sync.Scan().Frames.Count == 0, "Duplicate import");
            File.SetLastWriteTimeUtc(syncPath, DateTime.UtcNow.AddMinutes(3));
            Check(sync.Scan().Frames.Count == 0 && sync.Scan().Frames.Count == 0 &&
                  sync.Scan().Frames.Count == 0, "Identical content reimported");
            File.WriteAllBytes(syncPath, sourceBytes[..^32]);
            for (int i = 0; i < 5; i++) Check(sync.Scan().Frames.Count == 0, "Unlocked partial file imported");
            File.WriteAllBytes(syncPath, sourceBytes);
            for (int i = 0; i < 3; i++) Check(sync.Scan().Frames.Count == 0, "Recovered content duplicated");
            if (accepted.FirstOrDefault(p => Export.HashFile(p) != frame0.Sha256) is string different)
            {
                File.Copy(different, syncPath, true);
                File.SetLastWriteTimeUtc(syncPath, DateTime.UtcNow.AddMinutes(4));
                Check(sync.Scan().Frames.Count == 0 && sync.Scan().Frames.Count == 0 &&
                      sync.Scan().Frames.Count == 1, "Changed content not imported");
            }
            File.Delete(syncPath);
            Directory.Delete(syncRoot);

            var empty = new Dictionary<string, Reference>();
            string missing = Path.Combine(output, Guid.NewGuid() + ".bmt");
            string run = Export.Batch([source, source, missing], output, reader, empty);
            string repeated = Export.Batch([source], output, reader, empty);
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json"))))
                Check(doc.RootElement.GetArrayLength() == 2 &&
                      doc.RootElement[0].GetProperty("status").GetString() == "Extraction successful" &&
                      doc.RootElement[1].GetProperty("status").GetString() == "Failed", "Batch recovery and dedup");
            string folder = Directory.GetDirectories(run).Single();
            string npy = Path.Combine(folder, "temperature.npy");
            using (var binary = new BinaryReader(File.OpenRead(npy)))
            {
                Check(binary.ReadBytes(8).SequenceEqual(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 }), "NPY signature");
                int size = binary.ReadUInt16();
                Check(System.Text.Encoding.ASCII.GetString(binary.ReadBytes(size))
                    .Contains($"({frame0.Height}, {frame0.Width})"), "NPY shape");
                foreach (float value in frame0.Values) Check(binary.ReadSingle() == value, "NPY round trip");
                Check(binary.BaseStream.Position == binary.BaseStream.Length, "NPY trailing data");
            }
            int yCsv = 0;
            foreach (string row in File.ReadLines(Path.Combine(folder, "temperature.csv")))
            {
                var cells = row.Split(',');
                Check(cells.Length == frame0.Width, "CSV width");
                for (int x = 0; x < cells.Length; x++)
                    Check(float.Parse(cells[x], CultureInfo.InvariantCulture) == frame0.At(x, yCsv), "CSV round trip");
                yCsv++;
            }
            Check(yCsv == frame0.Height && Export.HashFile(source) == frame0.Sha256, "CSV height or source changed");
            string repeatedFolder = Directory.GetDirectories(repeated).Single();
            foreach (string file in Directory.GetFiles(folder))
                Check(Export.HashFile(file) == Export.HashFile(Path.Combine(repeatedFolder, Path.GetFileName(file))),
                    "Unstable export hash");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed = true, files = paths.Length, supported = accepted.Count, unsupported = paths.Length - accepted.Count,
                independentIrSoftValidation = "pending", roundTripDirectory = folder, results = scanned
            }, Export.Json));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = e.ToString() }, Export.Json));
            return 1;
        }
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
    }
}
