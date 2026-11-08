using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuickCamp;

public sealed record Fan(long Id, string Username);

public sealed record CollectionItem(string Artist, string Title, string Type, string? DownloadPage, string? ArtUrl,
    string? PackageTitle = null, string? TralbumType = null, string? ReleaseId = null)
{
    public string Display => $"{Artist} - {Title}";

    public string? MergedDetails { get; init; }

    public string TypeLabel => TralbumType == "t" || (TralbumType is null && Type == "track") ? "Track" : "Album";

    public string PurchaseDetails => MergedDetails ?? (_details ??= SinglePurchaseDetails());

    private string? _details;

    private string SinglePurchaseDetails()
    {
        if (Type != "package") return "Digital";
        var format = PhysicalFormat(PackageTitle);
        if (string.IsNullOrWhiteSpace(PackageTitle)) return "Physical (no details from Bandcamp)";
        return $"{(format == "package" ? "Physical" : format)}: {PackageTitle}";
    }

    private static string PhysicalFormat(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "package";
        var d = description.ToLowerInvariant();
        bool Has(string word) => Regex.IsMatch(d, $@"(^|[^a-z0-9]){word}([^a-z0-9]|$)");

        if (Has("lp") || Has("2xlp") || Has("vinyl") || Has("12\"") || Has("10\"") || Has("7\"") || d.Contains("7-inch") || d.Contains("12-inch"))
            return "Vinyl";
        if (Has("cd") || Has("compact disc")) return "CD";
        if (Has("cassette") || Has("tape") || Has("mc")) return "Cassette";
        if (d.Contains("floppy")) return "Floppy";
        if (d.Contains("splatter")) return "Vinyl";  
        return "package";
    }
}

public sealed class BandcampClient : IDisposable
{
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;

