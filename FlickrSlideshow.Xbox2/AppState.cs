using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FlickrSlideshow.Core;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace FlickrSlideshow.Xbox2;

/// <summary>
/// Singleton service shared by all pages. Holds user state, photo list,
/// and drives the image loop. Pages navigate each other via Frame.
/// </summary>
public class AppState
{
    public static readonly AppState Instance = new();

    private AppState() { }

    // ── Settings ────────────────────────────────────────────────────────

    public string ApiKey { get; private set; } = "";

    public async Task LoadApiKeyAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///appsettings.json"));
            var json = await FileIO.ReadTextAsync(file);
            using var doc = JsonDocument.Parse(json);
            ApiKey = doc.RootElement.GetProperty("Flickr").GetProperty("ApiKey").GetString() ?? "";
        }
        catch (Exception ex) { App.Log("ApiKey error: " + ex.Message); }
    }

    // ── Debug flag ────────────────────────────────────────────────────────────

    public bool DebugOutput { get; set; } = false;

    private static string DebugFlagPath =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "debug.txt");

    public void LoadDebugFlag()
    {
        try { DebugOutput = File.Exists(DebugFlagPath) && File.ReadAllText(DebugFlagPath).Trim() == "1"; }
        catch { }
    }

    public void SaveDebugFlag()
    {
        try { File.WriteAllText(DebugFlagPath, DebugOutput ? "1" : "0"); }
        catch (Exception ex) { App.Log("SaveDebugFlag error: " + ex.Message); }
    }

    // ── Recent users ────────────────────────────────────────────────────

    public List<FlickrUser> RecentUsers { get; private set; } = new();
    public FlickrUser? CurrentUser { get; private set; }

    public event Action? UsersChanged;

    private static string UsersFilePath =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "users.json");

    private static string LastUserFilePath =>
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "lastuser.txt");

    private bool _usersLoaded = false;

    public async Task LoadRecentUsersAsync()
    {
        if (_usersLoaded) return;
        _usersLoaded = true;

        try
        {
            if (File.Exists(UsersFilePath))
                RecentUsers = JsonSerializer.Deserialize<List<FlickrUser>>(File.ReadAllText(UsersFilePath)) ?? new();
        }
        catch (Exception ex) { App.Log("LoadRecentUsers error: " + ex.Message); RecentUsers = new(); }

        try
        {
            var lastId = File.Exists(LastUserFilePath) ? File.ReadAllText(LastUserFilePath).Trim() : null;
            if (!string.IsNullOrEmpty(lastId))
                CurrentUser = RecentUsers.Find(u => u.Id == lastId);
        }
        catch { }

        // Seed default user only if no users were loaded from disk
        if (RecentUsers.Count == 0 && !string.IsNullOrEmpty(ApiKey))
            await AddUserAsync("dragonspeed");
    }

    public void SaveRecentUsers()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(UsersFilePath, JsonSerializer.Serialize(RecentUsers, options));
        }
        catch (Exception ex) { App.Log("SaveRecentUsers error: " + ex.Message); }
    }

    public void SelectUser(FlickrUser user)
    {
        CurrentUser = user;
        RecentUsers.RemoveAll(u => u.Id == user.Id);
        RecentUsers.Insert(0, user);
        SaveRecentUsers();
        try { File.WriteAllText(LastUserFilePath, user.Id); }
        catch (Exception ex) { App.Log("SaveLastUser error: " + ex.Message); }
        UsersChanged?.Invoke();
    }

    public async Task<bool> AddUserAsync(string username)
    {
        try
        {
            var svc = new FlickrService(ApiKey, "");
            var id = await svc.GetUserIdFromName(username);
            SelectUser(new FlickrUser { Name = username, Id = id });
            return true;
        }
        catch { return false; }
    }

    // ── Photo loading ────────────────────────────────────────────────────

    public List<FlickrPhoto> Photos { get; private set; } = new();
    public bool Shuffle { get; set; } = true;

    public async Task<string> LoadAllPhotosAsync(IProgress<int>? progress = null)
    {
        if (CurrentUser == null) return "No user selected";

        var cached = PhotoCache.TryLoadStream(CurrentUser.Id);
        if (cached != null)
        {
            App.Log($"PhotoCache: stream hit ({cached.Count} photos)");
            Photos = cached;
            Shuffle = true;
            return $"{Photos.Count} photos loaded (cached)";
        }

        var svc = new FlickrService(ApiKey, CurrentUser.Id);
        Photos = await svc.GetAllPublicPhotos(progress);
        Shuffle = true;
        if (Photos.Count > 0) PhotoCache.SaveStream(CurrentUser.Id, Photos);
        return $"{Photos.Count} photos loaded";
    }

    public async Task<string> LoadExploreAsync()
    {
        var cached = PhotoCache.TryLoadExplore();
        if (cached != null)
        {
            App.Log($"PhotoCache: explore hit ({cached.Count} photos)");
            Photos = cached;
            Shuffle = true;
            return $"Explore: {Photos.Count} photos (cached)";
        }

        var svc = new FlickrService(ApiKey, "");
        Photos = await svc.GetExplorePhotos(500);
        Shuffle = true;
        if (Photos.Count > 0) PhotoCache.SaveExplore(Photos);
        return $"Explore: {Photos.Count} photos";
    }

    public async Task<(List<FlickrAlbum> albums, List<FlickrCollection> collections)> LoadAlbumsAsync()
    {
        if (CurrentUser == null) return (new(), new());
        var svc = new FlickrService(ApiKey, CurrentUser.Id);
        var albums = await svc.GetAlbums();
        albums.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        var collections = await svc.GetCollections();
        return (albums, collections);
    }

    public async Task<string> LoadAlbumPhotosAsync(List<string> albumIds)
    {
        if (CurrentUser == null) return "No user selected";
        var svc = new FlickrService(ApiKey, CurrentUser.Id);
        Photos = new();
        int cacheHits = 0;
        foreach (var id in albumIds)
        {
            var cached = PhotoCache.TryLoadAlbum(CurrentUser.Id, id);
            if (cached != null)
            {
                App.Log($"PhotoCache: album hit {id} ({cached.Count} photos)");
                Photos.AddRange(cached);
                cacheHits++;
            }
            else
            {
                var fetched = await svc.GetAlbumPhotos(id);
                if (fetched.Count > 0) PhotoCache.SaveAlbum(CurrentUser.Id, id, fetched);
                Photos.AddRange(fetched);
            }
        }
        var source = cacheHits == albumIds.Count ? " (cached)" : cacheHits > 0 ? " (partial cache)" : "";
        return $"{Photos.Count} photos from {albumIds.Count} album(s){source}";
    }
}

