using System.Collections.Generic;

namespace FlickrSlideshow.Xbox
{
    public class FlickrPhoto
    {
        public string Url { get; }
        public string Title { get; }
        public FlickrPhoto(string url, string title) { Url = url; Title = title; }
    }

    public class FlickrAlbum
    {
        public string Id { get; }
        public string Title { get; }
        public FlickrAlbum(string id, string title) { Id = id; Title = title; }
    }

    public class FlickrCollection
    {
        public string Id { get; }
        public string Title { get; }
        public List<FlickrAlbum> Albums { get; }
        public FlickrCollection(string id, string title, List<FlickrAlbum> albums) { Id = id; Title = title; Albums = albums; }
    }

    public class FlickrUser
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public override string ToString() => Name;
    }
}