    public BandcampClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _jar,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
    }

    public void SetIdentity(string identity) =>
        _jar.Add(new Cookie("identity", identity, "/", ".bandcamp.com"));

    public async Task<Fan?> GetFanAsync()
    {
        try
        {
            var json = JsonNode.Parse(await GetStringAsync("https://bandcamp.com/api/fan/2/collection_summary"));
            var id = json?["fan_id"]?.GetValue<long>();
            if (id is null or 0) return null;
            var name = json?["collection_summary"]?["username"]?.GetValue<string>() ?? id.ToString()!;
            return new Fan(id.Value, name);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<CollectionItem>> GetCollectionAsync(long fanId, Action<int>? progress = null)
    {
        var items = new List<CollectionItem>();
        var token = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}::a::";

        while (true)
        {
            var body = new JsonObject
            {
                ["fan_id"] = fanId,
                ["older_than_token"] = token,
                ["count"] = 500,
            };
            var json = await PostJsonAsync("https://bandcamp.com/api/fancollection/1/collection_items", body)
                       ?? throw new Exception("Empty response from collection API.");

            var urls = json["redownload_urls"] as JsonObject;
            foreach (var node in json["items"]?.AsArray() ?? new JsonArray())
            {
                if (node is null) continue;
                var key = $"{Str(node["sale_item_type"])}{Str(node["sale_item_id"])}";
                items.Add(new CollectionItem(
                    Artist: Str(node["band_name"]),
                    Title: Str(node["item_title"]),
                    Type: Str(node["item_type"]),
                    DownloadPage: urls?[key]?.GetValue<string>(),
                    ArtUrl: ArtUrlFor(node),
                    PackageTitle: node["package_details"]?["title"]?.ToString(),
                    TralbumType: node["tralbum_type"]?.ToString(),
                    ReleaseId: ReleaseIdFor(node)));
            }
            progress?.Invoke(items.Count);

            if (json["more_available"]?.GetValue<bool>() != true) break;
            token = json["last_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) break;
        }
        return MergeSameRelease(items);
    }

    private static string? ReleaseIdFor(JsonNode item)
    {
        var id = Str(item["tralbum_id"]);
        return id == "" ? null : Str(item["tralbum_type"]) + id;
    }
    private static List<CollectionItem> MergeSameRelease(List<CollectionItem> items)
    {
        var merged = new List<CollectionItem>();
        foreach (var group in items.GroupBy(i => i.ReleaseId ?? Guid.NewGuid().ToString()))
        {
            var copies = group.ToList();
            if (copies.Count == 1)
            {
                merged.Add(copies[0]);
                continue;
            }
            var keep = copies.FirstOrDefault(i => i.Type != "package" && i.DownloadPage is not null)
                       ?? copies.FirstOrDefault(i => i.DownloadPage is not null)
                       ?? copies[0];

            merged.Add(keep with
            {
                ArtUrl = keep.ArtUrl ?? copies.Select(i => i.ArtUrl).FirstOrDefault(a => a is not null),
                MergedDetails = $"{copies.Count} purchases: " + string.Join("; ", copies.Select(i => i.PurchaseDetails)),
            });
        }
        return merged;
    }
    private static string? ArtUrlFor(JsonNode item)
    {
        var url = Str(item["item_art_url"]);
        if (url == "" && long.TryParse(Str(item["item_art_id"]), out var artId) && artId > 0)
            url = $"https://f4.bcbits.com/img/a{artId:D10}_9.jpg";
        if (url == "") return null;
        return Regex.Replace(url, @"_\d+\.jpg$", "_2.jpg");
    }

    public async Task<Dictionary<string, string>> GetDownloadLinksAsync(string downloadPage, CancellationToken ct = default)
    {
        var html = await GetStringAsync(downloadPage, ct);
        var m = Regex.Match(html, "id=\"pagedata\"[^>]*data-blob=\"([^\"]*)\"");
        if (!m.Success) throw new Exception("Couldn't find download data on the page (login expired?).");

        var blob = JsonNode.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
        var downloads = blob?["digital_items"]?[0]?["downloads"] as JsonObject
                        ?? throw new Exception("No downloads listed for this item.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (format, info) in downloads)
        {
            var url = info?["url"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(url)) result[format] = url;
        }
        return result;
    }

    public async Task<string> ResolveDownloadUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var stat = url.Replace("/download/", "/statdownload/") + "&.vrs=1&.rand=" + Random.Shared.Next();
            var text = await _http.GetStringAsync(stat, ct);
            var m = Regex.Match(text, "\"download_url\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return Regex.Unescape(m.Groups[1].Value);
        }
        catch (OperationCanceledException) { throw; }
        catch {}
        return url;
    }

    public async Task<string> DownloadFileAsync(string url, string folder, string baseName, string fallbackExt,
        Action<long, long?>? progress, CancellationToken ct = default)
    {
        var existingPart = Directory.EnumerateFiles(folder, baseName + ".*.part")
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(p)) == baseName);
        long resumeFrom = existingPart is null ? 0 : new FileInfo(existingPart).Length;

        HttpResponseMessage resp;
        try
        {
            resp = await SendWithRetryAsync(() => MakeGet(url, resumeFrom), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(existingPart!);
            existingPart = null;
            resumeFrom = 0;
            resp = await SendWithRetryAsync(() => MakeGet(url, 0), ct);
        }

        using (resp)
        {
            bool resuming = resumeFrom > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
            string part = resuming
                ? existingPart!
                : Path.Combine(folder, baseName + (ExtensionFrom(resp.Content.Headers) ?? fallbackExt) + ".part");
            if (!resuming && existingPart is not null && existingPart != part) File.Delete(existingPart);

            long done = resuming ? resumeFrom : 0;
            long? total = resuming
                ? resp.Content.Headers.ContentRange?.Length
                : resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress?.Invoke(done, total);
                }
            }

            var target = part[..^".part".Length];
            File.Move(part, target, overwrite: true);
            return target;
        }
    }

    private static HttpRequestMessage MakeGet(string url, long resumeFrom)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        return req;
    }
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            var resp = await _http.SendAsync(makeRequest(), HttpCompletionOption.ResponseHeadersRead, ct);
            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (retryable && attempt < 5)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                resp.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var code = resp.StatusCode;
                resp.Dispose();
                throw new HttpRequestException($"HTTP {(int)code}", null, code);
            }
            return resp;
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode?> PostJsonAsync(string url, JsonObject body)
    {
        using var resp = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static string Str(JsonNode? n) => n?.ToString() ?? "";

    private static string? ExtensionFrom(HttpContentHeaders headers)
    {
        var cd = headers.ContentDisposition;
        var name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        var ext = string.IsNullOrEmpty(name) ? null : Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext)) return ext;

        return headers.ContentType?.MediaType switch
        {
            "application/zip" => ".zip",
            "audio/mpeg" => ".mp3",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/aiff" or "audio/x-aiff" => ".aiff",
            _ => null,
        };
    }
    public void Dispose() => _http.Dispose();
}
