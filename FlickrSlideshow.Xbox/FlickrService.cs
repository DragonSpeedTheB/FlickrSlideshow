using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Linq;using System.Threading.Tasks;

namespace FlickrSlideshow.Xbox
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
                    GetFlexibleTitle(s, "title")
                ));
            }

            return result;
        }

        /// <summary>
        /// Get top-level collections (and the albums contained in them).
        /// </summary>
        public async Task<List<FlickrCollection>> GetCollections()
        {
            var url =
                $"https://api.flickr.com/services/rest/?" +
                $"method=flickr.collections.getTree" +
                $"&api_key={_apiKey}" +
                $"&user_id={_userId}" +
                $"&format=json&nojsoncallback=1";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("collections", out var root))
                return new List<FlickrCollection>();

            var result = new List<FlickrCollection>();

            if (root.TryGetProperty("collection", out var collArray))
            {
                foreach (var c in collArray.EnumerateArray())
                {
                    var colId = c.GetProperty("id").GetString() ?? "";
                    var colTitle = GetStringPropertyFlexible(c, "title");

                    var albums = new List<FlickrAlbum>();

                    if (c.TryGetProperty("set", out var setsNode))
                    {
                        foreach (var s in setsNode.EnumerateArray())
                        {
                            var setId = s.GetProperty("id").GetString() ?? "";
                            var setTitle = GetStringPropertyFlexible(s, "title");
                            albums.Add(new FlickrAlbum(setId, setTitle));
                        }
                    }

                    result.Add(new FlickrCollection(colId, colTitle, albums));
                }
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

        // Update LoadPhotosFromPeople to prefer sizes extras and fallback to sizes API when needed
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
                    // request size URLs so we can pick the largest available
                    $"&extras=url_o,url_k,url_h,url_b,url_l" +
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

                    // Prefer the largest available URL from extras
                    string? imageUrl =
                        TryGet(p, "url_o") ??
                        TryGet(p, "url_k") ??
                        TryGet(p, "url_h") ??
                        TryGet(p, "url_b") ??
                        TryGet(p, "url_l");

                    // If extras didn't include any or you want to ensure the absolute largest,
                    // call the sizes API for this photo (more network work).
                    if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(id))
                    {
                        var largest = await GetLargestPhotoUrlAsync(id);
                        if (!string.IsNullOrEmpty(largest))
                            imageUrl = largest;
                    }

                    // final fallback to constructed _b URL
                    if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
                        imageUrl = $"https://live.staticflickr.com/{server}/{id}_{secret}_b.jpg";

                    var title = TryGet(p, "title") ?? "";

                    if (!string.IsNullOrEmpty(imageUrl))
                        photos.Add(new FlickrPhoto(imageUrl, title));

                    progress?.Report(photos.Count);
                }

                page++;
            } while (page <= pages);

            return photos;
        }
        public async Task<List<FlickrPhoto>> GetExplorePhotos(int perPage)
        {
            var date = DateTime.Now.AddDays(new Random().Next(-60, -1));
            var parameters = new Dictionary<string, string>
            {
                ["method"] = "flickr.interestingness.getList",
                ["per_page"] = perPage.ToString(),
                ["extras"] = "url_h,url_l,url_o",
                ["date"] = date.ToString("yyyy-MM-dd")
            };

            return await CallPhotos(parameters);
        }
        private async Task<List<FlickrPhoto>> CallPhotos(Dictionary<string, string> parameters)
        {
            var photos = new List<FlickrPhoto>();

            parameters["api_key"] = _apiKey;
            parameters["format"] = "json";
            parameters["nojsoncallback"] = "1";
            parameters["per_page"] = parameters.TryGetValue("per_page", out var pp) ? pp : "500";

            int page = 1;
            int pages = 1;

            do
            {
                parameters["page"] = page.ToString();

                var query = string.Join("&",
                    parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

                var requestUrl = $"https://api.flickr.com/services/rest/?{query}";
                var json = await _http.GetStringAsync(requestUrl);

                using var doc = JsonDocument.Parse(json);

                var photosNode = doc.RootElement.GetProperty("photos");
                pages = photosNode.GetProperty("pages").GetInt32();

                foreach (var p in photosNode.GetProperty("photo").EnumerateArray())
                {
                    string? imageUrl =
                        TryGet(p, "url_o") ??
                        TryGet(p, "url_h") ??
                        TryGet(p, "url_l");

                    var title = TryGet(p, "title") ?? "";

                    if (!string.IsNullOrEmpty(imageUrl))
                        photos.Add(new FlickrPhoto(imageUrl, title));
                }

                page++;
            }
            while (page <= pages);

            return photos;
        }


        private static string? TryGet(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var prop)
                ? prop.GetString()
                : null;
        }

        private static string GetStringPropertyFlexible(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var prop))
                return "";

            return prop.ValueKind switch
            {
                JsonValueKind.Object when prop.TryGetProperty("_content", out var c) => c.GetString() ?? "",
                JsonValueKind.String => prop.GetString() ?? "",
                _ => ""
            };
        }

        // Handle Flickr returning title as either a string or an object with "_content".
        private static string GetFlexibleTitle(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var prop))
                return "";

            return prop.ValueKind switch
            {
                JsonValueKind.Object when prop.TryGetProperty("_content", out var c) => c.GetString() ?? "",
                JsonValueKind.String => prop.GetString() ?? "",
                _ => ""
            };
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
                    // request size URLs so we can pick the largest available
                    $"&extras=url_o,url_k,url_h,url_b,url_l" +
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

                        var title = TryGet(p, "title") ?? "";

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(secret))
                        {
                            string? imageUrl =
                                TryGet(p, "url_o") ??
                                TryGet(p, "url_k") ??
                                TryGet(p, "url_h") ??
                                TryGet(p, "url_b") ??
                                TryGet(p, "url_l") ??
                                $"https://live.staticflickr.com/{server}/{id}_{secret}_b.jpg";

                            photos.Add(new FlickrPhoto(imageUrl, title));
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

        // Add this helper to FlickrService
        private async Task<string?> GetLargestPhotoUrlAsync(string photoId)
        {
            var url = $"https://api.flickr.com/services/rest/?" +
                      $"method=flickr.photos.getSizes" +
                      $"&api_key={_apiKey}" +
                      $"&photo_id={Uri.EscapeDataString(photoId)}" +
                      "&format=json&nojsoncallback=1";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("stat").GetString() != "ok")
                return null;

            var sizesNode = doc.RootElement.GetProperty("sizes").GetProperty("size");
            string? best = null;
            int maxWidth = 0;

            foreach (var s in sizesNode.EnumerateArray())
            {
                if (s.TryGetProperty("width", out var wEl) && int.TryParse(wEl.GetRawText(), out var w))
                {
                    if (w > maxWidth)
                    {
                        maxWidth = w;
                        best = s.GetProperty("source").GetString();
                    }
                }
                else if (s.TryGetProperty("label", out var label) && best == null)
                {
                    // fallback if width not present
                    best = s.GetProperty("source").GetString();
                }
            }

            return best;
        }


        #endregion
    }
}



