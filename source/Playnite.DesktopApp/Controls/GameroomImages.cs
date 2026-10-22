using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Full screen artwork for the start page and game pages, decoded in the background and kept for quick reuse.
    /// </summary>
    public static class GameroomImages
    {
        private const int maxCached = 32;
        // Covers used as backgrounds are decoded tiny and stretched, which blurs them softly instead of showing them pixelated.
        private const int softWidth = 36;
        private static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();
        private static readonly LinkedList<string> order = new LinkedList<string>();

        private static ImageSource Decode(string source, bool soft)
        {
            try
            {
                if (File.Exists(source))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(source);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = soft ? softWidth : 1920;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }

                var stored = ImageSourceManager.GetImage(source, !soft, soft ? new BitmapLoadProperties(softWidth, 0) : null);
                if (stored?.CanFreeze == true && !stored.IsFrozen)
                {
                    stored.Freeze();
                }

                return stored;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Must be called from the UI thread.
        /// </summary>
        public static async Task<ImageSource> LoadAsync(string source, bool soft)
        {
            if (source.IsNullOrWhiteSpace())
            {
                return null;
            }

            var key = (soft ? "soft:" : "full:") + source;
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var image = await Task.Run(() => Decode(source, soft));
            if (image != null && !cache.ContainsKey(key))
            {
                cache[key] = image;
                order.AddLast(key);
                if (order.Count > maxCached)
                {
                    cache.Remove(order.First.Value);
                    order.RemoveFirst();
                }
            }

            return image;
        }
    }
}
