using System;
using System.Threading;
using System.Threading.Tasks;
using FlickrSlideshow.Core;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media;

namespace FlickrSlideshow.Xbox2;

public sealed partial class SlideshowPage : Page
{
    private readonly AppState _state = AppState.Instance;
    private CancellationTokenSource? _cts;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(8);
    private System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SlideshowPage()
    {
        this.InitializeComponent();
        Window.Current.CoreWindow.KeyDown += OnKeyDown;
        StartCompositorKeepAlive();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); } catch { }
        try { Window.Current.CoreWindow.PointerCursor = null; } catch { }

        StartLoop();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        try { ApplicationView.GetForCurrentView().ExitFullScreenMode(); } catch { }
        try { Window.Current.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.Arrow, 0); } catch { }
    }

    // ── Loop ──────────────────────────────────────────────────────────

    private void StartLoop()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var photos = _state.Photos;
        if (_state.Shuffle) Shuffle(photos);
        _ = RunLoopAsync(photos, _cts.Token);
    }

    private async Task RunLoopAsync(System.Collections.Generic.List<FlickrPhoto> photos, CancellationToken token)
    {
        if (photos.Count == 0) return;

        int index = 0;

        // Show first image immediately before the delay loop
        await ShowPhotoAsync(photos[index], token);
        index = (index + 1) % photos.Count;

        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(SlideDuration, token); }
            catch (TaskCanceledException) { break; }

            if (token.IsCancellationRequested) break;

            await ShowPhotoAsync(photos[index], token);
            index = (index + 1) % photos.Count;
        }
    }

    private async Task ShowPhotoAsync(FlickrPhoto photo, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            byte[] bytes;
            try
            {
                bytes = await _http.GetByteArrayAsync(photo.Url, cts.Token);
            }
            catch (ObjectDisposedException)
            {
                // SSL connection was disposed — create a fresh HttpClient and retry once
                _http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                bytes = await _http.GetByteArrayAsync(photo.Url, cts.Token);
            }
            if (token.IsCancellationRequested) return;

            BitmapImage? bitmap = null;
            try
            {
                bitmap = new BitmapImage();
                using var ms = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                }
                ms.Seek(0);
                await bitmap.SetSourceAsync(ms);
            }
            catch (Exception ex)
            {
                App.Log("Image decode error: " + ex.Message);
                return;
            }

            if (token.IsCancellationRequested) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    SlideImage.Source = bitmap;
                    CaptionText.Text = photo.Title ?? "";
                    LoadingText.Visibility = Visibility.Collapsed;

                    var da = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(1.5) };
                    Storyboard.SetTarget(da, SlideImage);
                    Storyboard.SetTargetProperty(da, "Opacity");
                    var sb = new Storyboard();
                    sb.Children.Add(da);
                    sb.Begin();
                }
                catch (Exception ex) { App.Log("UI update error: " + ex.Message); }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log("ShowPhotoAsync error: " + ex.Message); }
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e) { }
    private void BackToSettings_Click(object sender, RoutedEventArgs e) => Frame.GoBack();
    private void QuitYes_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
    private void QuitNo_Click(object sender, RoutedEventArgs e) { }

    // ── Compositor keep-alive (perpetual Storyboard) ──────────────────

    private void StartCompositorKeepAlive()
    {
        var anim = new DoubleAnimation
        {
            From = 0.99, To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(800)),
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };
        Storyboard.SetTarget(anim, KeepAlive);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ── Gamepad ───────────────────────────────────────────────────────

    private void OnKeyDown(CoreWindow sender, KeyEventArgs e)
    {
        if (e.VirtualKey == Windows.System.VirtualKey.GamepadB ||
            e.VirtualKey == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }

    private static void Shuffle(System.Collections.Generic.List<FlickrPhoto> list)
    {
        var rng = new Random();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
