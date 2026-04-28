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
    private bool _paused;
    private bool _quitOpen;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(8);
    private readonly System.Net.Http.HttpClient _http = new();

    public SlideshowPage()
    {
        this.InitializeComponent();
        Window.Current.CoreWindow.KeyDown += OnKeyDown;
        StartCompositorKeepAlive();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _paused = false;
        PausedOverlay.Visibility = Visibility.Collapsed;
        PausedIndicator.Visibility = Visibility.Collapsed;

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

            while (_paused && !token.IsCancellationRequested)
                await Task.Delay(200, token).ConfigureAwait(false);

            if (token.IsCancellationRequested) break;

            await ShowPhotoAsync(photos[index], token);
            index = (index + 1) % photos.Count;
        }
    }

    private async Task ShowPhotoAsync(FlickrPhoto photo, CancellationToken token)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(photo.Url);
            if (token.IsCancellationRequested) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using var ms = new InMemoryRandomAccessStream();
                    using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
                    {
                        w.WriteBytes(bytes);
                        await w.StoreAsync();
                    }
                    ms.Seek(0);
                    await bitmap.SetSourceAsync(ms);

                    SlideImage.Source = bitmap;
                    CaptionText.Text = photo.Title ?? "";
                    LoadingText.Visibility = Visibility.Collapsed;

                    // Fade in
                    var da = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(1.5) };
                    Storyboard.SetTarget(da, SlideImage);
                    Storyboard.SetTargetProperty(da, "Opacity");
                    var sb = new Storyboard();
                    sb.Children.Add(da);
                    sb.Begin();
                }
                catch { }
            });
        }
        catch { }
    }

    // ── Pause / Resume ────────────────────────────────────────────────

    private void SetPaused(bool paused)
    {
        _paused = paused;
        PausedIndicator.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        PausedOverlay.Visibility = Visibility.Collapsed;
        SetPaused(false);
    }

    private void BackToSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame.GoBack();
    }

    // ── Quit prompt ───────────────────────────────────────────────────

    private void ShowQuit()
    {
        _quitOpen = true;
        _paused = true;
        QuitOverlay.Visibility = Visibility.Visible;
        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            QuitYesButton.Focus(FocusState.Programmatic));
    }

    private void DismissQuit()
    {
        _quitOpen = false;
        QuitOverlay.Visibility = Visibility.Collapsed;
        SetPaused(false);
    }

    private void QuitYes_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();
    private void QuitNo_Click(object sender, RoutedEventArgs e) => DismissQuit();

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
        if (_quitOpen)
        {
            if (e.VirtualKey == Windows.System.VirtualKey.GamepadA) { QuitYes_Click(null!, null!); e.Handled = true; }
            else if (e.VirtualKey == Windows.System.VirtualKey.GamepadB) { DismissQuit(); e.Handled = true; }
            return;
        }

        switch (e.VirtualKey)
        {
            case Windows.System.VirtualKey.GamepadMenu:
            case Windows.System.VirtualKey.GamepadView:
                // Toggle paused overlay (back to settings style)
                if (_paused)
                {
                    PausedOverlay.Visibility = Visibility.Collapsed;
                    SetPaused(false);
                }
                else
                {
                    SetPaused(true);
                    PausedOverlay.Visibility = Visibility.Visible;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                        ResumeButton.Focus(FocusState.Programmatic));
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.GamepadA:
            case Windows.System.VirtualKey.Space:
                SetPaused(!_paused);
                PausedOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.GamepadB:
            case Windows.System.VirtualKey.Escape:
                if (_paused) ShowQuit();
                else { SetPaused(true); PausedOverlay.Visibility = Visibility.Visible;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => ResumeButton.Focus(FocusState.Programmatic)); }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.GamepadDPadRight:
            case Windows.System.VirtualKey.Right:
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var nextPhotos = _state.Photos;
                _ = Task.Run(async () =>
                {
                    var i = (Array.IndexOf(nextPhotos.ToArray(), null) + 1) % nextPhotos.Count;
                    // just restart loop from next position
                });
                StartLoop();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.GamepadDPadLeft:
            case Windows.System.VirtualKey.Left:
                StartLoop();
                e.Handled = true;
                break;
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
