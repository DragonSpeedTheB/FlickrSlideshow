using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace FlickrSlideshow;

internal sealed class XboxStyleSlideshow
{
    private static readonly int[] RetrySeconds = { 30, 60, 120 };
    private readonly Func<BitmapImage, string, string?, Task> _onShow;
    private readonly Action<string?> _onNotice;
    private readonly Action<string> _onError;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private List<FlickrPhoto> _photos = new();
    private CancellationTokenSource? _cts;
    private int _index;
    private bool _paused;

    public XboxStyleSlideshow(Func<BitmapImage, string, string?, Task> onShow, Action<string?> onNotice, Action<string> onError)
    {
        _onShow = onShow;
        _onNotice = onNotice;
        _onError = onError;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.flickr.com/photos/dragonspeed/favorites/");
    }

    public int SlideDurationSeconds { get; set; } = 15;
    public bool DebugOutput { get; set; }
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public void Start(List<FlickrPhoto> photos, bool shuffle)
    {
        Stop();
        _photos = new List<FlickrPhoto>(photos);
        if (_photos.Count == 0) return;
        if (shuffle) Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_photos));
        _index = 0;
        _paused = false;
        StartLoop();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _onNotice(null);
    }

    public void Pause()
    {
        _paused = true;
        _cts?.Cancel();
        _cts = null;
        _onNotice(null);
    }

    public void Resume()
    {
        if (_photos.Count == 0 || !_paused) return;
        _cts?.Cancel();
        _paused = false;
        StartLoop();
    }

    public void Next() => Move(1);
    public void Previous() => Move(-1);

    private void Move(int offset)
    {
        if (_photos.Count == 0 || (!IsRunning && !_paused)) return;
        _index = (_index + offset + _photos.Count) % _photos.Count;
        _cts?.Cancel();
        _cts = null;
        StartLoop();
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await ShowAsync(_photos[_index], token);
                if (_paused) return;
                await Task.Delay(TimeSpan.FromSeconds(SlideDurationSeconds), token);
                _index = (_index + 1) % _photos.Count;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _cts = null;
            _onNotice(null);
            _onError($"Slideshow stopped: {ex.Message}");
        }
    }

    private async Task ShowAsync(FlickrPhoto photo, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await _http.GetAsync(photo.Url, timeout.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= RetrySeconds.Length)
                    {
                        _onNotice("Rate limit retries exhausted; skipping photo.");
                        return;
                    }
                    int wait = RetrySeconds[attempt];
                    if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                        wait = Math.Max(wait, (int)Math.Ceiling(delta.TotalSeconds));
                    for (int remaining = wait; remaining > 0; remaining--)
                    {
                        _onNotice(DebugOutput
                            ? $"429 {photo.Url}\nRate limited - retrying in {remaining}s"
                            : $"Rate limited - retrying in {remaining}s");
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                    }
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                token.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                _onNotice(null);
                await _onShow(bitmap, photo.Url, photo.Title);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _onNotice($"Could not load photo: {ex.Message}");
                return;
            }
        }
    }
}
