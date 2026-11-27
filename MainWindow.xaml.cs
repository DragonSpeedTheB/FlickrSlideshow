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
using System.Windows.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace FlickrSlideshow
{
    public partial class MainWindow : Window
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsDisplayRequired = 0x00000002;
        private const uint EsSystemRequired = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint flags);

        private string ApiKey;


        private FlickrService? _flickr;
        private List<FlickrPhoto> _photos = new();
        private bool _paused = false;

        // restore recent users list (was removed during refactor)
        private List<FlickrUser> _recentUsers = new();

        private XboxStyleSlideshow _slideshow;

        private bool _shuffle = true;

        public MainWindow()
        {
            InitializeComponent();

            // show assembly version in initial window
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = asm?.GetName().Version;
            VersionText.Text = $"Version: {version?.ToString(3) ?? "unknown"}";

            using var settingsStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlickrSlideshow.Appsettings.json")
                ?? throw new InvalidOperationException("Embedded Flickr configuration is missing.");
            var config = new ConfigurationBuilder().AddJsonStream(settingsStream).Build();
            ApiKey = config["Flickr:ApiKey"] ?? throw new InvalidOperationException("Flickr API key is missing.");
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("Flickr API key is empty.");

            _slideshow = new XboxStyleSlideshow(
                onShow: (bitmap, url, title) =>
                {
                    SlideImage.Source = bitmap;
                    SlideImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.5)));
                    CaptionText.Text = title ?? "";
                    DebugInfoText.Text = $"URL: {url}\nPixels: {bitmap.PixelWidth} x {bitmap.PixelHeight}";
                    DebugInfoText.Visibility = DebugOutputCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                    return Task.CompletedTask;
                },
                onNotice: message =>
                {
                    RateLimitText.Text = message ?? "";
                    RateLimitBanner.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
                },
                onError: message => StatusText.Text = message);

            _slideshow.SlideDurationSeconds = Math.Clamp(Properties.Settings.Default.SlideDurationSeconds, 15, 300);
            _slideshow.DebugOutput = Properties.Settings.Default.DebugOutput;
            SlideDurationValue.Text = _slideshow.SlideDurationSeconds.ToString();
            DebugOutputCheckBox.IsChecked = _slideshow.DebugOutput;
            Closed += (_, _) =>
            {
                _slideshow.Stop();
                SetThreadExecutionState(EsContinuous);
            };

            // Hook up editable ComboBox text changed
            UserComboBox.Loaded += (_, __) =>
            {
                if (UserComboBox.Template.FindName("PART_EditableTextBox", UserComboBox) is TextBox tb)
                    tb.TextChanged += UserComboBox_TextChanged;
            };

            // use async initialization so we can lookup a default user if none saved
            _ = LoadUserSettingsAsync();

            Loaded += (_, __) => Keyboard.Focus(this);
        }

        private async Task LoadUserSettingsAsync()
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

            // populate combo box immediately with whatever we have
            UserComboBox.ItemsSource = _recentUsers;

            // Select last used user if available
            if (!string.IsNullOrEmpty(Properties.Settings.Default.LastUserId))
            {
                var lastUser = _recentUsers.FirstOrDefault(u => u.Id == Properties.Settings.Default.LastUserId);
                if (lastUser != null)
                {
                    UserComboBox.SelectedItem = lastUser;
                    _flickr = new FlickrService(ApiKey, lastUser.Id);
                    Dispatcher.Invoke(() => StatusText.Text = $"Last user: {lastUser.Name}");
                    Dispatcher.Invoke(() => { AllPhotosButton.IsEnabled = true; PickAlbumsButton.IsEnabled = true; });
                    return;
                }
            }

            // If no recent users, try to prepopulate with "dragonspeed"
            if (_recentUsers.Count == 0)
            {
                try
                {
                    var tempService = new FlickrService(ApiKey, "");
                    string id = await tempService.GetUserIdFromName("dragonspeed");

                    var defaultUser = new FlickrUser { Name = "dragonspeed", Id = id };

                    _recentUsers.RemoveAll(u => u.Id == id);
                    _recentUsers.Insert(0, defaultUser);

                    // persist and update UI on UI thread
                    Properties.Settings.Default.RecentUsers = JsonSerializer.Serialize(_recentUsers);
                    Properties.Settings.Default.LastUserId = defaultUser.Id;
                    Properties.Settings.Default.Save();

                    Dispatcher.Invoke(() =>
                    {
                        UserComboBox.ItemsSource = null;
                        UserComboBox.ItemsSource = _recentUsers;
                        UserComboBox.SelectedItem = defaultUser;
                        StatusText.Text = $"Selected user: {defaultUser.Name}";
                        AllPhotosButton.IsEnabled = true;
                        PickAlbumsButton.IsEnabled = true;
                    });

                    _flickr = new FlickrService(ApiKey, defaultUser.Id);
                }
                catch (Exception ex)
                {
                    // non-fatal: show message and keep UI usable
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Default user lookup failed: {ex.Message}";
                    });
                }
            }
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                ResumeOverlay.Visibility = Visibility.Visible;
                ExitFullScreen();
                _slideshow.Pause();
            }
            else
            {
                ResumeOverlay.Visibility = Visibility.Collapsed;
                EnterFullScreen();
                _slideshow.Resume();
            }
        }

        private void StartShow()
        {
            if (_photos.Count == 0) return;

            _paused = false;
            ResumeOverlay.Visibility = Visibility.Collapsed;
            _slideshow.Start(_photos, _shuffle);
        }

        private void ShowNextImage()
        {
            _slideshow.Next();
        }

        private void ShowPreviousImage()
        {
            _slideshow.Previous();
        }

