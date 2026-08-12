using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FlickrSlideshow.Core;

namespace FlickrSlideshow.XboxTest;

public partial class MainWindow : Window
{
    private const string EdgeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36 Edg/151.0.0.0";

    private HttpClient _http;
    private readonly Stopwatch  _rateLimitWatch = new();
    private readonly DispatcherTimer _uiTimer;
    private CancellationTokenSource _cts = new();
    private int _successCount = 0;
    private string _last429Dump = string.Empty;
    private static readonly Random _rng = new();

    public MainWindow()
    {
        InitializeComponent();

        _http = CreateHttpClient();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) =>
        {
            if (_rateLimitWatch.IsRunning)
                TimerText.Text = $"⏱  Rate-limited — elapsed: {_rateLimitWatch.Elapsed:mm\\:ss\\.ff}";
        };

        Closed += (_, _) => _cts.Cancel();
    }

    // -- Start button -------------------------------------------------------------------

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _successCount = 0;
        lblGoodCount.Content = "Good: 0";

        _cts.Cancel();
        _cts = new CancellationTokenSource();

        StartButton.IsEnabled = false;
        LogText.Text = "";
        TimerText.Visibility = Visibility.Collapsed;
        OkButton.Visibility  = Visibility.Collapsed;
        _rateLimitWatch.Reset();
        _uiTimer.Stop();

        try { await RunAsync(_cts.Token); }
        finally { StartButton.IsEnabled = true; }
    }

    // ── Main probe loop ──────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken token)
    {
        string apiKey = LoadApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            Log("ERROR: Could not read API key from appsettings.json");
            return;
        }

        string username = UsernameBox.Text.Trim();
        List<FlickrPhoto> photos;

        if (string.IsNullOrEmpty(username))
        {
            var svc = new FlickrService(apiKey, "");
            Log("Loading Explore photos to build probe URL list...");
            try { photos = await svc.GetExplorePhotos(50); }
            catch (Exception ex) { Log($"ERROR loading photos: {ex.Message}"); return; }
        }
        else
        {
            Log($"Looking up user '{username}'...");
            var lookup = new FlickrService(apiKey, "");
            string userId;
            try { userId = await lookup.GetUserIdFromName(username); }
            catch (Exception ex) { Log($"User not found: {ex.Message}"); return; }

            Log($"User ID: {userId}. Loading all public photos...");
            var userSvc = new FlickrService(apiKey, userId);
            var progress = new Progress<int>(n => Log($"  ...{n} photos indexed"));
            try { photos = await userSvc.GetAllPublicPhotos(progress); }
            catch (Exception ex) { Log($"ERROR loading photos: {ex.Message}"); return; }
        }

        if (photos.Count == 0)
        {
            Log("No photos returned — cannot probe.");
            return;
        }

        Log($"Got {photos.Count} photo URLs.  Starting probe (GET + ReadAsByteArrayAsync, identical to Xbox)...");
        Log("─────────────────────────────────────────────");

        int photoIndex   = 0;
        int successCount  = 0;
        bool everGot429   = false;

        while (!token.IsCancellationRequested)
        {
            string rawUrl = photos[photoIndex % photos.Count].Url;
            // Rewrite _o/_k/_h to _b so CloudFront serves from edge cache, not origin
            //string url = DowngradeToB(rawUrl);
            var url = rawUrl;
            photoIndex++;

            HttpStatusCode status;
            try
            {
                // Mirror Xbox SlideshowPage exactly: GetAsync then ReadAsByteArrayAsync
                using var resp = await _http.GetAsync(url, token);
                status = resp.StatusCode;

                if (status != HttpStatusCode.TooManyRequests)
                {
                    _ = await resp.Content.ReadAsByteArrayAsync(token);
                    _successCount++;
                    Dispatcher.Invoke(() => lblGoodCount.Content = "Good: " + _successCount);
                }
                else
                {
                    // Capture full 429 details while resp is still in scope
                    var sb429 = new System.Text.StringBuilder();
                    sb429.AppendLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    sb429.AppendLine($"URL: {url}");
                    sb429.AppendLine();
                    sb429.AppendLine("=== Response Headers ===");
                    foreach (var kv in resp.Headers)
                        sb429.AppendLine($"{kv.Key}: {string.Join(", ", kv.Value)}");
                    sb429.AppendLine();
                    sb429.AppendLine("=== Content Headers ===");
                    foreach (var kv in resp.Content.Headers)
                        sb429.AppendLine($"{kv.Key}: {string.Join(", ", kv.Value)}");
                    sb429.AppendLine();
                    sb429.AppendLine("=== Body ===");
                    var body429 = await resp.Content.ReadAsStringAsync(token);
                    sb429.Append(body429.Length > 4000 ? body429[..4000] + "..." : body429);
                    _last429Dump = sb429.ToString();
                    Dispatcher.Invoke(() => Show429Button.Visibility = Visibility.Visible);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Request error: {ex.Message}");
                await Delay(1000, token);
                continue;
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                if (!everGot429)
                {
                    everGot429 = true;
                    _rateLimitWatch.Restart();
                    _uiTimer.Start();
                    TimerText.Visibility = Visibility.Visible;
                    Log("⛔  429 received — stopwatch started.");
                    RecycleHttpClient();
                }
                else
                {
                    Log($"    still 429 — elapsed {_rateLimitWatch.Elapsed:mm\\:ss\\.ff}");
                }

                // Probe again after 1 s so we detect the lift promptly
                await Delay(20000, token);
                continue;
            }

            // Any non-429 response
            if (everGot429)
            {
                // Rate limit has lifted — record the elapsed time and stop
                _rateLimitWatch.Stop();
                _uiTimer.Stop();

                string elapsed = _rateLimitWatch.Elapsed.ToString(@"mm\:ss\.ff");
                TimerText.Text = $"✅  Rate limit lifted after  {elapsed}";
                Log($"✅  First successful response after rate-limit — total duration: {elapsed}");
                Log($"    Status: {(int)status} {status}");
                Log("─────────────────────────────────────────────");
                Log("Click OK to exit.");
                OkButton.Visibility = Visibility.Visible;
                OkButton.Focus();
                return;   // Stop probing — wait for user
            }

            // Normal (no 429 yet) — log and continue after the same pause the real app uses
            successCount++;
            Log($"OK #{successCount}  {(int)status}  {url[^Math.Min(60, url.Length)..]}");
            // 20s base + ±3s jitter so the interval doesn't look metronomic
            int delay = 20_000 + _rng.Next(-3_000, 3_001);
            await Delay(delay, token);
        }
    }

	private HttpClient CreateHttpClient()
	{
		// HttpClientHandler with automatic decompression to match browser accept-encoding
		var handler = new HttpClientHandler
		{
			AutomaticDecompression = System.Net.DecompressionMethods.All
		};
		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

		// Match the exact headers captured from Edge 151 fetching a staticflickr.com image
		client.DefaultRequestHeaders.UserAgent.ParseAdd(EdgeUserAgent);
		client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
		{
			NoCache = true
		};
		client.DefaultRequestHeaders.Add("pragma", "no-cache");
		client.DefaultRequestHeaders.Add("dnt", "1");
		client.DefaultRequestHeaders.Add("priority", "i");
		client.DefaultRequestHeaders.Referrer = new Uri("https://www.flickr.com/");
		client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not=A?Brand\";v=\"99\", \"Microsoft Edge\";v=\"151\", \"Chromium\";v=\"151\"");
		client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
		client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
		client.DefaultRequestHeaders.Add("sec-fetch-dest", "image");
		client.DefaultRequestHeaders.Add("sec-fetch-mode", "no-cors");
		client.DefaultRequestHeaders.Add("sec-fetch-site", "cross-site");
		client.DefaultRequestHeaders.Add("sec-fetch-storage-access", "active");
		return client;
	}

	/// <summary>Rewrites _o, _k, _h suffixes to _b so requests are served from
	/// CloudFront edge cache rather than hitting Flickr origin directly.</summary>
	private static string DowngradeToB(string url)
	{
		foreach (var suffix in new[] { "_o.", "_k.", "_h." })
		{
			int i = url.LastIndexOf(suffix, StringComparison.Ordinal);
			if (i >= 0)
				return url[..i] + "_b." + url[(i + suffix.Length)..];
		}
		return url; // already _b, _z, _l etc.
	}

	private void RecycleHttpClient()
	{
		var old = _http;
		_http = CreateHttpClient();
		try { old.Dispose(); } catch { }
		Log("♻  HttpClient recycled — fresh TCP connection for next probe.");
	}

	private void Show429Button_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrEmpty(_last429Dump)) return;
		var win = new Window
		{
			Title = "Last 429 Response",
			Width = 720, Height = 520,
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.CenterOwner
		};
		var tb = new System.Windows.Controls.TextBox
		{
			Text = _last429Dump,
			IsReadOnly = true,
			TextWrapping = System.Windows.TextWrapping.Wrap,
			VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
			FontFamily = new System.Windows.Media.FontFamily("Consolas"),
			FontSize = 12,
			Margin = new System.Windows.Thickness(8)
		};
		win.Content = tb;
		win.ShowDialog();
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Text += message + "\n";
            LogScroller.ScrollToBottom();
        });
    }

    private static Task Delay(int ms, CancellationToken token) =>
        Task.Delay(ms, token).ContinueWith(_ => { }, TaskContinuationOptions.None);

    private static string LoadApiKey()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("Flickr").GetProperty("ApiKey").GetString() ?? "";
        }
        catch { return ""; }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
