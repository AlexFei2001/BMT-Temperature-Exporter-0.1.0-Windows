using System.IO;

namespace BmtExporter;

sealed class FolderSync(string folder, IBmtReader reader)
{
    private record Entry(long Length, long Modified, int Stable, bool Finished, int Retry);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    public string Folder { get; } = folder;

    // Called serially; the application polls every two seconds.
    public (List<(string Path, string Sha256)> Frames, int Pending, int Unsupported) Scan()
    {
        var frames = new List<(string Path, string Sha256)>();
        int pending = 0, unsupported = 0;
        var paths = Directory.EnumerateFiles(Folder).Where(IsBmt).ToArray();
        var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string missing in entries.Keys.Where(p => !present.Contains(p)).ToArray())
        {
            entries.Remove(missing);
            hashes.Remove(missing);
        }
        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            try
            {
                var current = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, 0, false, 0);
                if (entries.TryGetValue(path, out var previous) &&
                    (previous.Length, previous.Modified) == (current.Length, current.Modified))
                    current = previous with { Stable = Math.Min(previous.Stable + 1, 2) };
                entries[path] = current;
                if (current.Finished) { if (current.Retry > 0) unsupported++; continue; }
                if (current.Stable < 2) { pending++; continue; }
                var frame = reader.Read(path);
                info.Refresh();
                if (info.Length != current.Length || info.LastWriteTimeUtc.Ticks != current.Modified)
                {
                    pending++;
                    continue;
                }
                entries[path] = current with { Finished = true, Retry = 0 };
                if (!hashes.TryGetValue(path, out string? hash) || hash != frame.Sha256)
                    frames.Add((frame.Path, frame.Sha256));
                hashes[path] = frame.Sha256;
            }
            catch (IOException) { pending++; }
            catch (Exception e) when (e is UnsupportedBmtException or InvalidDataException or UnauthorizedAccessException)
            {
                if (!entries.TryGetValue(path, out var current)) { pending++; continue; }
                int retry = current.Retry + 1;
                entries[path] = current with { Retry = retry, Finished = retry >= 3 };
                if (retry >= 3) unsupported++; else pending++;
            }
        }
        return (frames, pending, unsupported);
    }

    public static bool IsBmt(string path) => Path.GetExtension(path).Equals(".bmt", StringComparison.OrdinalIgnoreCase);
}
