using System.Text;

namespace QuickCamp;

public sealed class LibraryScan
{
    public HashSet<string> Complete { get; } = new();
    public Dictionary<string, long> Partial { get; } = new();
    public bool IsComplete(CollectionItem item) => Complete.Contains(LibraryScanner.Key(item));
    public long PartialBytes(CollectionItem item) => Partial.GetValueOrDefault(LibraryScanner.Key(item));
}
public static class LibraryScanner
{
    public static string Key(CollectionItem item) => Normalize(item.Artist) + Normalize(item.Title);
    public static LibraryScan Scan(string folder)
    {
        var scan = new LibraryScan();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return scan;

        foreach (var file in SafeFiles(folder))
            AddFile(scan, file, prefix: "");

        foreach (var dir in SafeDirs(folder))
        {
            var name = Normalize(Path.GetFileName(dir));
            AddComplete(scan, name);

            foreach (var sub in SafeDirs(dir))
                AddComplete(scan, name + Normalize(Path.GetFileName(sub)));
            foreach (var file in SafeFiles(dir))
                AddFile(scan, file, prefix: name);
        }
        return scan;
    }

    private static void AddFile(LibraryScan scan, string path, string prefix)
    {
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
            var key = prefix + Normalize(baseName);
            if (key != "") scan.Partial[key] = new FileInfo(path).Length;
        }
        else
        {
            AddComplete(scan, prefix + Normalize(Path.GetFileNameWithoutExtension(path)));
        }
    }

    private static void AddComplete(LibraryScan scan, string key)
    {
        if (key != "") scan.Complete.Add(key);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }
}