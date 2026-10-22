using Playnite.DesktopApp.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Playnite.DesktopApp.Controls.Views
{
    public partial class HomeView : UserControl
    {
        private readonly HomeViewModel model;
        private readonly DispatcherTimer clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        // Sweeping the pointer over the carousel only selects the game it stops on.
        private readonly DispatcherTimer hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        private HomeGameItem hoveredItem;
        private Image visibleBackdrop;
        private string shownBackdrop;
        private int backdropVersion;

        public HomeView(HomeViewModel model)
        {
            InitializeComponent();
            this.model = model;
            DataContext = model;
            visibleBackdrop = BackdropA;
            model.PropertyChanged += Model_PropertyChanged;
            clockTimer.Tick += (_, __) => model.UpdateClock();
            hoverTimer.Tick += HoverTimer_Tick;
            Loaded += (_, __) =>
            {
                clockTimer.Start();
                ShowBackdrop(model.HeroImage, false);
                AnimateAmbient(model.Ambient, false);
            };
            Unloaded += (_, __) =>
            {
                clockTimer.Stop();
                hoverTimer.Stop();
            };
        }

        private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(HomeViewModel.HeroImage):
                    ShowBackdrop(model.HeroImage, true);
                    break;
                case nameof(HomeViewModel.Ambient):
                    AnimateAmbient(model.Ambient, true);
                    break;
                case nameof(HomeViewModel.HeroName):
                    AnimateHeroInfo();
                    break;
            }
        }

        /// <summary>
        /// Fades the artwork of the selected game in over the current one. The old artwork stays fully visible
        /// underneath until the new one covers it, so the page never darkens in between.
        /// </summary>
        private async void ShowBackdrop(string source, bool animate)
        {
            var soft = model.HeroIsCover;
            if (source == shownBackdrop && visibleBackdrop.Source != null)
            {
                return;
            }

            shownBackdrop = source;
            var version = ++backdropVersion;
            var image = await GameroomImages.LoadAsync(source, soft);
            if (version != backdropVersion)
            {
                return;
            }

            var next = visibleBackdrop == BackdropA ? BackdropB : BackdropA;
            var previous = visibleBackdrop;
            visibleBackdrop = next;
            Panel.SetZIndex(next, 1);
            Panel.SetZIndex(previous, 0);
            next.Source = image;

            var scale = new ScaleTransform(1, 1);
            next.RenderTransformOrigin = new Point(0.5, 0.3);
            next.RenderTransform = scale;
            if (!animate)
            {
                next.BeginAnimation(OpacityProperty, null);
                next.Opacity = 1;
                previous.BeginAnimation(OpacityProperty, null);
                previous.Opacity = 0;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
            fadeIn.Completed += (_, __) =>
            {
                if (visibleBackdrop == next)
                {
                    previous.BeginAnimation(OpacityProperty, null);
                    previous.Opacity = 0;
                }
            };

            previous.BeginAnimation(OpacityProperty, null);
            previous.Opacity = previous.Source == null ? 0 : 1;
            next.BeginAnimation(OpacityProperty, fadeIn);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.03, 1, TimeSpan.FromMilliseconds(1100)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.03, 1, TimeSpan.FromMilliseconds(1100)) { EasingFunction = ease });
        }

        private void AnimateAmbient(Color color, bool animate)
        {
            AmbientStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(animate ? 600 : 0)));
        }

        private void AnimateHeroInfo()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            HeroInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            HeroOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }

        private void Tile_MouseEnter(object sender, MouseEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is HomeGameItem item && item != model.SelectedItem)
            {
                hoveredItem = item;
                hoverTimer.Stop();
                hoverTimer.Start();
            }
        }

        private void Tile_MouseLeave(object sender, MouseEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext == hoveredItem)
            {
                hoverTimer.Stop();
                hoveredItem = null;
            }
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            hoverTimer.Stop();
            if (hoveredItem != null)
            {
                model.SelectedItem = hoveredItem;
                hoveredItem = null;
            }
        }

        private void CarouselScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // The wheel scrolls the carousel sideways, like a console menu, until it reaches an end.
            var atStart = CarouselScroll.HorizontalOffset <= 0;
            var atEnd = CarouselScroll.HorizontalOffset >= CarouselScroll.ScrollableWidth;
            if ((e.Delta > 0 && atStart) || (e.Delta < 0 && atEnd))
            {
                return;
            }

            CarouselScroll.ScrollToHorizontalOffset(CarouselScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
