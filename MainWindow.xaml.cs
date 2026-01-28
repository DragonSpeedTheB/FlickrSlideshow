using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Collections.Concurrent;

namespace FlickrSlideshow
{
    public partial class MainWindow : Window
    {
        private const string ApiKey = "5ba7109e23efeb1b9ce201c03ef489c5";

        private FlickrService _flickr;
        private List<FlickrPhoto> _photos = new();
        private int _index;
        private bool _paused = false;
        private CancellationTokenSource _cts;
        private bool _shuffle = true;

        // Shared HttpClient to reuse connections
        private static readonly HttpClient _httpClient = new HttpClient();

        // Simple in-memory cache for decoded images
        private readonly Dictionary<string, BitmapImage> _imageCache = new Dictionary<string, BitmapImage>();

        // Track in-flight loads to avoid duplicate downloads
        private readonly Dictionary<string, Task<BitmapImage>> _inflightLoads = new Dictionary<string, Task<BitmapImage>>();

        // Limit concurrent prefetches
        private readonly SemaphoreSlim _prefetchSemaphore = new SemaphoreSlim(3);


        private List<FlickrUser> _recentUsers = new();

        // add inside the class, with other fields
        private readonly Dictionary<string, (int Width, int Height)> _originalImageSizes = new Dictionary<string, (int Width, int Height)>();

        public MainWindow()
        {
            InitializeComponent();

#if DEBUG
            // Show debug info textblock in debug builds
            DebugInfoText.Visibility = Visibility.Visible;
#endif

            // Hook up editable ComboBox text changed
            UserComboBox.Loaded += (_, __) =>
            {
                if (UserComboBox.Template.FindName("PART_EditableTextBox", UserComboBox) is TextBox tb)
                    tb.TextChanged += UserComboBox_TextChanged;
            };

            // Load recent users from settings
            LoadUserSettings();

            // Focus keyboard for keybindings
            Loaded += (_, __) => Keyboard.Focus(this);
        }

        // centralized pause/resume handling so behavior is consistent (Escape, Space, Resume button)
        private void SetPaused(bool paused)
        {
            _paused = paused;

            if (paused)
            {
                // show overlay but keep the image visible so the photo + debug info remain visible
                ResumeOverlay.Visibility = Visibility.Visible;

                // restore to windowed so the user can interact with resized window
                ExitFullScreen();
            }
            else
            {
                ResumeOverlay.Visibility = Visibility.Collapsed;

                // return to full screen when resuming
                EnterFullScreen();
            }
        }

        #region User management

        private void LoadUserSettings()
        {
            // Deserialize recent users
            if (!string.IsNullOrEmpty(Properties.Settings.Default.RecentUsers))
            {
                try
                {
                    _recentUsers = JsonSerializer.Deserialize<List<FlickrUser>>(Properties.Settings.Default.RecentUsers)
                                   ?? new List<FlickrUser>();
                }
                catch { _recentUsers = new List<FlickrUser>(); }
            }

            UserComboBox.ItemsSource = _recentUsers;

            // Select last used user
            if (!string.IsNullOrEmpty(Properties.Settings.Default.LastUserId))
            {
                var lastUser = _recentUsers.FirstOrDefault(u => u.Id == Properties.Settings.Default.LastUserId);
                if (lastUser != null)
                {
                    UserComboBox.SelectedItem = lastUser;
                    _flickr = new FlickrService(ApiKey, lastUser.Id);
                    StatusText.Text = $"Last user: {lastUser.Name}";
                    AllPhotosButton.IsEnabled = true;
                    PickAlbumsButton.IsEnabled = true;
                }
            }
        }

        private void UserComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AddUserButton.IsEnabled = !string.IsNullOrWhiteSpace(((TextBox)sender).Text);
        }

        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            if (!(UserComboBox.Template.FindName("PART_EditableTextBox", UserComboBox) is TextBox tb)) return;
            string name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;

            StatusText.Text = "Looking up user ID...";
            AddUserButton.IsEnabled = false;

