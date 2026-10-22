using Playnite.DesktopApp.ViewModels;
using Playnite.DesktopApp.Windows;
using Playnite.Gameroom;
using Playnite.Gameroom.Achievements;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Achievement progress of a game for the game details panels: <c>&lt;GameAchievementsSummary Game="{Binding Game.Game}" /&gt;</c>.
    /// </summary>
    public partial class GameAchievementsSummary : UserControl
    {
        private const int recentIcons = 10;
        private static readonly ILogger logger = LogManager.GetLogger();
        private int loadVersion;
        private string appId;
        private AchievementService subscribed;

        public class RecentIcon
        {
            public string IconPath { get; set; }
            public string Tooltip { get; set; }
        }

        public class SummaryState
        {
            public string Summary { get; set; }
            public string PercentText { get; set; }
            public double Percent { get; set; }
            public List<RecentIcon> Recent { get; set; }
        }

        public static readonly DependencyProperty GameProperty = DependencyProperty.Register(
            nameof(Game), typeof(Game), typeof(GameAchievementsSummary), new PropertyMetadata(null, (d, _) => ((GameAchievementsSummary)d).Load()));

        public Game Game
        {
            get => (Game)GetValue(GameProperty);
            set => SetValue(GameProperty, value);
        }

        public GameAchievementsSummary()
        {
            InitializeComponent();
            Loaded += (_, __) => Subscribe(true);
            Unloaded += (_, __) => Subscribe(false);
        }

        private void Subscribe(bool subscribe)
        {
            if (subscribed != null)
            {
                subscribed.AchievementsUnlocked -= Achievements_AchievementsUnlocked;
                subscribed = null;
            }

            var service = GameroomService.Instance?.Achievements;
            if (subscribe && service != null)
            {
                subscribed = service;
                service.AchievementsUnlocked += Achievements_AchievementsUnlocked;
            }
        }

        private void Achievements_AchievementsUnlocked(object sender, AchievementsUnlockedEventArgs e)
        {
            if (e.AppId == appId)
            {
                Dispatcher.BeginInvoke(new Action(Load));
            }
        }

        private static List<RecentIcon> GetRecent(AchievementService achievements, GameAchievements loaded, bool allowDownload)
        {
            return loaded.Items
                .Where(a => a.Unlocked)
                .OrderByDescending(a => a.UnlockTime ?? DateTime.MinValue)
                .Take(recentIcons)
                .Select(a => new RecentIcon
                {
                    IconPath = achievements.GetIconPath(loaded.AppId, a.Definition.Icon, allowDownload),
                    Tooltip = a.UnlockTime == null ? a.Definition.Name : $"{a.Definition.Name}\n{a.UnlockTime.Value.ToString("g", CultureInfo.CurrentCulture)}"
                }).ToList();
        }

        private void Show(GameAchievements achievements, List<RecentIcon> recent, bool animate)
        {
            appId = achievements.AppId;
            LayoutRoot.DataContext = new SummaryState
            {
                Summary = string.Format(ResourceProvider.GetString("LOCGameroomAchievementsSummary"), achievements.UnlockedCount, achievements.Total),
                PercentText = $"{Math.Floor(achievements.Percent).ToString(CultureInfo.CurrentCulture)} %",
                Percent = achievements.Percent,
                Recent = recent
            };

            if (LayoutRoot.Visibility != Visibility.Visible)
            {
                LayoutRoot.Visibility = Visibility.Visible;
                if (animate)
                {
                    LayoutRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                }
                else
                {
                    LayoutRoot.BeginAnimation(OpacityProperty, null);
                }
            }
        }

        private void Hide()
        {
            appId = null;
            LayoutRoot.Visibility = Visibility.Collapsed;
        }

        private async void Load()
        {
            var version = ++loadVersion;
            var game = Game;
            var service = GameroomService.Instance;
            if (game == null || service == null)
            {
                Hide();
                return;
            }

            // Cached achievements show up together with the rest of the game details, so nothing of the previously
            // selected game stays on screen and nothing pops in afterwards.
            GameAchievements cached = null;
            var knownEmpty = false;
            List<RecentIcon> cachedRecent = null;
            try
            {
                cached = service.Achievements.GetCachedGameAchievements(game, out knownEmpty);
                cachedRecent = cached == null ? null : GetRecent(service.Achievements, cached, false);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to read cached achievements of {game.Name}");
            }

            if (cached?.Total > 0)
            {
                Show(cached, cachedRecent, false);
            }
            else
            {
                Hide();
                if (knownEmpty)
                {
                    return;
                }
            }

            // Then download what's missing: schema, icons or the Steam app of games from other stores.
            if (cached != null && cachedRecent.All(a => a.IconPath != null))
            {
                return;
            }

            GameAchievements achievements = null;
            List<RecentIcon> recent = null;
            try
            {
                achievements = await Task.Run(() =>
                {
                    var id = service.Achievements.GetAppId(game, true);
                    return id == null ? null : service.Achievements.GetGameAchievements(id, false);
                });

                if (achievements != null)
                {
                    recent = await Task.Run(() => GetRecent(service.Achievements, achievements, true));
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to load achievements of {game.Name}");
            }

            if (version != loadVersion)
            {
                return;
            }

            if (achievements == null || achievements.Total == 0)
            {
                Hide();
                return;
            }

            Show(achievements, recent, true);
        }

        private void OpenAll_Click(object sender, RoutedEventArgs e)
        {
            var service = GameroomService.Instance;
            if (Game != null && service != null)
            {
                new GameAchievementsViewModel(new GameAchievementsWindowFactory(), service, Game).OpenView();
                Load();
            }
        }
    }
}
