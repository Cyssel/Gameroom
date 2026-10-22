using Playnite.DesktopApp.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Playnite.DesktopApp.Controls.Views
{
    public partial class GamePageView : UserControl
    {
        private readonly GamePageViewModel model;

        public GamePageView(GamePageViewModel model)
        {
            InitializeComponent();
            this.model = model;
            DataContext = model;
            Loaded += GamePageView_Loaded;
            Unloaded += (_, __) =>
            {
                model.PropertyChanged -= Model_PropertyChanged;
                model.Dispose();
            };
            model.PropertyChanged += Model_PropertyChanged;
        }

        private void Model_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GamePageViewModel.HeroImage))
            {
                ShowHero();
            }
        }

        private string shownHero;

        private async void ShowHero()
        {
            var source = model.HeroImage;
            if (source == shownHero)
            {
                return;
            }

            shownHero = source;
            var image = await GameroomImages.LoadAsync(source, model.HeroIsCover);
            if (source != shownHero)
            {
                return;
            }

            Hero.Source = image;
        }

        private void GamePageView_Loaded(object sender, RoutedEventArgs e)
        {
            ShowHero();
            // Slow zoom out of the artwork, like console game pages.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(1600)) { EasingFunction = ease });
            HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, 1, TimeSpan.FromMilliseconds(1600)) { EasingFunction = ease });
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = model.Entry;
            if (entry == null)
            {
                return;
            }

            new GameMenu
            {
                DataContext = entry,
                ShowStartSection = false,
                PlacementTarget = MoreButton,
                Placement = PlacementMode.Bottom,
                IsOpen = true
            };
        }
    }
}
