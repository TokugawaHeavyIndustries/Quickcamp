namespace QuickCamp;

public static class Formats
{
    public static readonly string[] All =
        { "flac", "mp3-320", "mp3-v0", "aac-hi", "vorbis", "alac", "wav", "aiff-lossless" };

    public static string FallbackExtension(string format, string itemType)
    {
        if (itemType != "track") return ".zip";
        return format switch
        {
            "flac" => ".flac",
            "mp3-320" or "mp3-v0" => ".mp3",
            "aac-hi" or "alac" => ".m4a",
            "vorbis" => ".ogg",
            "wav" => ".wav",
            "aiff-lossless" => ".aiff",
            _ => ".bin",
        };
    }
}
