# FlickrSlideshow

A simple application to display a slideshow of images from Flickr.
## Features
- Fetches images from Flickr based on username
- displays a slideshow of images in random order from either the user's photostream or selected albums
- left and right arrow keys to navigate through images.  Space bar to pause/play slideshow.
- MRU list of usernames for quick access

## Windows standalone executable

Publish on a Windows x64 machine with the .NET 10 SDK and a Flickr API key. Create `Appsettings.json` in the repository root with the following content, replacing the placeholder with your own key:

```json
{
  "Flickr": {
    "ApiKey": "YOUR_FLICKR_API_KEY"
  }
}
```

Run `dotnet publish .\FlickrSlideshow.csproj -c Release`. The standalone executable is `bin\Release\net10.0-windows\win-x64\publish\FlickrSlideshow.exe`; copy it to any Windows x64 machine and double-click it. No .NET installation or accompanying configuration file is required. Native components are extracted to the user's local temporary storage when the app starts.

The API key is embedded in the executable and can be recovered from it, so do not distribute the executable if you need to keep that key private. You can obtain a key from the [Flickr API website](https://www.flickr.com/services/api/).

Windows remembers recent users, slide duration (15–300 seconds), and debug output settings. Photo lists are cached for 24 hours for users and albums, and one hour for Explore. During a slideshow, use Escape to return to settings, Space to pause or resume, and the arrow keys to navigate photos.

## Building from source

- Windows x64
- .NET 10 SDK
- Visual Studio 2022 or later (optional)
