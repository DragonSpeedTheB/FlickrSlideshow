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
using Microsoft.Extensions.Configuration;

namespace FlickrSlideshow
{
    public partial class MainWindow : Window
    {
        private string ApiKey;


        private FlickrService _flickr;
        private List<FlickrPhoto> _photos = new();
        private int _index;
        private bool _paused = false;

        // restore recent users list (was removed during refactor)
        private List<FlickrUser> _recentUsers = new();

        // slideshow helper
        private Slideshow _slideshow;

        private bool _shuffle = true;

        public MainWindow()
        {
            InitializeComponent();

            // show assembly version in initial window
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = asm?.GetName().Version;
            VersionText.Text = $"Version: {version?.ToString(3) ?? "unknown"}";

            // load API key from settings
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            string flickrApiKey = config["Flickr:ApiKey"];


#if DEBUG
            DebugInfoText.Visibility = Visibility.Visible;
#endif

            // create slideshow with UI callback
            _slideshow = new Slideshow(Dispatcher,
                onShow: async (bitmap, url, title) =>
                {
                    // update image source and start fade animation here
                    SlideImage.Source = bitmap;

                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2));
                    SlideImage.BeginAnimation(OpacityProperty, fadeIn);

                    // show title in bottom-left caption area
                    CaptionText.Text = title ?? "No Title";

                    await Task.CompletedTask;
                },
                onDebug: (url, bitmap) =>
                {
#if DEBUG
                    UpdateDebugInfo(url, bitmap);
#endif
                },
                httpClient: null // use default
            );

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
            int origW = 0, origH = 0;

            // try to read sizes from slideshow internal cache via _slideshow (not exposed),
            // fallback to the bitmap that was just displayed
            try
            {
                if (bitmap != null)
                {
                    origW = bitmap.PixelWidth;
                    origH = bitmap.PixelHeight;
                }
            }
            catch { origW = origH = 0; }

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

            // fetch collections so user can pick them as well
            var collections = await _flickr.GetCollections();

            var picker = new AlbumPicker(albums, collections);
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

        #endregion

        #region Keyboard

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;

            switch (e.Key)
            {
                case Key.Escape:
                    if (_photos.Count > 0 && _slideshow != null && _slideshow.IsRunning)
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
    }
}
