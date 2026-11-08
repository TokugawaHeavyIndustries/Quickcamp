using System.IO.Compression;
using System.Reflection;

namespace QuickCamp;
public static class Assets
{
    private static Icon? _appIcon;
    private static Image? _logo;

    public static Icon AppIcon => _appIcon ??=
        Icon.ExtractIcon(Environment.ProcessPath!, 0, 32) ?? SystemIcons.Application;

    public static Image Logo => _logo ??= LoadImage("QuickCamp.logo.png");

    private static Image LoadImage(string name)
    {
        using var stream = Open(name);
        using var original = Image.FromStream(stream);
        return new Bitmap(original);
    }
    public static string ExtractWebView2Loader(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "WebView2Loader.dll");

        using var loader = new MemoryStream();
        using (var gzip = new GZipStream(Open("QuickCamp.WebView2Loader.dll.gz"), CompressionMode.Decompress))
            gzip.CopyTo(loader);

        if (File.Exists(path) && new FileInfo(path).Length == loader.Length)
            return folder;

        try
        {
            File.WriteAllBytes(path, loader.ToArray());
        }
        catch (IOException)
        {}
        return folder;
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"'{name}' is missing");
}
