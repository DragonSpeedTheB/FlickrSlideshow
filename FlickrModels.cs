using System;
using System.Collections.Generic;
using System.Text;


namespace FlickrSlideshow
{
    // include Title so callers may display it
    public record FlickrPhoto(string Url, string? Title);
    public record FlickrAlbum(string Id, string Title);

    class FlickrModels
    {
    }
    public class FlickrUser 
    {
        public string Name { get; set; }  // e.g., "BrianHampson"
        public string Id { get; set; }    // Flickr user ID

        public override string ToString() => Name; // Shows Name in ComboBox
    }

}
