using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuickCamp;

public sealed class ArtCache : IDisposable
{
    private const int MemoryLimit = 400;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _diskDir;
    private readonly int _thumbSize;
    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _gate = new(6); 
    private readonly Dictionary<string, Image> _images = new();
    private readonly LinkedList<string> _recent = new(); 
    private readonly HashSet<string> _pending = new();
    private readonly HashSet<string> _failed = new();
    private volatile HashSet<string> _wanted = new();

    public event Action? Loaded;

    public ArtCache(string diskDir, int thumbSize)
    {
        _diskDir = diskDir;
        _thumbSize = thumbSize;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Directory.CreateDirectory(diskDir);
        Task.Run(DeleteOtherSizes);
    }

    private string ThumbFile(string url) =>
        Path.Combine(_diskDir, $"{Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath)}_{_thumbSize}.jpg");

    private void DeleteOtherSizes()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_diskDir))
                if (!file.EndsWith($"_{_thumbSize}.jpg", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
        }
        catch {}
    }
    public void SetVisible(IEnumerable<string> urls) => _wanted = new HashSet<string>(urls);
    public Image? Get(string? url)
    {
        if (url is null || _failed.Contains(url)) return null;

        if (_images.TryGetValue(url, out var img))
        {
            _recent.Remove(url);
            _recent.AddFirst(url);
            return img;
        }

        if (_pending.Add(url))
            _ = Task.Run(() => LoadAsync(url));
        return null;
    }

    private async Task LoadAsync(string url)
    {
        Image? thumb = null;
        bool failed = false;

        await _gate.WaitAsync();
        try
        {
            if (!_wanted.Contains(url)) return;

            var file = ThumbFile(url);
            if (File.Exists(file))
            {
                using var saved = Image.FromFile(file);
                thumb = new Bitmap(saved);  
            }
            else
            {
                var bytes = await Http.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                using var original = Image.FromStream(ms);
                thumb = MakeThumbnail(original, _thumbSize);
                try { SaveJpeg(thumb, file); } catch {}
            }
        }
        catch
        {
            failed = true;
        }
        finally
        {
            _gate.Release();
            _ui.Post(_ => Finish(url, thumb, failed), null);
        }
    }

    private void Finish(string url, Image? thumb, bool failed)
    {
        _pending.Remove(url);
        if (failed) _failed.Add(url);
        if (thumb is null) return;

        _images[url] = thumb;
        _recent.AddFirst(url);
        while (_recent.Count > MemoryLimit)
        {
            var oldest = _recent.Last!.Value;
            _recent.RemoveLast();
            if (_images.Remove(oldest, out var old)) old.Dispose();
        }
        Loaded?.Invoke();
    }

    private static Image MakeThumbnail(Image src, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
        return bmp;
    }

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static void SaveJpeg(Image image, string path)
    {
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        image.Save(path, JpegCodec, quality);
    }

    public void Dispose()
    {
        foreach (var img in _images.Values) img.Dispose();
        _images.Clear();
        _gate.Dispose();
    }
}
