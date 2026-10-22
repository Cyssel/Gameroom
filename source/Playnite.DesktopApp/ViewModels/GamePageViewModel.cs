using Playnite.Common;
using Playnite.DesktopApp.Controls;
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
using System.Windows.Media;
using Ambient = Playnite.Gameroom.Media.AmbientColor;

namespace Playnite.DesktopApp.ViewModels
{
    public class GamePageSession
    {
        public string DateText { get; set; }
        public string DurationText { get; set; }
        public string SourceText { get; set; }
        public double BarWidth { get; set; }
    }

    public class GamePageAchievement
    {
        public string IconPath { get; set; }
        public string Tooltip { get; set; }
    }

    public class GamePageFact
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Page of one game: artwork, play button, playtime, achievements, saves and activity.
    /// </summary>
    public class GamePageViewModel : ObservableObject, IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const int shownAchievements = 14;
        private const int shownSessions = 6;
        private const double maxSessionBar = 170;
        private readonly DesktopAppViewModel model;
        private readonly GameroomService service;

        public GamePageViewModel(DesktopAppViewModel model, Game game)
        {
            this.model = model;
            service = GameroomService.Instance;
            Game = game;
            BackCommand = new RelayCommand(() => model.CloseGamePage());
            PlayCommand = new RelayCommand(Play, () => !Game.IsRunning && !Game.IsLaunching && !Game.IsInstalling);
            AchievementsCommand = new RelayCommand(() => new GameAchievementsViewModel(new GameAchievementsWindowFactory(), service, Game).OpenView());
            SavesCommand = new RelayCommand(() =>
            {
                new GameSavesViewModel(new GameSavesWindowFactory(), model.Dialogs, service, Game).OpenView();
                LoadSaves();
            });
            BackupNowCommand = new RelayCommand(() =>
            {
                GameroomMenu.BackupNow(service, Game, model.Dialogs);
                LoadSaves();
            });
            SessionsCommand = new RelayCommand(() => new GameSessionsViewModel(new GameSessionsWindowFactory(), service, Game).OpenView());
            EditCommand = new RelayCommand(() => model.EditGameCommand.Execute(Game));
            OpenFolderCommand = new RelayCommand(() => Explorer.OpenDirectory(Game.InstallDirectory), () => Game.IsInstalled && !Game.InstallDirectory.IsNullOrWhiteSpace());
            ShowInLibraryCommand = new RelayCommand(() =>
            {
                model.SwitchToLibraryView();
                model.SelectGame(Game.Id);
            });

            model.Database.Games.ItemUpdated += Games_ItemUpdated;
            if (service != null)
            {
                service.Achievements.AchievementsUnlocked += Achievements_AchievementsUnlocked;
            }

            Refresh();
            LoadArtwork();
            LoadAchievements();
        }

        public void Dispose()
        {
            model.Database.Games.ItemUpdated -= Games_ItemUpdated;
            if (service != null)
            {
                service.Achievements.AchievementsUnlocked -= Achievements_AchievementsUnlocked;
            }
        }

        public RelayCommand BackCommand { get; }
        public RelayCommand PlayCommand { get; }
        public RelayCommand AchievementsCommand { get; }
        public RelayCommand SavesCommand { get; }
        public RelayCommand BackupNowCommand { get; }
        public RelayCommand SessionsCommand { get; }
        public RelayCommand EditCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand ShowInLibraryCommand { get; }

        private Game game;
        public Game Game
        {
            get => game;
            private set => SetValue(ref game, value);
        }

        public GamesCollectionViewEntry Entry => model.GamesView?.Items.FirstOrDefault(a => a.Id == Game.Id);

        private string heroImage;
        public string HeroImage
        {
            get => heroImage;
            private set => SetValue(ref heroImage, value);
        }

        /// <summary>The game has no wide artwork, <see cref="HeroImage"/> is its cover.</summary>
        public bool HeroIsCover { get; private set; }

        private string logoPath;
        public string LogoPath
        {
            get => logoPath;
            private set
            {
                SetValue(ref logoPath, value);
                OnPropertyChanged(nameof(HasLogo));
            }
        }

        public bool HasLogo => !LogoPath.IsNullOrWhiteSpace();

        private Color ambientColor = Color.FromRgb(0x8B, 0x5C, 0xF6);
        public Color AmbientColor
        {
            get => ambientColor;
            private set => SetValue(ref ambientColor, value);
        }

        private string playText;
        public string PlayText
        {
            get => playText;
            private set => SetValue(ref playText, value);
        }