#if DEBUG
        private void UpdateDebugInfo(string url, BitmapImage? bitmap)
        {
            // Show both pixel dimensions and WPF device-independent sizes (Width/Height) plus DPI.
            string pixelText = "unknown";
            string dipText = "unknown";
            string dpiText = "unknown";

            try
            {
                if (bitmap != null)
                {
                    pixelText = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}";
                    dipText = $"{bitmap.Width}×{bitmap.Height}";
                    dpiText = $"DPI: {bitmap.DpiX}×{bitmap.DpiY}";
                }
            }
            catch { }

            string text = $"URL: {url}\nPixels: {pixelText}\nDIPs: {dipText}\n{dpiText}";
            Dispatcher.Invoke(() =>
            {
                DebugInfoText.Text = text;
                DebugInfoText.Visibility = Visibility.Visible;
            });
        }
#else
        private void UpdateDebugInfo(string url, BitmapImage? bitmap) { }
#endif

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

        private void UpdateAddUserButtonState(string? text)
        {
            var trimmed = text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                AddUserButton.IsEnabled = false;
                return;
            }

            bool exists = _recentUsers.Any(u =>
                string.Equals(u.Name?.Trim(), trimmed, StringComparison.CurrentCultureIgnoreCase));

            AddUserButton.IsEnabled = !exists;
        }

        private void UserComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb)
            {
                AddUserButton.IsEnabled = false;
                return;
            }

            UpdateAddUserButtonState(tb.Text);
        }

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            if (UserComboBox.SelectedItem is not FlickrUser user) return;
            _recentUsers.Remove(user);
            Properties.Settings.Default.RecentUsers = JsonSerializer.Serialize(_recentUsers);
            Properties.Settings.Default.LastUserId = "";
            Properties.Settings.Default.Save();
            UserComboBox.ItemsSource = null;
            UserComboBox.ItemsSource = _recentUsers;
            UserComboBox.Text = "";
            _flickr = null;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;
            RemoveUserButton.IsEnabled = false;
            UpdateAddUserButtonState(UserComboBox.Text);
            StatusText.Text = $"Removed user: {user.Name}";
        }

        private void DebugOutput_Click(object sender, RoutedEventArgs e)
        {
            _slideshow.DebugOutput = DebugOutputCheckBox.IsChecked == true;
            Properties.Settings.Default.DebugOutput = _slideshow.DebugOutput;
            Properties.Settings.Default.Save();
            DebugInfoText.Visibility = _slideshow.DebugOutput ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChangeSlideDuration(int delta)
        {
            _slideshow.SlideDurationSeconds = Math.Clamp(_slideshow.SlideDurationSeconds + delta, 15, 300);
            SlideDurationValue.Text = _slideshow.SlideDurationSeconds.ToString();
            Properties.Settings.Default.SlideDurationSeconds = _slideshow.SlideDurationSeconds;
            Properties.Settings.Default.Save();
        }

        private void SlideDurationDown_Click(object sender, RoutedEventArgs e) => ChangeSlideDuration(-5);
        private void SlideDurationUp_Click(object sender, RoutedEventArgs e) => ChangeSlideDuration(5);
        private void Quit_Click(object sender, RoutedEventArgs e) => Close();

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

                // newly-selected existing user -> disable Add button
                AddUserButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"User '{name}' not found: {ex.Message}";
            }
            finally
            {
                // evaluate textbox to set correct enabled state (re-enable only if non-empty & not matching)
                if (UserComboBox.Template.FindName("PART_EditableTextBox", UserComboBox) is TextBox finalTb)
                    UpdateAddUserButtonState(finalTb.Text);
                else
                    AddUserButton.IsEnabled = false;
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
                RemoveUserButton.IsEnabled = true;

                // selection matches an existing recent user -> disable Add button
                AddUserButton.IsEnabled = false;
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
            ExploreButton.IsEnabled = false;
            UserComboBox.IsEnabled = false;
            RemoveUserButton.IsEnabled = false;

            // Progress reporter
            var progress = new Progress<int>(count =>
            {
                StatusText.Text = $"Loading photos... {count}";
            });

            try
            {
                var cacheKey = $"stream_{((FlickrUser)UserComboBox.SelectedItem).Id}";
                var cached = PhotoCache.Load(cacheKey, TimeSpan.FromHours(24));
                _photos = cached ?? await _flickr.GetAllPublicPhotos(progress);
                if (_photos.Count == 0)
                {
                    StatusText.Text = "No photos found.";
                    return;
                }
                if (cached == null) PhotoCache.Save(cacheKey, _photos);
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
                UpdateAddUserButtonState(UserComboBox.Text);
                AllPhotosButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                PickAlbumsButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                RemoveUserButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                UserComboBox.IsEnabled = true;
                ExploreButton.IsEnabled = true;
            }
        }

        private async void Explore_Click(object sender, RoutedEventArgs e)
        {
            _photos.Clear();
            _shuffle = true;
            StatusText.Text = "Loading Explore photos...";
            AddUserButton.IsEnabled = false;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;
            ExploreButton.IsEnabled = false;
            UserComboBox.IsEnabled = false;
            RemoveUserButton.IsEnabled = false;

            try
            {
                // Flickr service WITHOUT user context
                var exploreService = new FlickrService(ApiKey, "");

                var cached = PhotoCache.Load("explore", TimeSpan.FromHours(1));
                _photos = cached ?? await exploreService.GetExplorePhotos(500);

                if (_photos.Count == 0)
                {
                    StatusText.Text = "No Explore photos found.";
                    return;
                }

                if (cached == null) PhotoCache.Save("explore", _photos);
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
                UpdateAddUserButtonState(UserComboBox.Text);
                AllPhotosButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                PickAlbumsButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                RemoveUserButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                UserComboBox.IsEnabled = true;
                ExploreButton.IsEnabled = true;
            }
        }

        private async void Albums_Click(object sender, RoutedEventArgs e)
        {
            if (_flickr == null) return;

            _shuffle = true;
            var user = UserComboBox.SelectedItem as FlickrUser;
            if (user == null) return;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;
            ExploreButton.IsEnabled = false;
            UserComboBox.IsEnabled = false;
            AddUserButton.IsEnabled = false;
            RemoveUserButton.IsEnabled = false;
            try
            {
                StatusText.Text = "Loading albums...";
                var albums = await _flickr.GetAlbums();
                albums.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                var collections = await _flickr.GetCollections();
                var picker = new AlbumPicker(albums, collections) { Owner = this };
                if (picker.ShowDialog() != true) return;

                StatusText.Text = "Loading album photos...";
                _photos.Clear();
                foreach (var album in picker.SelectedAlbums)
                {
                    var key = $"album_{user.Id}_{album.Id}";
                    var cached = PhotoCache.Load(key, TimeSpan.FromHours(24));
                    var photos = cached ?? await _flickr.GetAlbumPhotos(album.Id);
                    if (cached == null && photos.Count > 0) PhotoCache.Save(key, photos);
                    _photos.AddRange(photos);
                }
                if (_photos.Count == 0)
                {
                    StatusText.Text = "No photos found in the selected albums.";
                    return;
                }
                StatusText.Text = $"Loaded {_photos.Count} photos!";
                EnterFullScreen();
                StartShow();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading albums: {ex.Message}";
            }
            finally
            {
                AllPhotosButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                PickAlbumsButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                RemoveUserButton.IsEnabled = UserComboBox.SelectedItem is FlickrUser;
                UserComboBox.IsEnabled = true;
                ExploreButton.IsEnabled = true;
                UpdateAddUserButtonState(UserComboBox.Text);
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
            if (SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired) == 0)
                StatusText.Text = "Could not keep the display awake during the slideshow.";

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
            if (SetThreadExecutionState(EsContinuous) == 0)
                StatusText.Text = "Could not release the display-awake request.";
        }

        #endregion

        #region Keyboard

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;

            switch (e.Key)
            {
                case Key.Escape:
                    if (TopControls.Visibility == Visibility.Collapsed || _paused)
                    {
                        _slideshow.Stop();
                        _paused = false;
                        ResumeOverlay.Visibility = Visibility.Collapsed;
                        ExitFullScreen();
                    }
                    else
                    {
                        Close();
                    }
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (_slideshow.IsRunning || _paused)
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
    }
}
