using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml.Media.Imaging;

namespace FlickrSlideshow.Xbox
{
    internal class SlideshowViewModel
    {
        public event Action<BitmapImage, string> ImageReady;
        public event Action<string> StatusChanged;
        public event Action<FlickrUser> CurrentUserChanged;   // fires when user is set or cleared
        public bool IsPaused { get; private set; }
        public bool Shuffle { get; set; } = true;

        private readonly HttpClient _http = new HttpClient();
        private string _apiKey = "";
        private FlickrService _flickr;
        private List<FlickrPhoto> _photos = new List<FlickrPhoto>();
        private int _index;
        private CancellationTokenSource _cts;
        private readonly CoreDispatcher _dispatcher;
        private static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(8);

        // Recent users
        public List<FlickrUser> RecentUsers { get; private set; } = new List<FlickrUser>();
        public FlickrUser CurrentUser { get; private set; }

        // Albums/Collections for current user
        public List<FlickrAlbum> Albums { get; private set; } = new List<FlickrAlbum>();
        public List<FlickrCollection> Collections { get; private set; } = new List<FlickrCollection>();

        public SlideshowViewModel(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            // Load API key
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///appsettings.json"));
                var json = await FileIO.ReadTextAsync(file);
                using (var doc = JsonDocument.Parse(json))
                    _apiKey = doc.RootElement.GetProperty("Flickr").GetProperty("ApiKey").GetString() ?? "";
            }
            catch (Exception ex) { ReportStatus("API key error: " + ex.Message); return; }

            // Load recent users from local settings
            LoadRecentUsers();

            // If we have a last user, select them
            var settings = ApplicationData.Current.LocalSettings;
            var lastId = settings.Values["LastUserId"] as string;
            if (!string.IsNullOrEmpty(lastId))
            {
                var user = RecentUsers.FirstOrDefault(u => u.Id == lastId);
                if (user != null)
                {
                    CurrentUser = user;
                    _flickr = new FlickrService(_apiKey, user.Id);
                    ReportStatus("User: " + user.Name);
                    ReportCurrentUserChanged(user);
                }
            }
        }

        // ── User management ──────────────────────────────────────────────

        public async Task<bool> AddOrSelectUserAsync(string username)
        {
            try
            {
                ReportStatus("Looking up " + username + "...");
                var svc = new FlickrService(_apiKey, "");
                var userId = await svc.GetUserIdFromName(username);

                var user = new FlickrUser { Name = username, Id = userId };
                RecentUsers.RemoveAll(u => u.Id == userId);
                RecentUsers.Insert(0, user);
                SaveRecentUsers();

                CurrentUser = user;
                _flickr = new FlickrService(_apiKey, userId);

                // Save as last user
                ApplicationData.Current.LocalSettings.Values["LastUserId"] = userId;

                ReportStatus("Selected: " + username);
                ReportCurrentUserChanged(user);
                return true;
            }
            catch (Exception ex)
            {
                ReportStatus("User not found: " + ex.Message);
                return false;
            }
        }

        public void SelectUser(FlickrUser user)
        {
            CurrentUser = user;
            _flickr = new FlickrService(_apiKey, user.Id);
            ApplicationData.Current.LocalSettings.Values["LastUserId"] = user.Id;
            ReportStatus("Selected: " + user.Name);
            ReportCurrentUserChanged(user);
        }

        public void RemoveUser(FlickrUser user)
        {
            RecentUsers.RemoveAll(u => u.Id == user.Id);
            SaveRecentUsers();
            if (CurrentUser?.Id == user.Id) CurrentUser = null;
        }

        // ── Load photos ─────────────────────────────────────────────────

        public async Task LoadAllPhotosAsync()
        {
            if (_flickr == null) return;
            ReportStatus("Loading all photos...");
            try
            {
                _photos = await _flickr.GetAllPublicPhotos();
                ReportStatus(_photos.Count + " photos loaded");
            }
            catch (Exception ex) { ReportStatus("Error: " + ex.Message); }
        }

