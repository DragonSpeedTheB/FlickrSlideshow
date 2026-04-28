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

        // Resolve default user ID if it hasn't been looked up yet
        if (RecentUsers.Count == 0 && !string.IsNullOrEmpty(ApiKey))
        {
            App.Log("No users found, seeding default user dragonspeed...");
            await AddUserAsync("dragonspeed");
        }
    }

    // ── Recent users ────────────────────────────────────────────────────

    public List<FlickrUser> RecentUsers { get; private set; } = new();
    public FlickrUser? CurrentUser { get; private set; }

    public event Action? UsersChanged;

    public void LoadRecentUsers()
    {
        try
        {
            var json = ApplicationData.Current.LocalSettings.Values["RecentUsers"] as string;
            if (!string.IsNullOrEmpty(json))
                RecentUsers = JsonSerializer.Deserialize<List<FlickrUser>>(json) ?? new();
        }
        catch { RecentUsers = new(); }

        var lastId = ApplicationData.Current.LocalSettings.Values["LastUserId"] as string;
        if (!string.IsNullOrEmpty(lastId))
            CurrentUser = RecentUsers.Find(u => u.Id == lastId);
    }

    public void SaveRecentUsers() =>
        ApplicationData.Current.LocalSettings.Values["RecentUsers"] = JsonSerializer.Serialize(RecentUsers);

    public void SelectUser(FlickrUser user)
    {
        CurrentUser = user;
        RecentUsers.RemoveAll(u => u.Id == user.Id);
        RecentUsers.Insert(0, user);
        SaveRecentUsers();
        ApplicationData.Current.LocalSettings.Values["LastUserId"] = user.Id;
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
        return $"{Photos.Count} photos loaded";
    }

    public async Task<string> LoadExploreAsync()
    {
        var svc = new FlickrService(ApiKey, "");
        Photos = await svc.GetExplorePhotos(500);
        Shuffle = false;
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
