using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FlickrSlideshow
{
    public class FlickrService
    {
        private readonly string _apiKey;
        private readonly string _userId;
        private readonly HttpClient _http = new();

        public FlickrService(string apiKey, string userId)
        {
            _apiKey = apiKey;
            _userId = userId;
        }

        /// <summary>
        /// Get list of albums for the user.
        /// </summary>
        public async Task<List<FlickrAlbum>> GetAlbums()
        {
            var url =
                $"https://api.flickr.com/services/rest/?" +
                $"method=flickr.photosets.getList" +
                $"&api_key={_apiKey}" +
                $"&user_id={_userId}" +
                $"&format=json&nojsoncallback=1";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var sets = doc.RootElement
                .GetProperty("photosets")
                .GetProperty("photoset");

            var result = new List<FlickrAlbum>();

            foreach (var s in sets.EnumerateArray())
            {
                result.Add(new FlickrAlbum(
                    s.GetProperty("id").GetString()!,
                    s.GetProperty("title").GetProperty("_content").GetString()!
                ));
            }

            return result;
        }

        /// <summary>
        /// Get all public photos from the user photostream.
        /// </summary>
        public async Task<List<FlickrPhoto>> GetAllPublicPhotos(IProgress<int>? progress = null)
        {
            return await LoadPhotosFromPeople(progress);
        }


        /// <summary>
        /// Get all photos from a single album.
        /// </summary>
        public async Task<List<FlickrPhoto>> GetAlbumPhotos(string albumId)
        {
            return await LoadPhotosFromAlbum(albumId);
        }

        #region Private helpers

        private async Task<List<FlickrPhoto>> LoadPhotosFromPeople(IProgress<int>? progress = null)
        {
            var photos = new List<FlickrPhoto>();
            int page = 1;
            int pages;

            do
            {
                var url =
                    $"https://api.flickr.com/services/rest/?" +
                    $"method=flickr.people.getPublicPhotos" +
                    $"&api_key={_apiKey}" +
                    $"&user_id={_userId}" +
                    $"&per_page=500" +
                    $"&page={page}" +
                    $"&format=json&nojsoncallback=1";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var photosNode = doc.RootElement.GetProperty("photos");
                pages = photosNode.GetProperty("pages").GetInt32();

                foreach (var p in photosNode.GetProperty("photo").EnumerateArray())
                {
                    var id = p.GetProperty("id").GetString();
                    var server = p.GetProperty("server").GetString();
                    var secret = p.GetProperty("secret").GetString();

                    var urlImg = $"https://live.staticflickr.com/{server}/{id}_{secret}_b.jpg";
                    photos.Add(new FlickrPhoto(urlImg));

                    progress?.Report(photos.Count); // <-- report progress after each photo
                }

                page++;
            } while (page <= pages);

            return photos;
        }


        private async Task<List<FlickrPhoto>> LoadPhotosFromAlbum(string albumId)
        {
            var photos = new List<FlickrPhoto>();
            int page = 1;
            int pages = 1;

            do
            {
                var url =
                    $"https://api.flickr.com/services/rest/?" +
                    $"method=flickr.photosets.getPhotos" +
                    $"&api_key={_apiKey}" +
                    $"&photoset_id={albumId}" +
                    $"&user_id={_userId}" +
                    $"&per_page=500" +
                    $"&page={page}" +
                    $"&format=json&nojsoncallback=1";

                var json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("photoset", out var photosetNode))
                {
                    // Album missing or private, skip
                    break;
                }

                pages = photosetNode.TryGetProperty("pages", out var pagesElement) ? pagesElement.GetInt32() : 1;

                if (photosetNode.TryGetProperty("photo", out var photoArray))
                {
                    foreach (var p in photoArray.EnumerateArray())
                    {
                        var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        var server = p.TryGetProperty("server", out var serverEl) ? serverEl.GetString() : null;
                        var secret = p.TryGetProperty("secret", out var secretEl) ? secretEl.GetString() : null;

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(secret))
                        {
                            var urlImg = $"https://live.staticflickr.com/{server}/{id}_{secret}_b.jpg";
                            photos.Add(new FlickrPhoto(urlImg));
                        }
                    }
                }

                page++;
            } while (page <= pages);

            return photos;
        }
        public async Task<string> GetUserIdFromName(string username)
        {
            var url = $"https://api.flickr.com/services/rest/?" +
                      $"method=flickr.people.findByUsername" +
                      $"&api_key={_apiKey}" +
                      $"&username={Uri.EscapeDataString(username)}" +
                      "&format=json&nojsoncallback=1";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("stat").GetString() != "ok")
            {
                var msg = doc.RootElement.GetProperty("message").GetString();
                throw new Exception($"Flickr API error: {msg}");
            }

            return doc.RootElement.GetProperty("user").GetProperty("nsid").GetString()!;
        }



        #endregion
    }
}
