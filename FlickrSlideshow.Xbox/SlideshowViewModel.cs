using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlickrSlideshow.Core;
using Microsoft.Extensions.Configuration;

namespace FlickrSlideshow.Xbox
{
    /// <summary>
    /// Drives the Xbox slideshow: loads photos from Flickr, cycles images on a timer,
    /// handles next/previous and pause. Fires <see cref="ImageReady"/> on the UI thread.
    /// </summary>
    internal class SlideshowViewModel
    {
        public event Action<BitmapImage, string>? ImageReady;

        public bool IsPaused { get; private set; }

        private readonly HttpClient _http = new();
        private FlickrService? _flickr;
        private List<FlickrPhoto> _photos = new();
        private int _index;
        private CancellationTokenSource? _cts;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

        // Slide duration
        private static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(8);

        public SlideshowViewModel()
        {
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }

        public async Task InitializeAsync()
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false)
                    .Build();

                var apiKey = config["Flickr:ApiKey"] ?? throw new InvalidOperationException("Flickr:ApiKey not found in appsettings.json");
                var userId = config["Flickr:UserId"] ?? "";

                _flickr = new FlickrService(apiKey, userId);

                // If no UserId configured, fall back to explore photos
                if (string.IsNullOrWhiteSpace(userId))
                    _photos = await _flickr.GetExplorePhotos(500);
                else
                    _photos = await _flickr.GetAllPublicPhotos();

                if (_photos.Count == 0) return;

                Shuffle(_photos);
                StartLoop();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Xbox] InitializeAsync failed: {ex.Message}");
            }
        }

        public void Next()
        {
            _index = (_index + 1) % _photos.Count;
            _ = ShowCurrentAsync();
        }

        public void Previous()
        {
            _index = (_index - 1 + _photos.Count) % _photos.Count;
            _ = ShowCurrentAsync();
        }

        public void TogglePause()
        {
            IsPaused = !IsPaused;
        }

        // ── private ────────────────────────────────────────────────────────────

        private void StartLoop()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = RunLoopAsync(_cts.Token);
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!IsPaused)
                {
                    await ShowCurrentAsync();
                    _index = (_index + 1) % _photos.Count;
                }

                try { await Task.Delay(SlideDuration, token); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task ShowCurrentAsync()
        {
            if (_photos.Count == 0) return;
            var photo = _photos[_index];

            try
            {
                var bytes = await _http.GetByteArrayAsync(photo.Url);

                _dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        var bitmap = new BitmapImage();
                        bitmap.SetSource(ms.AsRandomAccessStream());
                        ImageReady?.Invoke(bitmap, photo.Title ?? "");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Xbox] Decode failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Xbox] Download failed: {ex.Message}");
            }
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
    }
}