        private string playIcon;
        public string PlayIcon
        {
            get => playIcon;
            private set => SetValue(ref playIcon, value);
        }

        private string playtimeText;
        public string PlaytimeText
        {
            get => playtimeText;
            private set => SetValue(ref playtimeText, value);
        }

        private string lastPlayedText;
        public string LastPlayedText
        {
            get => lastPlayedText;
            private set => SetValue(ref lastPlayedText, value);
        }

        private bool hasAchievements;
        public bool HasAchievements
        {
            get => hasAchievements;
            private set => SetValue(ref hasAchievements, value);
        }

        private string achievementsText = "—";
        public string AchievementsText
        {
            get => achievementsText;
            private set => SetValue(ref achievementsText, value);
        }

        private string achievementsSummary;
        public string AchievementsSummary
        {
            get => achievementsSummary;
            private set => SetValue(ref achievementsSummary, value);
        }

        private double achievementsPercent;
        public double AchievementsPercent
        {
            get => achievementsPercent;
            private set => SetValue(ref achievementsPercent, value);
        }

        private List<GamePageAchievement> recentAchievements = new List<GamePageAchievement>();
        public List<GamePageAchievement> RecentAchievements
        {
            get => recentAchievements;
            private set => SetValue(ref recentAchievements, value);
        }

        private string rarestText;
        public string RarestText
        {
            get => rarestText;
            private set => SetValue(ref rarestText, value);
        }

        private string rarestIcon;
        public string RarestIcon
        {
            get => rarestIcon;
            private set => SetValue(ref rarestIcon, value);
        }

        private string savesText;
        public string SavesText
        {
            get => savesText;
            private set => SetValue(ref savesText, value);
        }

        private string savesShortText = "—";
        /// <summary>When the latest backup was made, for the stats bar.</summary>
        public string SavesShortText
        {
            get => savesShortText;
            private set => SetValue(ref savesShortText, value);
        }

        private string savesDetail;
        public string SavesDetail
        {
            get => savesDetail;
            private set => SetValue(ref savesDetail, value);
        }

        private string cloudText;
        public string CloudText
        {
            get => cloudText;
            private set => SetValue(ref cloudText, value);
        }

        private bool isInCloud;
        public bool IsInCloud
        {
            get => isInCloud;
            private set => SetValue(ref isInCloud, value);
        }

        private List<GamePageSession> sessions = new List<GamePageSession>();
        public List<GamePageSession> Sessions
        {
            get => sessions;
            private set
            {
                SetValue(ref sessions, value);
                OnPropertyChanged(nameof(HasSessions));
            }
        }

        public bool HasSessions => Sessions.Count > 0;

        private string activitySummary;
        public string ActivitySummary
        {
            get => activitySummary;
            private set => SetValue(ref activitySummary, value);
        }

        private List<GamePageFact> facts = new List<GamePageFact>();
        public List<GamePageFact> Facts
        {
            get => facts;
            private set => SetValue(ref facts, value);
        }

        private List<string> genres = new List<string>();
        public List<string> Genres
        {
            get => genres;
            private set => SetValue(ref genres, value);
        }

