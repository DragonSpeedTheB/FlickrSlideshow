using System.Collections.Generic;

namespace FlickrSlideshow.Core
{
    public record FlickrPhoto(string Url, string? Title);
    public record FlickrAlbum(string Id, string Title);
    public record FlickrCollection(string Id, string Title, List<FlickrAlbum> Albums);

    public class FlickrUser
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";

        public override string ToString() => Name;
    }
}
