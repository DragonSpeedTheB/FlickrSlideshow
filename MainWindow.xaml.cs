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
using System.Windows.Media.Imaging;

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

        private List<FlickrUser> _recentUsers = new();

        public MainWindow()
        {
            InitializeComponent();

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


        private async void Albums_Click(object sender, RoutedEventArgs e)
        {
            if (_flickr == null) return;

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
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
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

            Shuffle(_photos);
            _index = 0;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            _ = RunSlideshow(_cts.Token);
        }

        private async Task RunSlideshow(CancellationToken token)
        {
            BitmapImage nextImage = await LoadBitmapAsync(_photos[_index].Url);

            while (!token.IsCancellationRequested)
            {
                while (_paused)
                    await Task.Delay(200, token);

                var currentImage = nextImage;
                _index = (_index + 1) % _photos.Count;
                nextImage = await LoadBitmapAsync(_photos[_index].Url);

                await ShowImage(currentImage, token);
            }
        }

        private async Task ShowImage(BitmapImage bitmap, CancellationToken token)
        {
            SlideImage.Source = bitmap;

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
            _ = LoadBitmapAsync(_photos[_index].Url).ContinueWith(t =>
            {
                Dispatcher.Invoke(() => SlideImage.Source = t.Result);
            });
        }

        private void ShowPreviousImage()
        {
            if (_photos.Count == 0) return;

            _index = (_index - 1 + _photos.Count) % _photos.Count;
            _ = LoadBitmapAsync(_photos[_index].Url).ContinueWith(t =>
            {
                Dispatcher.Invoke(() => SlideImage.Source = t.Result);
            });
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
            byte[] data;
            using (var client = new System.Net.Http.HttpClient())
                data = await client.GetByteArrayAsync(url);

            var bitmap = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(data))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            return bitmap;
        }

        #endregion

        #region Keyboard

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Don't intercept keys if the user is typing in a text box
            if (Keyboard.FocusedElement is TextBox) return;

            switch (e.Key)
            {
                case Key.Escape:
                    if (_cts != null && !_cts.IsCancellationRequested)
                    {
                        _cts.Cancel();
                        ExitFullScreen();
                    }
                    else
                    {
                        Close();
                    }
                    e.Handled = true;
                    break;

                case Key.Space:
                    _paused = !_paused;
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


        #endregion
    }
}
