using Playnite.Gameroom;
using Playnite.Gameroom.Achievements;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Windows;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.ViewModels
{
    public class AchievementItem
    {
        public GameAchievement Achievement { get; set; }
        public bool Unlocked => Achievement.Unlocked;
        /// <summary>Locked hidden achievement whose details aren't shown.</summary>
        public bool IsSecret { get; set; }
        public string Name => IsSecret ? ResourceProvider.GetString("LOCGameroomAchievementsHiddenName") : Achievement.Definition.Name;
        public string Description => IsSecret ? ResourceProvider.GetString("LOCGameroomAchievementsHiddenDescription") : Achievement.Definition.Description;
        public string IconPath => IsSecret ? null : Achievement.IconPath;
        public bool HasProgress => !Unlocked && Achievement.MaxProgress > 0;
        public double ProgressPercent => Achievement.MaxProgress == 0 ? 0 : Math.Min(100, 100.0 * Achievement.CurrentProgress / Achievement.MaxProgress);
        public string ProgressText => $"{Achievement.CurrentProgress} / {Achievement.MaxProgress}";
        public bool IsRare => Achievement.Definition.GlobalPercent < 10;

        public string UnlockText => Unlocked
            ? Achievement.UnlockTime == null
                ? ResourceProvider.GetString("LOCGameroomAchievementsUnlocked")
                : string.Format(ResourceProvider.GetString("LOCGameroomAchievementsUnlockedOn"), Achievement.UnlockTime.Value.ToString("g", CultureInfo.CurrentCulture))
            : null;

        public string RarityText => Achievement.Definition.GlobalPercent == null
            ? null
            : string.Format(ResourceProvider.GetString("LOCGameroomAchievementPercent"), Achievement.Definition.GlobalPercent.Value.ToString("0.#", CultureInfo.CurrentCulture));
    }

    public class GameAchievementsViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IWindowFactory window;
        private readonly GameroomService service;
        private readonly Game game;
        private GameAchievements achievements;

        public string Title => $"{ResourceProvider.GetString("LOCGameroomAchievementsTitle")} - {game.Name}";
        public string GameName => game.Name;

        private bool isLoading = true;
        public bool IsLoading
        {
            get => isLoading;
            private set => SetValue(ref isLoading, value);
        }

        private bool hasAchievements;
        public bool HasAchievements
        {
            get => hasAchievements;
            private set => SetValue(ref hasAchievements, value);
        }

        public bool ShowEmpty => !IsLoading && !HasAchievements;

        private string emptyText;
        public string EmptyText
        {
            get => emptyText;
            private set => SetValue(ref emptyText, value);
        }

        private string summary;
        public string Summary
        {
            get => summary;
            private set => SetValue(ref summary, value);
        }

        private string sourcesText;
        public string SourcesText
        {
            get => sourcesText;
            private set => SetValue(ref sourcesText, value);
        }

        private double percent;
        public double Percent
        {
            get => percent;
            private set => SetValue(ref percent, value);
        }

        private string percentText;
        public string PercentText
        {
            get => percentText;
            private set => SetValue(ref percentText, value);
        }

        private List<AchievementItem> items = new List<AchievementItem>();
        public List<AchievementItem> Items
        {
            get => items;
            private set => SetValue(ref items, value);
        }

        private bool showAll = true;
        public bool ShowAll
        {
            get => showAll;
            set
            {
                SetValue(ref showAll, value);
                ApplyFilter();
            }
        }

        private bool showUnlocked;
        public bool ShowUnlocked
        {
            get => showUnlocked;
            set
            {
                SetValue(ref showUnlocked, value);
                ApplyFilter();
            }
        }

        private bool showLocked;
        public bool ShowLocked
        {
            get => showLocked;
            set
            {
                SetValue(ref showLocked, value);
                ApplyFilter();
            }
        }

        private bool revealHidden;
        public bool RevealHidden
        {
            get => revealHidden;
            set
            {
                SetValue(ref revealHidden, value);
                ApplyFilter();
            }
        }

        private string steamAppId;
        /// <summary>Steam app id typed by the user when the game wasn't found on Steam.</summary>
        public string SteamAppId
        {
            get => steamAppId;
            set => SetValue(ref steamAppId, value);
        }

        public RelayCommand RefreshCommand { get; }
        public RelayCommand UseAppIdCommand { get; }
        public RelayCommand CloseCommand { get; }

        public GameAchievementsViewModel(IWindowFactory window, GameroomService service, Game game)
        {
            this.window = window;
            this.service = service;
            this.game = game;
            RefreshCommand = new RelayCommand(() => Load(), () => !IsLoading);
            UseAppIdCommand = new RelayCommand(UseAppId, () => !SteamAppId.IsNullOrWhiteSpace() && SteamAppId.Trim().All(char.IsDigit));
            CloseCommand = new RelayCommand(() => window.Close());
            service.Achievements.AchievementsUnlocked += Achievements_AchievementsUnlocked;
        }

        public void OpenView()
        {
            Load();
            window.CreateAndOpenDialog(this);
            service.Achievements.AchievementsUnlocked -= Achievements_AchievementsUnlocked;
        }

        private void Achievements_AchievementsUnlocked(object sender, AchievementsUnlockedEventArgs e)
        {
            if (e.AppId == achievements?.AppId)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Load()));
            }
        }

        private void UseAppId()
        {
            var data = service.Database.GetOrCreateGameData(game.Id);
            data.SteamAppId = SteamAppId.Trim();
            service.Database.SaveGameData(data);
            Load();
        }

        private async void Load()
        {
            IsLoading = true;
            OnPropertyChanged(nameof(ShowEmpty));
            try
            {
                var appId = await Task.Run(() => service.Achievements.GetAppId(game, true));
                achievements = appId == null ? null : await Task.Run(() => service.Achievements.GetGameAchievements(appId));
                EmptyText = ResourceProvider.GetString(appId == null ? "LOCGameroomAchievementsNotFound" : "LOCGameroomAchievementsNone");
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to load achievements of {game.Name}");
                achievements = null;
                EmptyText = ResourceProvider.GetString("LOCGameroomAchievementsFailed");
            }

            HasAchievements = achievements?.Total > 0;
            if (HasAchievements)
            {
                Summary = string.Format(ResourceProvider.GetString("LOCGameroomAchievementsSummary"), achievements.UnlockedCount, achievements.Total);
                Percent = achievements.Percent;
                PercentText = $"{Math.Floor(achievements.Percent).ToString(CultureInfo.CurrentCulture)} %";
                SourcesText = achievements.Sources.Count == 0
                    ? ResourceProvider.GetString("LOCGameroomAchievementsNoProgress")
                    : string.Format(ResourceProvider.GetString("LOCGameroomAchievementsSources"),
                        string.Join(", ", achievements.Sources.Select(a => ResourceProvider.GetString("LOCGameroomAchievementSource" + a))));
            }

            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (achievements == null)
            {
                Items = new List<AchievementItem>();
                return;
            }

            // Like Steam: newest unlocks first, then what most players already got.
            Items = achievements.Items
                .Where(a => ShowAll || (ShowUnlocked && a.Unlocked) || (ShowLocked && !a.Unlocked))
                .OrderByDescending(a => a.Unlocked)
                .ThenByDescending(a => a.Unlocked ? a.UnlockTime ?? DateTime.MinValue : DateTime.MinValue)
                .ThenByDescending(a => a.Definition.GlobalPercent ?? 0)
                .Select(a => new AchievementItem
                {
                    Achievement = a,
                    IsSecret = !a.Unlocked && a.Definition.Hidden && !RevealHidden
                })
                .ToList();
        }
    }
}
