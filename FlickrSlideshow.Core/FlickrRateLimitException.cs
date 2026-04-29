using System;

namespace FlickrSlideshow.Core
{
    /// <summary>
    /// Thrown when the Flickr API responds with HTTP 429 (Too Many Requests)
    /// and the maximum retry attempts have been exhausted.
    /// </summary>
    public class FlickrRateLimitException : Exception
    {
        public TimeSpan RetryAfter { get; }

        public FlickrRateLimitException(TimeSpan retryAfter)
            : base($"Flickr API rate limit exceeded. Retry after {retryAfter.TotalSeconds:0}s.")
        {
            RetryAfter = retryAfter;
        }
    }
}
