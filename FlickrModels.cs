using System;
using System.Collections.Generic;
using System.Text;


namespace FlickrSlideshow
{
    public record FlickrPhoto(string Url);
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
