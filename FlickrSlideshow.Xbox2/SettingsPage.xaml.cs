using System;
using System.Linq;
using FlickrSlideshow.Core;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace FlickrSlideshow.Xbox2;

public sealed partial class SettingsPage : Page
{
    private readonly AppState _state = AppState.Instance;

    public SettingsPage()
    {
        this.InitializeComponent();
        Window.Current.CoreWindow.KeyDown += OnKeyDown;
        _state.UsersChanged += RefreshUserCombo;

        UsernameBox.GotFocus += (s, e) =>
        {
            try { InputPane.GetForCurrentView().TryShow(); } catch { }
        };
        UsernameBox.PointerPressed += (s, e) =>
        {
            UsernameBox.Focus(FocusState.Pointer);
            try { InputPane.GetForCurrentView().TryShow(); } catch { }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (string.IsNullOrEmpty(_state.ApiKey))
        {
            StatusText.Text = "Loading...";
            await _state.LoadApiKeyAsync();
            StatusText.Text = "";
        }

        await _state.LoadRecentUsersAsync();

        RefreshUserCombo();
        UpdateButtons();

        // Deferred focus to a button so D-pad works immediately (not captured by ComboBox)
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
        {
            if (_state.CurrentUser != null)
                AllPhotosButton.Focus(FocusState.Programmatic);
            else
                ExploreButton.Focus(FocusState.Programmatic);
        });
    }

    private void RefreshUserCombo()
    {
        UserComboBox.SelectionChanged -= UserComboBox_SelectionChanged;
        UserComboBox.ItemsSource = null;
        UserComboBox.ItemsSource = _state.RecentUsers;
        if (_state.CurrentUser != null)
        {
            var match = _state.RecentUsers.FirstOrDefault(u => u.Id == _state.CurrentUser.Id);
            if (match != null) UserComboBox.SelectedItem = match;
        }
        UserComboBox.SelectionChanged += UserComboBox_SelectionChanged;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasUser = _state.CurrentUser != null;
        AllPhotosButton.IsEnabled = hasUser;
        PickAlbumsButton.IsEnabled = hasUser;
        RemoveUserButton.IsEnabled = hasUser;
        AddUserButton.IsEnabled = !string.IsNullOrWhiteSpace(UsernameBox.Text)
            && !_state.RecentUsers.Any(u => string.Equals(u.Name, UsernameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserComboBox.SelectedItem is FlickrUser user)
            _state.SelectUser(user);
        UpdateButtons();
    }

    private void UsernameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtons();

    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        var name = UsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        AddUserButton.IsEnabled = false;
        StatusText.Text = $"Looking up {name}...";
        var ok = await _state.AddUserAsync(name);
        StatusText.Text = ok ? $"Selected: {name}" : "User not found.";
        if (ok)
        {
            UsernameBox.Text = "";
            _state.SaveRecentUsers();
        }
        UpdateButtons();
    }

    private void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (_state.CurrentUser == null) return;
        _state.RecentUsers.Remove(_state.CurrentUser);
        _state.SaveRecentUsers();
        RefreshUserCombo();
    }

    private async void AllPhotos_Click(object sender, RoutedEventArgs e)
    {
        SetLoading("Loading all photos...");
        _state.Shuffle = true;
        var status = await _state.LoadAllPhotosAsync(new Progress<int>(n => StatusText.Text = $"Loading photos... {n}"));
        if (_state.Photos.Count > 0)
            Frame.Navigate(typeof(SlideshowPage));
        else
            StatusText.Text = status;
    }

    private void PickAlbums_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(AlbumPickerPage));
    }

    private async void Explore_Click(object sender, RoutedEventArgs e)
    {
        SetLoading("Loading Explore...");
        var status = await _state.LoadExploreAsync();
        if (_state.Photos.Count > 0)
            Frame.Navigate(typeof(SlideshowPage));
        else
            StatusText.Text = status;
    }

    private void SetLoading(string msg)
    {
        StatusText.Text = msg;
        AllPhotosButton.IsEnabled = false;
        PickAlbumsButton.IsEnabled = false;
        ExploreButton.IsEnabled = false;
    }

    private void OnKeyDown(CoreWindow sender, KeyEventArgs e)
    {
        if (e.VirtualKey == Windows.System.VirtualKey.GamepadB ||
            e.VirtualKey == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ShowQuit();
        }
    }

    private void ShowQuit()
    {
        QuitOverlay.Visibility = Visibility.Visible;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            QuitYesButton.Focus(FocusState.Programmatic));
    }

    private void DismissQuit()
    {
        QuitOverlay.Visibility = Visibility.Collapsed;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            AllPhotosButton.Focus(FocusState.Programmatic));
    }

    private void QuitYes_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
    private void QuitNo_Click(object sender, RoutedEventArgs e) => DismissQuit();
    private void QuitButton_Click(object sender, RoutedEventArgs e) => ShowQuit();
}