            try
            {
                var tempService = new FlickrService(ApiKey, "");
                string id = await tempService.GetUserIdFromName(name);

                var user = new FlickrUser { Name = name, Id = id };

                // Remove duplicates and add new user at top
                _recentUsers.RemoveAll(u => u.Id == id);
                _recentUsers.Insert(0, user);

                // Update ComboBox
                UserComboBox.ItemsSource = null;
                UserComboBox.ItemsSource = _recentUsers;
                UserComboBox.SelectedItem = user;

                // Persist settings
                Properties.Settings.Default.RecentUsers = JsonSerializer.Serialize(_recentUsers);
                Properties.Settings.Default.LastUserId = user.Id;
                Properties.Settings.Default.Save();

                _flickr = new FlickrService(ApiKey, user.Id);
                StatusText.Text = $"User '{name}' added and selected!";
                AllPhotosButton.IsEnabled = true;
                PickAlbumsButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"User '{name}' not found: {ex.Message}";
            }
            finally
            {
                AddUserButton.IsEnabled = true;
            }
        }

        private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserComboBox.SelectedItem is FlickrUser user)
            {
                _flickr = new FlickrService(ApiKey, user.Id);

                // Persist last user
                Properties.Settings.Default.LastUserId = user.Id;
                Properties.Settings.Default.Save();

                StatusText.Text = $"Selected user: {user.Name}";
                AllPhotosButton.IsEnabled = true;
                PickAlbumsButton.IsEnabled = true;
            }
        }

        #endregion

        #region Button clicks

        private async void AllPhotos_Click(object sender, RoutedEventArgs e)
        {
            if (_flickr == null) return;

            _photos.Clear();
            _shuffle = true;
            StatusText.Text = "Loading photos... 0";
            AddUserButton.IsEnabled = false;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;

            // Progress reporter
            var progress = new Progress<int>(count =>
            {
                StatusText.Text = $"Loading photos... {count}";
            });

            try
            {
                _photos = await _flickr.GetAllPublicPhotos(progress);
                StatusText.Text = $"Loaded {_photos.Count} photos!";
                EnterFullScreen();
                StartShow();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading photos: {ex.Message}";
            }
            finally
            {
                AddUserButton.IsEnabled = true;
                AllPhotosButton.IsEnabled = true;
                PickAlbumsButton.IsEnabled = true;
            }
        }

        private async void Explore_Click(object sender, RoutedEventArgs e)
        {
            _photos.Clear();
            _shuffle = false;
            StatusText.Text = "Loading Explore photos...";
            AddUserButton.IsEnabled = false;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;

            try
            {
                // Flickr service WITHOUT user context
                var exploreService = new FlickrService(ApiKey, "");

                _photos = await exploreService.GetExplorePhotos(500);

                if (_photos.Count == 0)
                {
                    StatusText.Text = "No Explore photos found.";
                    return;
                }

                StatusText.Text = $"Explore – {_photos.Count} photos";
                EnterFullScreen();
                StartShow();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Explore failed: {ex.Message}";
            }
            finally
            {
                AddUserButton.IsEnabled = true;
                AllPhotosButton.IsEnabled = true;
                PickAlbumsButton.IsEnabled = true;
            }
        }

        private async void Albums_Click(object sender, RoutedEventArgs e)
        {
            if (_flickr == null) return;

            _shuffle = true;
            var albums = await _flickr.GetAlbums();
            albums.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));

            var picker = new AlbumPicker(albums);
            if (picker.ShowDialog() == true)
            {
                EnterFullScreen();
                _photos.Clear();
                foreach (var album in picker.SelectedAlbums)
                    _photos.AddRange(await _flickr.GetAlbumPhotos(album.Id));

                StartShow();
            }
        }

        #endregion

        #region Slideshow

        private void EnterFullScreen()
        {
            TopControls.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;

            // 🔑 FORCE KEYBOARD FOCUS
            Dispatcher.InvokeAsync(() =>
            {
                Activate();
                Focus();
                Keyboard.Focus(this);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }


        private void ExitFullScreen()
        {
            TopControls.Visibility = Visibility.Visible;
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.WindowState = WindowState.Normal;
            this.Topmost = false;
        }

        private void StartShow()
        {
            if (_photos.Count == 0) return;

            if (_shuffle)
                Shuffle(_photos);
            _index = 0;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            // Kick off initial prefetch of current + 2 ahead
            EnsurePrefetch(_index);

            _ = RunSlideshow(_cts.Token);
        }

        private async Task RunSlideshow(CancellationToken token)
        {
            BitmapImage nextImage = await LoadBitmapAsync(_photos[_index].Url);

            // Ensure we keep two images prefetched ahead
            EnsurePrefetch(_index);

            while (!token.IsCancellationRequested)
            {   
                while (_paused)
                    await Task.Delay(200, token);

                var currentImage = nextImage;
                _index = (_index + 1) % _photos.Count;

                // Get next image (should be cached/prefetched usually)
                nextImage = await LoadBitmapAsync(_photos[_index].Url);

                // Maintain two-image prefetch window
                EnsurePrefetch(_index);

                // previous index corresponds to the image we are about to show
                int prevIndex = (_index - 1 + _photos.Count) % _photos.Count;
                string prevUrl = _photos[prevIndex].Url;

                await ShowImage(currentImage, token, prevUrl);
            }
        }

        private async Task ShowImage(BitmapImage bitmap, CancellationToken token, string url)
        {
            SlideImage.Source = bitmap;

#if DEBUG
            UpdateDebugInfo(url, bitmap);
#endif

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2));
            SlideImage.BeginAnimation(OpacityProperty, fadeIn);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), token);
            }
            catch (TaskCanceledException) { }

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(2));
            SlideImage.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void ShowNextImage()
        {
            if (_photos.Count == 0) return;

            _index = (_index + 1) % _photos.Count;
            string url = _photos[_index].Url;
            _ = LoadBitmapAsync(_photos[_index].Url).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    SlideImage.Source = t.Result;
#if DEBUG
                    UpdateDebugInfo(url, t.Result);
#endif
                });
            });
        }

        private void ShowPreviousImage()
        {
            if (_photos.Count == 0) return;

            _index = (_index - 1 + _photos.Count) % _photos.Count;
            string url = _photos[_index].Url;
            _ = LoadBitmapAsync(_photos[_index].Url).ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    SlideImage.Source = t.Result;
#if DEBUG
                    UpdateDebugInfo(url, t.Result);
#endif
                });
            });
        }

        private void EnsurePrefetch(int currentIndex)
        {
            if (_photos.Count == 0) return;

            // Prefetch current, +1 and +2
            for (int i = 0; i <= 2; i++)
            {
                int idx = (currentIndex + i) % _photos.Count;
                string url = _photos[idx].Url;

                // Start load if not cached or inflight
                lock (_imageCache)
                {
                    if (_imageCache.ContainsKey(url) || _inflightLoads.ContainsKey(url))
                        continue;

                    // Start the async load (no extra Task.Run needed)
                    var task = LoadBitmapAsync(url);
                    _inflightLoads[url] = task;

                    // When complete, move to cache and remove inflight
                    _ = task.ContinueWith(t =>
                    {
                        lock (_imageCache)
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                            {
                                _imageCache[url] = t.Result;
                            }
                            _inflightLoads.Remove(url);
                        }
                    }, TaskScheduler.Default);
                }
            }
        }

        private static void Shuffle<T>(IList<T> list)
        {
            var rng = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        #endregion

        #region Image loading

        private async Task<BitmapImage> LoadBitmapAsync(string url)
        {
            // Return from cache if present
            Task<BitmapImage> inflight = null;
            lock (_imageCache)
            {
                if (_imageCache.TryGetValue(url, out var cached))
                    return cached;

                if (_inflightLoads.TryGetValue(url, out inflight))
                {
                    // we capture the inflight task and await it outside the lock
                }
            }

            if (inflight != null)
                return await inflight.ConfigureAwait(false);

            // Load and decode off-UI thread
            async Task<BitmapImage> LoadInternal()
            {
                await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Download stream
                    using var stream = await _httpClient.GetStreamAsync(url).ConfigureAwait(false);

                    // Create bitmap on background thread and DO NOT set DecodePixelWidth
                    // so we load the original pixel dimensions and let the layout cell handle scaling.
#pragma warning disable CS8603 // Possible null reference return.
                    return await Task.Run(() =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;

                            // Copy stream because original stream will be disposed
                            using var ms = new System.IO.MemoryStream();
                            stream.CopyTo(ms);
                            ms.Position = 0;

                            bitmap.StreamSource = ms;
                            bitmap.EndInit();

                            // capture original pixel dimensions (full image)
                            int originalWidth = bitmap.PixelWidth;
                            int originalHeight = bitmap.PixelHeight;
                            lock (_imageCache)
                            {
                                if (originalWidth > 0 && originalHeight > 0)
                                    _originalImageSizes[url] = (originalWidth, originalHeight);
                            }

                            bitmap.Freeze();
                            return bitmap;
                        }
                        catch
                        {
                            return null;
                        }
                    }).ConfigureAwait(false);
#pragma warning restore CS8603 // Possible null reference return.
                }
                finally
                {
                    _prefetchSemaphore.Release();
                }
            }

            Task<BitmapImage> loadTask;
            lock (_imageCache)
            {
                // Check again in case another thread raced in
                if (_inflightLoads.TryGetValue(url, out var existing))
                {
                    loadTask = existing;
                }
                else
                {
                    loadTask = LoadInternal();
                    _inflightLoads[url] = loadTask;
                }
            }

            var result = await loadTask.ConfigureAwait(false);

            lock (_imageCache)
            {
                if (result != null)
                    _imageCache[url] = result;
                _inflightLoads.Remove(url);
            }

            return result;
        }

        #endregion

        #region Keyboard

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;

            switch (e.Key)
            {
                case Key.Escape:
                    if (_photos.Count > 0 && _cts != null && !_cts.IsCancellationRequested)
                    {
                        // Pause slideshow (keep image visible)
                        SetPaused(true);
                    }
                    else
                    {
                        Close();
                    }
                    e.Handled = true;
                    break;

                case Key.Space:
                    // Toggle pause/resume and update UI consistently
                    SetPaused(!_paused);
                    e.Handled = true;
                    break;

                case Key.Right:
                    ShowNextImage();
                    e.Handled = true;
                    break;

                case Key.Left:
                    ShowPreviousImage();
                    e.Handled = true;
                    break;
            }
        }
        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            // Resume slideshow
            SetPaused(false);
        }


        #endregion

#if DEBUG
        private void UpdateDebugInfo(string url, BitmapImage? bitmap)
        {
            int origW = 0, origH = 0;
            lock (_imageCache)
            {
                if (_originalImageSizes.TryGetValue(url, out var dims))
                {
                    origW = dims.Width;
                    origH = dims.Height;
                }
            }

            // fallback to bitmap pixel dims if original not recorded
            try
            {
                if ((origW == 0 || origH == 0) && bitmap != null)
                {
                    origW = bitmap.PixelWidth;
                    origH = bitmap.PixelHeight;
                }
            }
            catch { origW = origW == 0 ? 0 : origW; origH = origH == 0 ? 0 : origH; }

            // Since we no longer do manual resizing, display reported sizes are the same as original
            string origText = (origW > 0 && origH > 0) ? $"{origW}×{origH}" : "unknown";
            string displayText = origText;

            string text = $"URL: {url}\nOriginal: {origText}\nDisplay: {displayText}";
            Dispatcher.Invoke(() =>
            {
                DebugInfoText.Text = text;
                DebugInfoText.Visibility = Visibility.Visible;
            });
        }
#else
        private void UpdateDebugInfo(string url, BitmapImage? bitmap) { }
#endif
    }
}
