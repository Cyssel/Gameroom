using NUnit.Framework;
using Playnite.Gameroom.Media;
using System.Windows.Media;

namespace Gameroom.Tests
{
    [TestFixture]
    public class AmbientColorTests
    {
        private static byte[] Pixels(params Color[] colors)
        {
            var bgra = new byte[colors.Length * 4];
            for (var i = 0; i < colors.Length; i++)
            {
                bgra[i * 4] = colors[i].B;
                bgra[i * 4 + 1] = colors[i].G;
                bgra[i * 4 + 2] = colors[i].R;
                bgra[i * 4 + 3] = 255;
            }

            return bgra;
        }

        [Test]
        public void VividPixelsWinOverDarkAndGrayTest()
        {
            // Mostly black and gray artwork with some red: the glow should be red.
            var color = AmbientColor.FromPixels(Pixels(
                Colors.Black, Colors.Black, Colors.Black, Color.FromRgb(90, 90, 90), Color.FromRgb(200, 30, 40)));
            Assert.IsNotNull(color);
            Assert.Greater(color.Value.R, color.Value.G * 2);
            Assert.Greater(color.Value.R, color.Value.B * 2);
            Assert.GreaterOrEqual(color.Value.R, 178, "Bright enough to glow on the dark interface.");
        }

        [Test]
        public void GrayArtworkGetsAccentColorTest()
        {
            var color = AmbientColor.FromPixels(Pixels(Color.FromRgb(120, 120, 120), Color.FromRgb(60, 60, 60)));
            Assert.AreEqual(Color.FromRgb(0x8B, 0x5C, 0xF6), color);
        }
    }
}