        public bool HasDescription => !Game.Description.IsNullOrWhiteSpace();

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            var update = e.UpdatedItems.FirstOrDefault(a => a.NewData.Id == Game.Id);
            if (update != null)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Game = update.NewData;
                    Refresh();
                }));
            }
        }

        private void Achievements_AchievementsUnlocked(object sender, AchievementsUnlockedEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(LoadAchievements));
        }

        private void Play()
        {
            if (Game.IsInstalled)
            {
                model.StartGameCommand.Execute(Game);
            }
            else
            {
                model.InstallGameCommand.Execute(Game);
            }
        }

        /// <summary>
        /// "5 min ago", "2 h ago", then the same texts as the start page.
        /// </summary>
        public static string FormatRelative(DateTime date)
        {
            var elapsed = DateTime.Now - date;
            if (elapsed.TotalMinutes < 1)
            {
                return ResourceProvider.GetString("LOCGameroomJustNow");
            }

            if (elapsed.TotalHours < 1)
            {
                return string.Format(ResourceProvider.GetString("LOCGameroomMinutesAgo"), (int)elapsed.TotalMinutes);
            }

            if (elapsed.TotalHours < 24 && date.Date == DateTime.Today)
            {
                return string.Format(ResourceProvider.GetString("LOCGameroomHoursAgo"), (int)elapsed.TotalHours);
            }

            return HomeViewModel.FormatLastPlayed(date);
        }

        private void Refresh()
        {
            if (Game.IsRunning)
            {
                PlayText = ResourceProvider.GetString("LOCGameroomGameRunning");
                PlayIcon = "";
            }
            else if (Game.IsLaunching)
            {
                PlayText = ResourceProvider.GetString("LOCGameroomGameLaunching");
                PlayIcon = "";
            }
            else if (Game.IsInstalling)
            {
                PlayText = ResourceProvider.GetString("LOCGameroomGameInstalling");
                PlayIcon = "";
            }
            else if (Game.IsInstalled)
            {
                PlayText = ResourceProvider.GetString("LOCGameroomHomePlay");
                PlayIcon = "";
            }
            else
            {
                PlayText = ResourceProvider.GetString("LOCGameroomGameInstall");
                PlayIcon = "";
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            PlaytimeText = Game.Playtime < 60 ? ResourceProvider.GetString("LOCGameroomHomeNotPlayed") : GameroomService.FormatDuration(Game.Playtime);
            LastPlayedText = Game.LastActivity == null ? "—" : FormatRelative(Game.LastActivity.Value);
            OnPropertyChanged(nameof(HasDescription));
            LoadFacts();
            LoadSessions();
            LoadSaves();
        }

        private static string LowerFirst(string text)
        {
            return text.IsNullOrEmpty() ? text : char.ToLower(text[0], GameroomLocalization.Culture) + text.Substring(1);
        }

        private void LoadFacts()
        {
            var result = new List<GamePageFact>();
            void add(string key, string value)
            {
                if (!value.IsNullOrWhiteSpace())
                {
                    result.Add(new GamePageFact { Label = ResourceProvider.GetString(key), Value = value });
                }
            }

            add("LOCGameroomGameDeveloper", Game.Developers?.Count > 0 ? string.Join(", ", Game.Developers.Select(a => a.Name)) : null);
            add("LOCGameroomGamePublisher", Game.Publishers?.Count > 0 ? string.Join(", ", Game.Publishers.Select(a => a.Name)) : null);
            add("LOCGameroomGameReleased", Game.ReleaseDate?.Date.ToString("d MMM yyyy", GameroomLocalization.Culture));
            add("LOCGameroomGamePlatform", Game.Platforms?.Count > 0 ? string.Join(", ", Game.Platforms.Select(a => a.Name)) : null);
            var library = model.Extensions.Plugins.TryGetValue(Game.PluginId, out var plugin) ? plugin.Description.Name : Game.Source?.Name;
            add("LOCGameroomGameLibrary", library ?? ResourceProvider.GetString("LOCGameroomGameManual"));
            add("LOCGameroomGameInstallSize", Game.InstallSize > 0 ? FormatSize(Game.InstallSize.Value) : null);
            add("LOCGameroomGameAdded", Game.Added?.ToString("d MMM yyyy", GameroomLocalization.Culture));
            Facts = result;
            Genres = Game.Genres?.Select(a => a.Name).ToList() ?? new List<string>();
        }

        private static string FormatSize(ulong bytes)
        {
            return bytes >= 1024UL * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
                : $"{Math.Max(1, bytes / 1024.0 / 1024):0} MB";
        }

        private void LoadSessions()
        {
            if (service == null)
            {
                return;
            }

            var all = service.Database.GetSessions(Game.Id).OrderByDescending(a => a.Start).ToList();
            var shown = all.Take(shownSessions).ToList();
            var longest = Math.Max(1, shown.Count == 0 ? 1 : shown.Max(a => a.Duration));
            Sessions = shown.Select(a => new GamePageSession
            {
                DateText = $"{HomeViewModel.FormatLastPlayed(a.Start)}, {a.Start.ToString("t", CultureInfo.CurrentCulture)}",
                DurationText = GameroomService.FormatDuration(a.Duration),
                SourceText = ResourceProvider.GetString("LOCGameroomSessionSource" + a.Source),
                BarWidth = Math.Max(6, maxSessionBar * a.Duration / longest)
            }).ToList();

            ActivitySummary = all.Count == 0
                ? ResourceProvider.GetString("LOCGameroomGameNoSessions")
                : string.Format(ResourceProvider.GetString("LOCGameroomGameActivitySummary"),
                    HomeViewModel.FormatCount(all.Count, "LOCGameroomHomeSessions"),
                    GameroomService.FormatDuration((ulong)(all.Sum(a => (long)a.Duration) / all.Count)));
        }

        private void LoadSaves()
        {
            if (service == null)
            {
                return;
            }

            var backups = service.Saves.GetBackups(Game);
            var latest = backups.FirstOrDefault();
            SavesText = latest == null
                ? ResourceProvider.GetString("LOCGameroomGameNoBackups")
                : string.Format(ResourceProvider.GetString("LOCGameroomGameLastBackup"), LowerFirst(FormatRelative(latest.Created)));
            SavesShortText = latest == null ? "—" : FormatRelative(latest.Created);
            SavesDetail = latest == null ? null : HomeViewModel.FormatCount(backups.Count, "LOCGameroomCountBackups");
            IsInCloud = backups.Any(a => !a.CloudObjectPath.IsNullOrEmpty());
            CloudText = ResourceProvider.GetString(IsInCloud
                ? "LOCGameroomGameInCloud"
                : service.Cloud.IsSignedIn ? "LOCGameroomGameNotInCloudYet" : "LOCGameroomGameCloudSignedOut");
        }

        private void SetHero(string path)
        {
            HeroIsCover = path != null && !Game.CoverImage.IsNullOrWhiteSpace() &&
                string.Equals(path, model.Database.GetFullFilePath(Game.CoverImage), StringComparison.OrdinalIgnoreCase);
            HeroImage = path;
        }

        private async void LoadArtwork()
        {
            if (service == null)
            {
                return;
            }

            var current = Game;
            SetHero(service.Media.GetHeroPath(current, false));
            LogoPath = service.Media.GetLogoPath(current, false);
            try
            {
                var (hero, logo, color) = await Task.Run(() =>
                {
                    // Finds and remembers the Steam app of games from other stores first.
                    service.Achievements.GetAppId(current, true);
                    var heroPath = service.Media.GetHeroPath(current);
                    var logoFile = service.Media.GetLogoPath(current);
                    var tint = Ambient.FromImage(heroPath) ??
                        (current.CoverImage.IsNullOrWhiteSpace() ? null : Ambient.FromImage(model.Database.GetFullFilePath(current.CoverImage)));
                    return (heroPath, logoFile, tint);
                });

                SetHero(hero);
                LogoPath = logo;
                if (color != null)
                {
                    AmbientColor = color.Value;
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to load artwork of {current.Name}");
            }
        }

        private async void LoadAchievements()
        {
            if (service == null)
            {
                return;
            }

            var current = Game;
            try
            {
                var (achievements, recent, rarest) = await Task.Run(() =>
                {
                    var appId = service.Achievements.GetAppId(current, true);
                    var loaded = appId == null ? null : service.Achievements.GetGameAchievements(appId, false);
                    var unlocked = loaded?.Items.Where(a => a.Unlocked).ToList() ?? new List<GameAchievement>();
                    var recentIcons = unlocked
                        .OrderByDescending(a => a.UnlockTime ?? DateTime.MinValue)
                        .Take(shownAchievements)
                        .Select(a => new GamePageAchievement
                        {
                            IconPath = service.Achievements.GetIconPath(appId, a.Definition.Icon),
                            Tooltip = a.UnlockTime == null ? a.Definition.Name : $"{a.Definition.Name}\n{a.UnlockTime.Value.ToString("g", CultureInfo.CurrentCulture)}"
                        }).ToList();
                    // Only interesting once there are a few to compare.
                    var rarestUnlocked = unlocked.Count < 3 ? null : unlocked.Where(a => a.Definition.GlobalPercent != null).OrderBy(a => a.Definition.GlobalPercent).FirstOrDefault();
                    if (rarestUnlocked != null)
                    {
                        rarestUnlocked.IconPath = service.Achievements.GetIconPath(appId, rarestUnlocked.Definition.Icon);
                    }

                    return (loaded, recentIcons, rarestUnlocked);
                });

                HasAchievements = achievements?.Total > 0;
                if (!HasAchievements)
                {
                    AchievementsText = "—";
                    return;
                }

                AchievementsText = $"{achievements.UnlockedCount} / {achievements.Total}";
                AchievementsPercent = achievements.Percent;
                AchievementsSummary = string.Format(ResourceProvider.GetString("LOCGameroomAchievementsSummary"), achievements.UnlockedCount, achievements.Total) +
                    $"  ·  {Math.Floor(achievements.Percent).ToString(CultureInfo.CurrentCulture)} %";
                RecentAchievements = recent;
                RarestIcon = rarest?.IconPath;
                RarestText = rarest == null
                    ? null
                    : string.Format(ResourceProvider.GetString("LOCGameroomGameRarest"), rarest.Definition.Name,
                        rarest.Definition.GlobalPercent.Value.ToString("0.#", CultureInfo.CurrentCulture));
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to load achievements of {current.Name}");
            }
        }
    }
}