/// <summary>
/// Caches resolved photo lists to LocalCacheFolder as JSON files.
/// Cache entries expire after their specified TTL.
/// </summary>
internal static class PhotoCache
{
    private static readonly TimeSpan AlbumTtl   = TimeSpan.FromHours(24);
    private static readonly TimeSpan StreamTtl  = TimeSpan.FromHours(24);
    private static readonly TimeSpan ExploreTtl = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    private static string CacheDir =>
        ApplicationData.Current.LocalCacheFolder.Path;

    private static string AlbumKey(string userId, string albumId) =>
        Path.Combine(CacheDir, $"album_{Sanitize(userId)}_{Sanitize(albumId)}.json");

    private static string StreamKey(string userId) =>
        Path.Combine(CacheDir, $"stream_{Sanitize(userId)}.json");

    private static string ExploreKey() =>
        Path.Combine(CacheDir, "explore.json");

    private static string Sanitize(string s) =>
        string.Concat(s.Split(Path.GetInvalidFileNameChars()));

    // ── Public API ───────────────────────────────────────────────────────────

    public static List<FlickrPhoto>? TryLoadAlbum(string userId, string albumId) =>
        TryLoad(AlbumKey(userId, albumId), AlbumTtl);

    public static void SaveAlbum(string userId, string albumId, List<FlickrPhoto> photos) =>
        Save(AlbumKey(userId, albumId), photos);

    public static List<FlickrPhoto>? TryLoadStream(string userId) =>
        TryLoad(StreamKey(userId), StreamTtl);

    public static void SaveStream(string userId, List<FlickrPhoto> photos) =>
        Save(StreamKey(userId), photos);

    public static List<FlickrPhoto>? TryLoadExplore() =>
        TryLoad(ExploreKey(), ExploreTtl);

    public static void SaveExplore(List<FlickrPhoto> photos) =>
        Save(ExploreKey(), photos);

    // ── Private helpers ──────────────────────────────────────────────────────

    private sealed record CacheFile(DateTime SavedAt, List<FlickrPhoto> Photos);

    private static List<FlickrPhoto>? TryLoad(string path, TimeSpan ttl)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (entry == null) return null;
            if (DateTime.UtcNow - entry.SavedAt > ttl)
            {
                File.Delete(path);
                return null;
            }
            return entry.Photos.Count > 0 ? entry.Photos : null;
        }
        catch { return null; }
    }

    private static void Save(string path, List<FlickrPhoto> photos)
    {
        try
        {
            var entry = new CacheFile(DateTime.UtcNow, photos);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, _opts));
        }
        catch (Exception ex) { App.Log("PhotoCache.Save error: " + ex.Message); }
    }
}
