using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Playnite.Gameroom.Achievements
{
    public class AchievementToast
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string GameName { get; set; }
        public string IconPath { get; set; }
        public double? GlobalPercent { get; set; }
    }

    /// <summary>
    /// Shows unlocked achievements over the running game, one after another.
    /// </summary>
    public static class AchievementOverlay
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Queue<(AchievementToast Toast, bool Sound)> queue = new Queue<(AchievementToast, bool)>();
        private static bool showing;

        public static void Show(IEnumerable<AchievementToast> toasts, bool playSound)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            var list = new List<AchievementToast>(toasts);
            dispatcher.BeginInvoke(new Action(() =>
            {
                list.ForEach(a => queue.Enqueue((a, playSound)));
                if (!showing)
                {
                    ShowNext();
                }
            }));
        }

        private static void ShowNext()
        {
            if (queue.Count == 0)
            {
                showing = false;
                return;
            }

            showing = true;
            var (toast, sound) = queue.Dequeue();
            try
            {
                var window = new AchievementToastWindow(toast);
                window.Closed += (_, __) => ShowNext();
                window.ShowToast(sound);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to show achievement overlay.");
                ShowNext();
            }
        }
    }

    public partial class AchievementToastWindow : Window
    {
        private const int visibleMs = 6000;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x8000000;
        private const uint SWP_NOSIZE = 0x1;
        private const uint SWP_NOACTIVATE = 0x10;
        private const uint SWP_SHOWWINDOW = 0x40;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private bool playSound;

        public AchievementToastWindow(AchievementToast toast)
        {
            InitializeComponent();
            HeaderText.Text = ResourceProvider.GetString("LOCGameroomAchievementUnlocked").ToUpper(GameroomLocalization.Culture);
            NameText.Text = toast.Name;
            DescriptionText.Text = toast.Description;
            DescriptionText.Visibility = toast.Description.IsNullOrWhiteSpace() ? Visibility.Collapsed : Visibility.Visible;

            var footer = new List<string>();
            if (!toast.GameName.IsNullOrWhiteSpace())
            {
                footer.Add(toast.GameName);
            }

            if (toast.GlobalPercent != null)
            {
                footer.Add(string.Format(ResourceProvider.GetString(toast.GlobalPercent < 10 ? "LOCGameroomAchievementRare" : "LOCGameroomAchievementPercent"),
                    toast.GlobalPercent.Value.ToString("0.#", CultureInfo.CurrentCulture)));
            }

            FooterText.Text = string.Join("  ·  ", footer);
            FooterText.Visibility = footer.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (!toast.IconPath.IsNullOrWhiteSpace() && File.Exists(toast.IconPath))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(toast.IconPath);
                    image.EndInit();
                    IconBorder.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    IconFallback.Visibility = Visibility.Collapsed;
                }
                catch
                {
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Never takes focus or clicks away from the game.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        }

        public void ShowToast(bool sound)
        {
            playSound = sound;
            ContentRendered += AchievementToastWindow_ContentRendered;
            Show();
        }

        private void AchievementToastWindow_ContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= AchievementToastWindow_ContentRendered;
            PlaceOnScreen();
            if (playSound)
            {
                AchievementSound.Play();
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
            CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(visibleMs) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                fade.Completed += (___, ____) => Close();
                Card.BeginAnimation(OpacityProperty, fade);
                CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 16, TimeSpan.FromMilliseconds(400)));
            };
            timer.Start();
        }

        /// <summary>
        /// Bottom right corner of the screen the game is on. Fullscreen games cover the taskbar, so its area is used too.
        /// </summary>
        private void PlaceOnScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var foreground = GetForegroundWindow();
            var screen = foreground == IntPtr.Zero
                ? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.FromHandle(foreground);
            var area = screen.WorkingArea;
            if (foreground != IntPtr.Zero && foreground != GetShellWindow() && foreground != GetDesktopWindow() &&
                GetWindowRect(foreground, out var foregroundRect) &&
                foregroundRect.Left <= screen.Bounds.Left && foregroundRect.Top <= screen.Bounds.Top &&
                foregroundRect.Right >= screen.Bounds.Right && foregroundRect.Bottom >= screen.Bounds.Bottom)
            {
                area = screen.Bounds;
            }

            GetWindowRect(hwnd, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            // The window already has a transparent margin around the card.
            SetWindowPos(hwnd, HWND_TOPMOST, area.Right - width, area.Bottom - height, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>
    /// Short two note chime, generated so no sound file has to be shipped.
    /// </summary>
    public static class AchievementSound
    {
        // Kept alive: the sound plays asynchronously from the player's memory.
        private static SoundPlayer player;

        public static void Play()
        {
            try
            {
                if (player == null)
                {
                    player = new SoundPlayer(new MemoryStream(CreateChime()));
                    player.Load();
                }

                player.Play();
            }
            catch
            {
            }
        }

        public static byte[] CreateChime()
        {
            const int sampleRate = 44100;
            const double length = 0.9;
            var notes = new[] { (Frequency: 1046.5, Start: 0.0), (Frequency: 1568.0, Start: 0.11) };
            var samples = new short[(int)(sampleRate * length)];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = (double)i / sampleRate;
                var value = 0.0;
                foreach (var note in notes)
                {
                    var local = t - note.Start;
                    if (local < 0)
                    {
                        continue;
                    }

                    var envelope = Math.Min(1.0, local / 0.006) * Math.Exp(-local * 5.5);
                    value += envelope * (Math.Sin(2 * Math.PI * note.Frequency * local) +
                        0.28 * Math.Sin(4 * Math.PI * note.Frequency * local) +
                        0.1 * Math.Sin(6 * Math.PI * note.Frequency * local));
                }

                samples[i] = (short)(Math.Max(-1, Math.Min(1, value * 0.22)) * short.MaxValue);
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var dataSize = samples.Length * 2;
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                foreach (var sample in samples)
                {
                    writer.Write(sample);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
