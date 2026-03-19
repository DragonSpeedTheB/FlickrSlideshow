using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace FlickrSlideshow
{
    // Made public so it is accessible from other parts of the assembly/consumers.
    public class Slideshow
    {
        private readonly Dispatcher _dispatcher;
        // now pass title along with url
        private readonly Func<BitmapImage, string, string?, Task> _onShow;
        private readonly Action<string, BitmapImage?>? _onDebug;
        private readonly HttpClient _httpClient;

        private readonly Dictionary<string, BitmapImage> _imageCache = new();
        private readonly Dictionary<string, Task<BitmapImage>> _inflightLoads = new();
        // Cache raw bytes for prefetched images so we can decode on-demand with desired DecodePixel settings
        private readonly Dictionary<string, byte[]> _imageBytesCache = new();
        private readonly Dictionary<string, Task<byte[]>> _inflightByteLoads = new();
        private readonly Dictionary<string, (int Width, int Height)> _originalImageSizes = new();
        private readonly SemaphoreSlim _prefetchSemaphore = new(3);

        private List<FlickrPhoto> _photos = new();
        private int _index;
        private CancellationTokenSource? _cts;
        private bool _paused;
        private bool _shuffle;

        public Slideshow(Dispatcher dispatcher, Func<BitmapImage, string, string?, Task> onShow, Action<string, BitmapImage?>? onDebug = null, HttpClient? httpClient = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _onShow = onShow ?? throw new ArgumentNullException(nameof(onShow));
            _onDebug = onDebug;
            _httpClient = httpClient ?? new HttpClient();
        }

        // Expose running state so callers (MainWindow) can decide when to pause vs close.
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

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
            await InvokeShowAsync(nextImage, _photos[_index].Url, _photos[_index].Title).ConfigureAwait(false);

            // keep two images prefetched ahead
            EnsurePrefetch(_index);

            while (!token.IsCancellationRequested)
            {
                while (_paused)
                    await Task.Delay(200, token).ConfigureAwait(false);

                _index = (_index + 1) % _photos.Count;

                var currentIndex = (_index - 1 + _photos.Count) % _photos.Count;
                var url = _photos[_index].Url;
                var title = _photos[_index].Title;

                var image = await LoadBitmapAsync(url).ConfigureAwait(false);

                EnsurePrefetch(_index);

                // show (UI callback)
                await InvokeShowAsync(image, url, title).ConfigureAwait(false);

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
            var title = _photos[_index].Title;
            var image = await LoadBitmapAsync(url).ConfigureAwait(false);
            await InvokeShowAsync(image, url, title).ConfigureAwait(false);
            EnsurePrefetch(_index);
        }

        private async Task ShowPreviousImmediateAsync()
        {
            if (_photos.Count == 0) return;
            _index = (_index - 1 + _photos.Count) % _photos.Count;
            var url = _photos[_index].Url;
            var title = _photos[_index].Title;
            var image = await LoadBitmapAsync(url).ConfigureAwait(false);
            await InvokeShowAsync(image, url, title).ConfigureAwait(false);
            EnsurePrefetch(_index);
        }

        private async Task InvokeShowAsync(BitmapImage? bitmap, string url, string? title)
        {
            if (bitmap == null) return;
            // ensure callback runs on UI thread (caller expects to update UI)
            await _dispatcher.InvokeAsync(async () =>
            {
                _onDebug?.Invoke(url, bitmap);
                await _onShow(bitmap, url, title).ConfigureAwait(false);
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
                    if (_imageCache.ContainsKey(url) || _inflightLoads.ContainsKey(url) || _imageBytesCache.ContainsKey(url) || _inflightByteLoads.ContainsKey(url))
                        continue;

                    var task = PrefetchBytesAsync(url);
                    _inflightByteLoads[url] = task;

                    _ = task.ContinueWith(t =>
                    {
                        lock (_imageCache)
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                                _imageBytesCache[url] = t.Result;
                            _inflightByteLoads.Remove(url);
                        }
                    }, TaskScheduler.Default);
                }
            }
        }

        private async Task<byte[]> PrefetchBytesAsync(string url)
        {
            await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // Use GetByteArrayAsync to get the full bytes for later on-demand decode
                var bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                return bytes;
            }
            finally
            {
                _prefetchSemaphore.Release();
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
                {
                    // found cached decoded image
                }
                if (_inflightLoads.TryGetValue(url, out inflight))
                {
                    // inflight is set, do nothing here
                }
            }

            if (cached != null)
            {
                await ReportDebugAsync(url, cached).ConfigureAwait(false);
                return cached;
            }

            if (inflight != null)
                return await inflight.ConfigureAwait(false);

            async Task<BitmapImage> LoadInternal()
            {
                await _prefetchSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var stream = await _httpClient.GetStreamAsync(url).ConfigureAwait(false);
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;

                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            ms.Position = 0;

                            // Save raw bytes for potential reuse
                            var rawBytes = ms.ToArray();
                            lock (_imageBytesCache)
                            {
                                _imageBytesCache[url] = rawBytes;
                            }

                            // Read original pixel dimensions without forcing a full decode
                            int originalWidth = 0;
                            int originalHeight = 0;
                            try
                            {
                                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                                if (decoder.Frames.Count > 0)
                                {
                                    var frame = decoder.Frames[0];
                                    originalWidth = frame.PixelWidth;
                                    originalHeight = frame.PixelHeight;
                                }
                            }
                            catch
                            {
                                // Some images (unusual JPEGs/progressive/CMYK) can cause the OnDemand decoder to fail.
                                // Try again with OnLoad which is more robust for reading metadata.
                                try
                                {
                                    ms.Position = 0;
                                    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                    if (decoder.Frames.Count > 0)
                                    {
                                        var frame = decoder.Frames[0];
                                        originalWidth = frame.PixelWidth;
                                        originalHeight = frame.PixelHeight;
                                    }
                                }
                                catch
                                {
                                    // ignore and fall back to letting BitmapImage determine size
                                    originalWidth = 0;
                                    originalHeight = 0;
                                }
                            }

                            // reset stream for actual image decode
                            ms.Position = 0;

                            // If the original long side is larger than 1024, decode so the long side is 1024
                            if (originalWidth > 0 && originalHeight > 0)
                            {
                                if (Math.Max(originalWidth, originalHeight) > 1024)
                                {
                                    if (originalWidth >= originalHeight)
                                        bitmap.DecodePixelWidth = 1024;
                                    else
                                        bitmap.DecodePixelHeight = 1024;
                                }
                            }

                            bitmap.StreamSource = ms;
                            bitmap.EndInit();

                            // capture original pixel dimensions (use values from decoder when available)
                            int capturedWidth = originalWidth > 0 ? originalWidth : bitmap.PixelWidth;
                            int capturedHeight = originalHeight > 0 ? originalHeight : bitmap.PixelHeight;
                            lock (_originalImageSizes)
                            {
                                if (capturedWidth > 0 && capturedHeight > 0)
                                    _originalImageSizes[url] = (capturedWidth, capturedHeight);
                            }

                            bitmap.Freeze();

                            // If the decoded image still has a long side greater than 1024,
                            // produce a downscaled BitmapImage so the slideshow consistently
                            // uses images with a long side of 1024.
                            int decodedLongSide = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                            if (decodedLongSide > 1024)
                            {
                                double scale = 1024.0 / decodedLongSide;

                                // Create a scaled BitmapSource
                                var transformed = new TransformedBitmap(bitmap, new System.Windows.Media.ScaleTransform(scale, scale));
                                transformed.Freeze();

                                // Encode to PNG and re-create a BitmapImage so caller always receives a BitmapImage instance
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(transformed));
                                using var outMs2 = new MemoryStream();
                                encoder.Save(outMs2);
                                outMs2.Position = 0;

                                var scaled = new BitmapImage();
                                scaled.BeginInit();
                                scaled.CacheOption = BitmapCacheOption.OnLoad;
                                scaled.StreamSource = outMs2;
                                scaled.EndInit();
                                scaled.Freeze();

                                // Ensure returned BitmapImage uses 96 DPI so Width/Height equal pixels in DIPs
                                var finalScaled = await _dispatcher.InvokeAsync(() => ConvertTo96DpiBitmap(scaled));
                                await ReportDebugAsync(url, finalScaled).ConfigureAwait(false);
                                return finalScaled;
                            }

                            var final = await _dispatcher.InvokeAsync(() => ConvertTo96DpiBitmap(bitmap));
                            await ReportDebugAsync(url, final).ConfigureAwait(false);
                            return final;
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

        private async Task ReportDebugAsync(string url, BitmapImage? bitmap)
        {
            if (_onDebug == null) return;

            int origW = 0, origH = 0;
            lock (_originalImageSizes)
            {
                if (_originalImageSizes.TryGetValue(url, out var size))
                {
                    origW = size.Width;
                    origH = size.Height;
                }
            }

            // If we don't have original size, try to take from bitmap
            try
            {
                if (bitmap != null && (origW == 0 || origH == 0))
                {
                    origW = bitmap.PixelWidth;
                    origH = bitmap.PixelHeight;
                }
            }
            catch { }

            // Use dispatcher to call debug on UI thread
            await _dispatcher.InvokeAsync(() => _onDebug?.Invoke(url, bitmap));
        }

        private BitmapImage ConvertTo96DpiBitmap(BitmapSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // If already 96 DPI, and is a BitmapImage, return as-is
            if (Math.Abs(source.DpiX - 96.0) < 0.01 && Math.Abs(source.DpiY - 96.0) < 0.01 && source is BitmapImage bi)
                return bi;

            // Render the source into a new RenderTargetBitmap at 96 DPI
            int pxWidth = source.PixelWidth;
            int pxHeight = source.PixelHeight;

            var target = new RenderTargetBitmap(pxWidth, pxHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(source, new System.Windows.Rect(0, 0, pxWidth, pxHeight));
            }
            target.Render(dv);
            target.Freeze();

            // Encode to PNG and create a BitmapImage with CacheOption.OnLoad
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;

            var result = new BitmapImage();
            result.BeginInit();
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.StreamSource = ms;
            result.EndInit();
            result.Freeze();

            return result;
        }
    }
}