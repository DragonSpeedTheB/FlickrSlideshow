using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace FlickrSlideshow.Xbox
{
    public sealed partial class MainPage : Page
    {
        private readonly SlideshowViewModel _vm;
        private bool _paused;
        private bool _slideshowActive;
        private bool _quitPromptOpen;
        private List<AlbumPickerItem> _allAlbumItems = new List<AlbumPickerItem>();
        private static readonly CoreCursor _arrowCursor = new CoreCursor(CoreCursorType.Arrow, 0);

        public MainPage()
        {
            this.InitializeComponent();
            _vm = new SlideshowViewModel(Window.Current.CoreWindow.Dispatcher);
            _vm.ImageReady += OnImageReady;
            _vm.StatusChanged += msg => StatusText.Text = msg;
            _vm.CurrentUserChanged += OnCurrentUserChanged;

            UsernameBox.GotFocus += (s, e) => { try { InputPane.GetForCurrentView().TryShow(); } catch { } };
            AlbumFilterBox.GotFocus += (s, e) => { try { InputPane.GetForCurrentView().TryShow(); } catch { } };

            Window.Current.CoreWindow.KeyDown += OnCoreKeyDown;
            StartCompositorKeepAlive();
        }

        // === WPF-matching state transitions ===

        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused)
            {
                ResumeOverlay.Visibility = Visibility.Visible;
                ExitFullScreen();
                _vm.Pause();
            }
            else
            {
                ResumeOverlay.Visibility = Visibility.Collapsed;
                EnterFullScreen();
                _vm.Resume();
            }
        }

        private void EnterFullScreen()
        {
            TopControls.Visibility = Visibility.Collapsed;
            RootGrid.Background = new SolidColorBrush(Colors.Black);
            try { Window.Current.CoreWindow.PointerCursor = null; } catch { }
            try { ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); } catch { }
        }

        private void ExitFullScreen()
        {
            TopControls.Visibility = Visibility.Visible;
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(255, 211, 211, 211));
            try { Window.Current.CoreWindow.PointerCursor = _arrowCursor; } catch { }
            try { ApplicationView.GetForCurrentView().ExitFullScreenMode(); } catch { }
        }

        private void StartShow()
        {
            if (!_vm.HasPhotos) return;
            _slideshowActive = true;
            LoadingText.Visibility = Visibility.Collapsed;
            EnterFullScreen();
            _vm.StartSlideshow();
        }

        // === ComboBox / User management ===

        private void RefreshComboBox()
        {
            UserComboBox.ItemsSource = null;
            UserComboBox.ItemsSource = _vm.RecentUsers;
            if (_vm.CurrentUser != null)
            {
                var match = _vm.RecentUsers.FirstOrDefault(u => u.Id == _vm.CurrentUser.Id);
                if (match != null) UserComboBox.SelectedItem = match;
            }
            UpdateAddButtonState();
        }

        private void UpdateAddButtonState()
        {
            var text = (UsernameBox.Text ?? "").Trim();
            bool exists = _vm.RecentUsers.Any(u => string.Equals(u.Name != null ? u.Name.Trim() : "", text, StringComparison.OrdinalIgnoreCase));
            AddUserButton.IsEnabled = !string.IsNullOrWhiteSpace(text) && !exists;
        }

        private void OnCurrentUserChanged(FlickrUser user)
        {
            AllPhotosButton.IsEnabled = user != null;
            PickAlbumsButton.IsEnabled = user != null;
            RefreshComboBox();
        }

        private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserComboBox.SelectedItem is FlickrUser user)
            {
                _vm.SelectUser(user);
                AllPhotosButton.IsEnabled = true;
                PickAlbumsButton.IsEnabled = true;
            }
            UpdateAddButtonState();
        }

        private void UsernameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAddButtonState();

        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            var name = (UsernameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return;
            AddUserButton.IsEnabled = false;
            StatusText.Text = "Looking up user...";
            var ok = await _vm.AddOrSelectUserAsync(name);
            if (ok) UsernameBox.Text = "";
            else AddUserButton.IsEnabled = true;
        }

        // === Photo loading (matches WPF flow) ===

        private async void AllPhotos_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.CurrentUser == null) return;
            AllPhotosButton.IsEnabled = false;
            PickAlbumsButton.IsEnabled = false;
            _vm.Shuffle = true;
            StatusText.Text = "Loading photos... 0";
            await _vm.LoadAllPhotosAsync();
            AllPhotosButton.IsEnabled = true;
            PickAlbumsButton.IsEnabled = true;
            StartShow();
        }

        private async void Explore_Click(object sender, RoutedEventArgs e)
        {
            ExploreButton.IsEnabled = false;
            StatusText.Text = "Loading Explore photos...";
            await _vm.LoadExploreAsync();
            ExploreButton.IsEnabled = true;
            StartShow();
        }

        private async void PickAlbums_Click(object sender, RoutedEventArgs e)
        {
            PickAlbumsButton.IsEnabled = false;
            StatusText.Text = "Loading albums...";
            var albums = await _vm.LoadAlbumsAsync();
            PickAlbumsButton.IsEnabled = true;

            _allAlbumItems = new List<AlbumPickerItem>();
            foreach (var c in _vm.Collections.OrderBy(c => c.Title))
                _allAlbumItems.Add(new AlbumPickerItem { Id = c.Id, Title = c.Title, IsCollection = true });
            foreach (var a in albums)
                _allAlbumItems.Add(new AlbumPickerItem { Id = a.Id, Title = a.Title, IsCollection = false });
            RebuildAlbumList(_allAlbumItems);
            AlbumFilterBox.Text = "";
            AlbumPickerOverlay.Visibility = Visibility.Visible;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => AlbumList.Focus(FocusState.Programmatic));
        }

        private void RebuildAlbumList(List<AlbumPickerItem> items)
        {
            AlbumList.Items.Clear();
            foreach (var item in items)
                AlbumList.Items.Add(new ListBoxItem
                {
                    Content = (item.IsCollection ? "[C] " : "") + item.Title,
                    Tag = item,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 18
                });
        }

        private void AlbumFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = (AlbumFilterBox.Text ?? "").Trim().ToLower();
            RebuildAlbumList(string.IsNullOrEmpty(filter) ? _allAlbumItems
                : _allAlbumItems.Where(a => a.Title.ToLower().Contains(filter)).ToList());
        }

        private async void AlbumOk_Click(object sender, RoutedEventArgs e)
        {
            var albumIds = new List<string>();
            foreach (ListBoxItem item in AlbumList.SelectedItems)
            {
                if (item.Tag is AlbumPickerItem a)
                {
                    if (a.IsCollection)
                    {
                        var coll = _vm.Collections.FirstOrDefault(c => c.Id == a.Id);
                        if (coll != null)
                            foreach (var alb in coll.Albums)
                                if (!albumIds.Contains(alb.Id)) albumIds.Add(alb.Id);
                    }
                    else if (!albumIds.Contains(a.Id)) albumIds.Add(a.Id);
                }
            }
            if (albumIds.Count == 0) return;
            AlbumPickerOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "Loading album photos...";
            await _vm.LoadSelectedAlbumsAsync(albumIds);
            StartShow();
        }

        private void AlbumCancel_Click(object sender, RoutedEventArgs e)
        {
            AlbumPickerOverlay.Visibility = Visibility.Collapsed;
        }

        // === Resume button (matches WPF) ===

        private void ResumeButton_Click(object sender, RoutedEventArgs e) => SetPaused(false);

        // === Quit confirmation ===

        private void ShowQuitPrompt()
        {
            _quitPromptOpen = true;
            _vm.Pause();
            QuitOverlay.Visibility = Visibility.Visible;
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => QuitYesButton.Focus(FocusState.Programmatic));
        }

        private void DismissQuitPrompt()
        {
            _quitPromptOpen = false;
            QuitOverlay.Visibility = Visibility.Collapsed;
            _vm.Resume();
        }

        private void QuitYes_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
        private void QuitNo_Click(object sender, RoutedEventArgs e) => DismissQuitPrompt();

        // === Compositor keep-alive ===

        private void StartCompositorKeepAlive()
        {
            var animation = new DoubleAnimation
            {
                From = 0.99, To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };
            Storyboard.SetTarget(animation, CompositorKeepAlive);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(animation);
            sb.Begin();
        }

        // === Image display ===

        private void OnImageReady(BitmapImage bitmap, string title)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            SlideImage.Source = bitmap;
            CaptionText.Text = title ?? "";
            SlideImage.InvalidateMeasure();

            var da = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(2) };
            Storyboard.SetTarget(da, SlideImage);
            Storyboard.SetTargetProperty(da, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(da);
            sb.Begin();
        }

        // === Gamepad / keyboard (matches WPF key handling) ===

        private void OnCoreKeyDown(CoreWindow sender, KeyEventArgs e)
        {
            var key = e.VirtualKey;

            // Quit prompt: A=yes, B=no
            if (_quitPromptOpen)
            {
                if (key == VirtualKey.GamepadA) { QuitYes_Click(null, null); e.Handled = true; }
                else if (key == VirtualKey.GamepadB || key == VirtualKey.Escape) { DismissQuitPrompt(); e.Handled = true; }
                return;
            }

            // Album picker open: B cancels
            if (AlbumPickerOverlay.Visibility == Visibility.Visible)
            {
                if (key == VirtualKey.GamepadB || key == VirtualKey.Escape) { AlbumCancel_Click(null, null); e.Handled = true; }
                return;
            }

            // If slideshow is not active, controls are showing; don't intercept keys
            if (!_slideshowActive) return;

            switch (key)
            {
                // Escape / B: pause (shows controls), or quit prompt if already paused
                case VirtualKey.GamepadB:
                case VirtualKey.Escape:
                    if (_paused)
                        ShowQuitPrompt();
                    else
                        SetPaused(true);
                    e.Handled = true;
                    break;

                // Menu / View: toggle pause
                case VirtualKey.GamepadMenu:
                case VirtualKey.GamepadView:
                    SetPaused(!_paused);
                    e.Handled = true;
                    break;

                // Space / A: toggle pause
                case VirtualKey.GamepadA:
                case VirtualKey.Space:
                    SetPaused(!_paused);
                    e.Handled = true;
                    break;

                // Navigation
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.Right:
                    _vm.Next();
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.Left:
                    _vm.Previous();
                    e.Handled = true;
                    break;
            }
        }
    }
}