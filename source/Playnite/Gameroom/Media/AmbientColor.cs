using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Gameroom.Media
{
    /// <summary>
    /// Dominant vivid color of an image, used to tint the interface around game artwork.
    /// </summary>
    public static class AmbientColor
    {
        private static readonly ConcurrentDictionary<string, Color?> cache = new ConcurrentDictionary<string, Color?>(StringComparer.OrdinalIgnoreCase);

        public static Color? FromImage(string path)
        {
            if (path.IsNullOrWhiteSpace() || !File.Exists(path))
            {
                return null;
            }

            return cache.GetOrAdd(path, Compute);
        }

        private static Color? Compute(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();

                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                return FromPixels(pixels);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Averages the pixels weighted by how vivid they are, so dark and gray areas don't wash the color out.
        /// </summary>
        public static Color? FromPixels(byte[] bgra)
        {
            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i + 3 < bgra.Length; i += 4)
            {
                var pb = bgra[i] / 255.0;
                var pg = bgra[i + 1] / 255.0;
                var pr = bgra[i + 2] / 255.0;
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                var saturation = max == 0 ? 0 : (max - min) / max;
                var weight = saturation * saturation * max + 0.002;
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }

            if (total <= 0)
            {
                return null;
            }

            return Normalize(r / total, g / total, b / total);
        }

        /// <summary>
        /// Keeps the hue but makes the color bright and saturated enough to glow on the dark interface.
        /// </summary>
        private static Color Normalize(double r, double g, double b)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            double hue = 0;
            if (delta > 0)
            {
                if (max == r)
                {
                    hue = 60 * (((g - b) / delta) % 6);
                }
                else if (max == g)
                {
                    hue = 60 * (((b - r) / delta) + 2);
                }
                else
                {
                    hue = 60 * (((r - g) / delta) + 4);
                }
            }

            if (hue < 0)
            {
                hue += 360;
            }

            var saturation = max == 0 ? 0 : delta / max;
            // Gray artwork gets the Gameroom violet instead of a dull gray.
            if (saturation < 0.12)
            {
                return Color.FromRgb(0x8B, 0x5C, 0xF6);
            }

            saturation = Math.Max(0.55, Math.Min(0.85, saturation * 1.3));
            var value = Math.Max(0.7, Math.Min(0.95, max * 1.4));
            return FromHsv(hue, saturation, value);
        }

        private static Color FromHsv(double hue, double saturation, double value)
        {
            var c = value * saturation;
            var x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
            var m = value - c;
            double r = 0, g = 0, b = 0;
            switch ((int)(hue / 60) % 6)
            {
                case 0:
                    r = c;
                    g = x;
                    break;
                case 1:
                    r = x;
                    g = c;
                    break;
                case 2:
                    g = c;
                    b = x;
                    break;
                case 3:
                    g = x;
                    b = c;
                    break;
                case 4:
                    r = x;
                    b = c;
                    break;
                default:
                    r = c;
                    b = x;
                    break;
            }

            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
