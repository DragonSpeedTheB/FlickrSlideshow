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
        var svc = new FlickrService(ApiKey, CurrentUser.Id);
        Photos = await svc.GetAllPublicPhotos(progress);
        Shuffle = true;
        return $"{Photos.Count} photos loaded";
    }

    public async Task<string> LoadExploreAsync()
    {
        var svc = new FlickrService(ApiKey, "");
        Photos = await svc.GetExplorePhotos(500);
        Shuffle = true;
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
        foreach (var id in albumIds)
            Photos.AddRange(await svc.GetAlbumPhotos(id));
        return $"{Photos.Count} photos from {albumIds.Count} album(s)";
    }
}
