using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlickrSlideshow.Core;
using Windows.System.Display;
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
    private DisplayRequest? _displayRequest;
    private DispatcherTimer? _pointerHideTimer;

    private TimeSpan SlideDuration => TimeSpan.FromSeconds(_state.SlideDurationSeconds);
    public SlideshowPage()
    {
        this.InitializeComponent();
        Window.Current.CoreWindow.KeyDown += OnKeyDown;
        Window.Current.CoreWindow.PointerMoved += OnPointerMoved;
        StartCompositorKeepAlive();
        InitPointerHideTimer();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); } catch { }
        try { ApplicationView.GetForCurrentView().FullScreenSystemOverlayMode = FullScreenSystemOverlayMode.Minimal; } catch { }
        try { Window.Current.CoreWindow.PointerCursor = null; } catch { }

        // Prevent the screen from dimming during the slideshow
        try
        {
            _displayRequest = new DisplayRequest();
            _displayRequest.RequestActive();
        }
        catch { }

        StartLoop();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        _pointerHideTimer?.Stop();

        try { _displayRequest?.RequestRelease(); } catch { }
        _displayRequest = null;

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
        try
        {
            await RunLoopCoreAsync(photos, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log("RunLoopAsync fatal: " + ex);
            // Restart the loop after brief delay so a transient failure doesn't kill the slideshow
            try { await Task.Delay(2000, token); } catch { return; }
            if (!token.IsCancellationRequested) _ = RunLoopAsync(photos, token);
        }
    }

    private async Task RunLoopCoreAsync(System.Collections.Generic.List<FlickrPhoto> photos, CancellationToken token)
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
        int[] rateLimitWaitSeconds = { 30, 60, 120 };

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(30));

                byte[] bytes;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0");
                    http.DefaultRequestHeaders.Referrer = new Uri("https://www.flickr.com/photos/dragonspeed/favorites/");
                    var response = await http.GetAsync(photo.Url, cts.Token);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        if (attempt >= rateLimitWaitSeconds.Length)
                        {
                            App.Log("ShowPhotoAsync: rate limit retries exhausted, skipping photo.");
                            return;
                        }

                        int wait = rateLimitWaitSeconds[attempt];
                        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                            wait = Math.Max(wait, (int)delta.TotalSeconds);

                        App.Log($"ShowPhotoAsync: rate limited (429), waiting {wait}s before retry {attempt + 1}.");
                        await ShowRateLimitNoticeAsync(wait, photo.Url, token);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                }

                // Rate limit resolved — hide the banner if it was visible
                await HideRateLimitNoticeAsync();

                if (token.IsCancellationRequested) return;

                BitmapImage? bitmap = null;
                try
                {
                    bitmap = new BitmapImage();
                    var ms = new InMemoryRandomAccessStream();
                    using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
                    {
                        w.WriteBytes(bytes);
                        await w.StoreAsync();
                    }
                    ms.Seek(0);
                    await bitmap.SetSourceAsync(ms);
                    ms.Dispose(); // dispose only after SetSourceAsync completes
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

                return; // success
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { App.Log("ShowPhotoAsync error: " + ex.Message); return; }
        }
    }

    /// <summary>Shows the rate-limit banner and counts down, then hides it.</summary>
    private async Task ShowRateLimitNoticeAsync(int waitSeconds, string url, CancellationToken token)
    {
        bool debug = AppState.Instance.DebugOutput;

        for (int remaining = waitSeconds; remaining > 0; remaining--)
        {
            if (token.IsCancellationRequested) return;

            int snap = remaining;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RateLimitText.Text = debug
                    ? $"429 {url}\nAPI rate limited - trying again in {snap}s"
                    : $"⚠ Rate limited — retrying in {snap}s";
                RateLimitBanner.Visibility = Visibility.Visible;
            });

            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Hides the rate-limit banner.</summary>
    private async Task HideRateLimitNoticeAsync()
    {
        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            RateLimitBanner.Visibility = Visibility.Collapsed;
        });
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

    // ── Pointer auto-hide ──────────────────────────────────────────────────

    private void InitPointerHideTimer()
    {
        _pointerHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _pointerHideTimer.Tick += (_, _) =>
        {
            _pointerHideTimer.Stop();
            try { Window.Current.CoreWindow.PointerCursor = null; } catch { }
        };
    }

    private void OnPointerMoved(CoreWindow sender, PointerEventArgs e)
    {
        try { sender.PointerCursor ??= new CoreCursor(CoreCursorType.Arrow, 0); } catch { }
        _pointerHideTimer?.Stop();
        _pointerHideTimer?.Start();
    }

    // ── Gamepad ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

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