        public async Task LoadExploreAsync()
        {
            ReportStatus("Loading Explore...");
            try
            {
                var svc = new FlickrService(_apiKey, "");
                _photos = await svc.GetExplorePhotos(500);
                Shuffle = false; // Explore is curated order
                ReportStatus("Explore: " + _photos.Count + " photos");
            }
            catch (Exception ex) { ReportStatus("Explore failed: " + ex.Message); }
        }

        public async Task<List<FlickrAlbum>> LoadAlbumsAsync()
        {
            if (_flickr == null) return new List<FlickrAlbum>();
            ReportStatus("Loading albums...");
            try
            {
                Albums = await _flickr.GetAlbums();
                Albums.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
                Collections = await _flickr.GetCollections();
                ReportStatus(Albums.Count + " albums, " + Collections.Count + " collections");
                return Albums;
            }
            catch (Exception ex) { ReportStatus("Albums error: " + ex.Message); return new List<FlickrAlbum>(); }
        }

        public async Task LoadSelectedAlbumsAsync(List<string> albumIds)
        {
            if (_flickr == null) return;
            ReportStatus("Loading album photos...");
            try
            {
                _photos.Clear();
                foreach (var id in albumIds)
                    _photos.AddRange(await _flickr.GetAlbumPhotos(id));
                ReportStatus(_photos.Count + " photos from " + albumIds.Count + " albums");
            }
            catch (Exception ex) { ReportStatus("Error: " + ex.Message); }
        }

        // ── Slideshow controls ──────────────────────────────────────────

        public void Pause()  { IsPaused = true; }
        public void Resume() { IsPaused = false; }
        public void TogglePause() { IsPaused = !IsPaused; }

        // Called by MainPage after photos are loaded — matches WPF Slideshow.Start()
        public void StartSlideshow()
        {
            if (_photos.Count == 0) return;
            StartLoop();
        }

        public void Next()
        {
            if (_photos.Count == 0) return;
            _index = (_index + 1) % _photos.Count;
            _ = ShowCurrentAsync();
        }

        public void Previous()
        {
            if (_photos.Count == 0) return;
            _index = (_index - 1 + _photos.Count) % _photos.Count;
            _ = ShowCurrentAsync();
        }

        public bool HasPhotos => _photos.Count > 0;

        // ── Private ─────────────────────────────────────────────────────

        private void StartLoop()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _index = 0;
            if (Shuffle) ShuffleList(_photos);
            IsPaused = false;
            _ = RunLoopAsync(_cts.Token);
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!IsPaused) { await ShowCurrentAsync(); _index = (_index + 1) % _photos.Count; }
                try { await Task.Delay(SlideDuration, token); } catch (TaskCanceledException) { break; }
            }
        }

        private async Task ShowCurrentAsync()
        {
            if (_photos.Count == 0) return;
            var photo = _photos[_index];
            try
            {
                var bytes = await _http.GetByteArrayAsync(photo.Url);
                await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        using (var ms = new InMemoryRandomAccessStream())
                        {
                            using (var w = new DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); }
                            ms.Seek(0);
                            await bitmap.SetSourceAsync(ms);
                        }
                        ImageReady?.Invoke(bitmap, photo.Title ?? "");
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void ReportStatus(string msg)
        {
            _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusChanged?.Invoke(msg));
        }

        private void ReportCurrentUserChanged(FlickrUser user)
        {
            _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => CurrentUserChanged?.Invoke(user));
        }

        private void LoadRecentUsers()
        {
            try
            {
                var json = ApplicationData.Current.LocalSettings.Values["RecentUsers"] as string;
                if (!string.IsNullOrEmpty(json))
                    RecentUsers = JsonSerializer.Deserialize<List<FlickrUser>>(json) ?? new List<FlickrUser>();
            }
            catch { RecentUsers = new List<FlickrUser>(); }
        }

        private void SaveRecentUsers()
        {
            ApplicationData.Current.LocalSettings.Values["RecentUsers"] = JsonSerializer.Serialize(RecentUsers);
        }

        private static void ShuffleList<T>(IList<T> list)
        {
            var rng = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            { int j = rng.Next(i + 1); var t = list[i]; list[i] = list[j]; list[j] = t; }
        }
    }
}