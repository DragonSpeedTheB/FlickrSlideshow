using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace FlickrSlideshow
{
    // Made public so it is accessible from other parts of the assembly/consumers.
    public class Slideshow
    {
        private readonly Dispatcher _dispatcher;
        private readonly Func<BitmapImage, string, Task> _onShow;
        private readonly Action<string, BitmapImage?>? _onDebug;
        private readonly HttpClient _httpClient;

        private readonly Dictionary<string, BitmapImage> _imageCache = new();
        private readonly Dictionary<string, Task<BitmapImage>> _inflightLoads = new();
        private readonly Dictionary<string, (int Width, int Height)> _originalImageSizes = new();
        private readonly SemaphoreSlim _prefetchSemaphore = new(3);

        private List<FlickrPhoto> _photos = new();
        private int _index;
        private CancellationTokenSource? _cts;
        private bool _paused;
        private bool _shuffle;

        public Slideshow(Dispatcher dispatcher, Func<BitmapImage, string, Task> onShow, Action<string, BitmapImage?>? onDebug = null, HttpClient? httpClient = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _onShow = onShow ?? throw new ArgumentNullException(nameof(onShow));
            _onDebug = onDebug;
            _httpClient = httpClient ?? new HttpClient();
        }

        public void Start(List<FlickrPhoto> photos, bool shuffle)
        {
            if (photos == null || photos.Count == 0) return;

            _photos = photos;
            _shuffle = shuffle;

            if (_shuffle) Shuffle(_photos);

            _index = 0;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = RunLoopAsync(_cts.Token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        public void Pause() => _paused = true;
        public void Resume() => _paused = false;

        public void Next() => _ = ShowNextImmediateAsync();
        public void Previous() => _ = ShowPreviousImmediateAsync();

        private async Task RunLoopAsync(CancellationToken token)
        {
            // initial image
            BitmapImage nextImage = await LoadBitmapAsync(_photos[_index].Url).ConfigureAwait(false);
            await InvokeShowAsync(nextImage, _photos[_index].Url).ConfigureAwait(false);

            // keep two images prefetched ahead
            EnsurePrefetch(_index);

            while (!token.IsCancellationRequested)
            {
                while (_paused)
                    await Task.Delay(200, token).ConfigureAwait(false);

                _index = (_index + 1) % _photos.Count;

                var currentIndex = (_index - 1 + _photos.Count) % _photos.Count;
                var url = _photos[_index].Url;

                var image = await LoadBitmapAsync(url).ConfigureAwait(false);

                EnsurePrefetch(_index);

                // show (UI callback)
                await InvokeShowAsync(image, url).ConfigureAwait(false);

                // wait between images (timing handled here; UI animations are still in MainWindow)
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task ShowNextImmediateAsync()
        {
            if (_photos.Count == 0) return;
            _index = (_index + 1) % _photos.Count;
            var url = _photos[_index].Url;
            var image = await LoadBitmapAsync(url).ConfigureAwait(false);
            await InvokeShowAsync(image, url).ConfigureAwait(false);
            EnsurePrefetch(_index);
        }

        private async Task ShowPreviousImmediateAsync()
        {
            if (_photos.Count == 0) return;
            _index = (_index - 1 + _photos.Count) % _photos.Count;
            var url = _photos[_index].Url;
            var image = await LoadBitmapAsync(url).ConfigureAwait(false);
            await InvokeShowAsync(image, url).ConfigureAwait(false);
            EnsurePrefetch(_index);
        }

        private async Task InvokeShowAsync(BitmapImage? bitmap, string url)
        {
            if (bitmap == null) return;
            // ensure callback runs on UI thread (caller expects to update UI)
            await _dispatcher.InvokeAsync(async () =>
            {
                _onDebug?.Invoke(url, bitmap);
                await _onShow(bitmap, url).ConfigureAwait(false);
            });
        }

        private void EnsurePrefetch(int currentIndex)
        {
            if (_photos.Count == 0) return;

            for (int i = 0; i <= 2; i++)
            {
                int idx = (currentIndex + i) % _photos.Count;
                string url = _photos[idx].Url;

                lock (_imageCache)
                {
                    if (_imageCache.ContainsKey(url) || _inflightLoads.ContainsKey(url))
                        continue;

                    var task = LoadBitmapAsync(url);
                    _inflightLoads[url] = task;

                    _ = task.ContinueWith(t =>
                    {
                        lock (_imageCache)
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                                _imageCache[url] = t.Result;
                            _inflightLoads.Remove(url);
                        }
                    }, TaskScheduler.Default);
                }
            }
        }

        private async Task<BitmapImage> LoadBitmapAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));

            Task<BitmapImage>? inflight = null;
            BitmapImage? cached = null;

            lock (_imageCache)
            {
                if (_imageCache.TryGetValue(url, out cached))
                    return cached;
                if (_inflightLoads.TryGetValue(url, out inflight))
                {
                    // inflight is set, do nothing here
                }
            }

            if (inflight != null)
                return await inflight.ConfigureAwait(false);

            async Task<BitmapImage> LoadInternal()
            {
                await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var stream = await _httpClient.GetStreamAsync(url).ConfigureAwait(false);
                    return await Task.Run(() =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;

                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            ms.Position = 0;

                            bitmap.StreamSource = ms;
                            bitmap.EndInit();

                            // capture original pixel dimensions
                            int originalWidth = bitmap.PixelWidth;
                            int originalHeight = bitmap.PixelHeight;
                            lock (_originalImageSizes)
                            {
                                if (originalWidth > 0 && originalHeight > 0)
                                    _originalImageSizes[url] = (originalWidth, originalHeight);
                            }

                            bitmap.Freeze();
                            return bitmap;
                        }
                        catch
                        {
                            // Instead of returning null, throw an exception to avoid CS8603
                            throw new InvalidOperationException("Failed to load bitmap image.");
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _prefetchSemaphore.Release();
                }
            }

            Task<BitmapImage> loadTask;
            lock (_imageCache)
            {
                if (_inflightLoads.TryGetValue(url, out var existing))
                    loadTask = existing;
                else
                {
                    loadTask = LoadInternal();
                    _inflightLoads[url] = loadTask;
                }
            }

            var result = await loadTask.ConfigureAwait(false);

            lock (_imageCache)
            {
                if (result != null)
                    _imageCache[url] = result;
                _inflightLoads.Remove(url);
            }

            return result;
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