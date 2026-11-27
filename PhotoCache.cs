using System.IO;
using System.Text.Json;

namespace FlickrSlideshow;

internal static class PhotoCache
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlickrSlideshow", "PhotoCache");

    private sealed record CacheFile(DateTime SavedAt, List<FlickrPhoto> Photos);

    public static List<FlickrPhoto>? Load(string key, TimeSpan ttl)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        var entry = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
        if (entry == null || entry.Photos == null)
            throw new InvalidDataException($"Invalid photo cache: {path}");
        return DateTime.UtcNow - entry.SavedAt <= ttl && entry.Photos.Count > 0 ? entry.Photos : null;
    }

    public static void Save(string key, List<FlickrPhoto> photos)
    {
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(GetPath(key), JsonSerializer.Serialize(new CacheFile(DateTime.UtcNow, photos)));
    }

    private static string GetPath(string key)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) key = key.Replace(invalid, '_');
        return Path.Combine(CacheDirectory, key + ".json");
    }
}
