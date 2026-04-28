using System;
using System.Collections.Generic;
using System.Linq;
using FlickrSlideshow.Core;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace FlickrSlideshow.Xbox2;

public sealed partial class AlbumPickerPage : Page
{
    private readonly AppState _state = AppState.Instance;
    private List<AlbumItem> _allItems = new();
    private List<FlickrCollection> _collections = new();

    public AlbumPickerPage()
    {
        this.InitializeComponent();
        Window.Current.CoreWindow.KeyDown += OnKeyDown;
        FilterBox.PointerPressed += (s, e) =>
        {
            FilterBox.Focus(FocusState.Pointer);
            try { InputPane.GetForCurrentView().TryShow(); } catch { }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AlbumList.Items.Clear();
        AlbumList.Items.Add(new ListBoxItem { Content = "Loading albums...", IsEnabled = false });

        var (albums, collections) = await _state.LoadAlbumsAsync();
        _collections = collections;

        _allItems = new();
        foreach (var c in collections)
            _allItems.Add(new AlbumItem { Id = c.Id, Title = $"[Collection] {c.Title}", IsCollection = true });
        foreach (var a in albums)
            _allItems.Add(new AlbumItem { Id = a.Id, Title = a.Title, IsCollection = false });

        RebuildList(_allItems);

        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
        {
            try { InputPane.GetForCurrentView().TryHide(); } catch { }
            AlbumList.Focus(FocusState.Programmatic);
        });
    }

    private void RebuildList(List<AlbumItem> items)
    {
        AlbumList.Items.Clear();
        foreach (var item in items)
            AlbumList.Items.Add(new ListBoxItem { Content = item.Title, Tag = item, FontSize = 16 });
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim().ToLower();
        RebuildList(string.IsNullOrEmpty(filter)
            ? _allItems
            : _allItems.Where(a => a.Title.ToLower().Contains(filter)).ToList());
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var albumIds = new List<string>();
        foreach (ListBoxItem item in AlbumList.SelectedItems)
        {
            if (item.Tag is AlbumItem a)
            {
                if (a.IsCollection)
                {
                    var coll = _collections.FirstOrDefault(c => c.Id == a.Id);
                    if (coll != null)
                        foreach (var alb in coll.Albums)
                            if (!albumIds.Contains(alb.Id)) albumIds.Add(alb.Id);
                }
                else if (!albumIds.Contains(a.Id)) albumIds.Add(a.Id);
            }
        }
        if (albumIds.Count == 0) return;

        var settingsPage = Frame.BackStack.Count > 0 ? null : (object?)null;
        // Show status on settings page by updating AppState, then navigate back to slideshow
        await _state.LoadAlbumPhotosAsync(albumIds);
        if (_state.Photos.Count > 0)
        {
            // Clear back-stack entry for AlbumPickerPage, go straight to slideshow
            Frame.Navigate(typeof(SlideshowPage));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Frame.GoBack();

    private void OnKeyDown(CoreWindow sender, KeyEventArgs e)
    {
        if (e.VirtualKey == Windows.System.VirtualKey.GamepadB ||
            e.VirtualKey == Windows.System.VirtualKey.Escape)
        {
            Frame.GoBack();
            e.Handled = true;
        }
    }

    private record AlbumItem
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public bool IsCollection { get; init; }
    }
}
