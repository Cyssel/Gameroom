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
using ArtworkColor = Playnite.Gameroom.Media.AmbientColor;

namespace Playnite.DesktopApp.ViewModels
{
    public class HomeGameItem : ObservableObject
    {
        public Game Game { get; private set; }
        public string Name => Game.Name;
        public string CoverImage => Game.CoverImage;

        /// <summary>Cover and background the item shows, to notice new artwork from a metadata download.</summary>
        public string ShownArtwork { get; private set; }
        private string shownName;

        public HomeGameItem(Game game)
        {
            Game = game;
            ShownArtwork = GetArtworkKey(game);
            shownName = game.Name;
        }

        public static string GetArtworkKey(Game game)
        {
            return game.CoverImage + "|" + game.BackgroundImage;
        }

        /// <summary>
        /// Points the item at the game's current data. Bindings are only refreshed for what changed, so covers
        /// don't blink.
        /// </summary>
        public void Update(Game game)
        {
            Game = game;
            if (shownName != game.Name)
            {
                shownName = game.Name;
                OnPropertyChanged(nameof(Name));
            }

            var artwork = GetArtworkKey(game);
            if (ShownArtwork != artwork)
            {
                ShownArtwork = artwork;
                ArtworkLoaded = false;
                OnPropertyChanged(nameof(CoverImage));
            }
        }

        private string subtitle;
        public string Subtitle
        {
            get => subtitle;
            set => SetValue(ref subtitle, value);
        }

        public RelayCommand PlayCommand { get; set; }
        public RelayCommand OpenCommand { get; set; }
        public RelayCommand SelectCommand { get; set; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set => SetValue(ref isSelected, value);
        }

        private string heroImage;
        /// <summary>Wide artwork, loaded in the background.</summary>
        public string HeroImage
        {
            get => heroImage;
            set => SetValue(ref heroImage, value);
        }

        /// <summary>The game has no wide artwork, <see cref="HeroImage"/> is its cover.</summary>
        public bool HeroIsCover { get; set; }

        private string logoPath;
        public string LogoPath
        {
            get => logoPath;
            set => SetValue(ref logoPath, value);
        }

        private Color? ambient;
        public Color? Ambient
        {
            get => ambient;
            set => SetValue(ref ambient, value);
        }

        public bool ArtworkLoaded { get; set; }
    }

    public class HomeStatTile
    {
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Value { get; set; }
        public string Detail { get; set; }
    }

    public class HomeAchievementItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconPath { get; set; }
        public string Subtitle { get; set; }
        public RelayCommand OpenCommand { get; set; }
    }

    /// <summary>Game whose achievements are almost all unlocked.</summary>
    public class HomeProgressItem
    {
        public string Name { get; set; }
        public string CoverImage { get; set; }
        public string ProgressText { get; set; }
        public double Percent { get; set; }
        public string PercentText { get; set; }
        public RelayCommand OpenCommand { get; set; }
    }

    public class HomeDayActivity
    {
        public string Label { get; set; }
        public string Tooltip { get; set; }
        /// <summary>Bar height in pixels.</summary>
        public double BarHeight { get; set; }
        public bool IsToday { get; set; }
    }

    /// <summary>
    /// Start page like a console home screen: carousel of recent games over their artwork, statistics and suggestions.
    /// </summary>
    public class HomeViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const double maxBarHeight = 110;
        private const int recentAchievements = 5;
        private const int carouselSize = 14;
        private const int almostDoneCount = 4;
        private static readonly Color defaultAmbient = Color.FromRgb(0x8B, 0x5C, 0xF6);
        private int loadVersion;
        private readonly DesktopAppViewModel model;
        private Dictionary<Guid, (int Unlocked, int Total)> achievementCounts = new Dictionary<Guid, (int, int)>();

        public HomeViewModel(DesktopAppViewModel model)
        {
            this.model = model;
            PlayHeroCommand = new RelayCommand(() => Play(SelectedItem?.Game), () => SelectedItem != null);
            OpenHeroCommand = new RelayCommand(() => model.OpenGamePage(SelectedItem?.Game), () => SelectedItem != null);
            HeroAchievementsCommand = new RelayCommand(() => OpenAchievements(SelectedItem.Game.Id), () => SelectedItem != null);
            OpenLibraryCommand = new RelayCommand(() => model.SwitchToLibraryView());
            UpdateClock();
        }

        public RelayCommand PlayHeroCommand { get; }
        public RelayCommand OpenHeroCommand { get; }
        public RelayCommand HeroAchievementsCommand { get; }
        public RelayCommand OpenLibraryCommand { get; }

        private bool hasGames;
        public bool HasGames
        {
            get => hasGames;
            private set => SetValue(ref hasGames, value);
        }

        private string greeting;
        public string Greeting
        {
            get => greeting;
            private set => SetValue(ref greeting, value);
        }

        private string clock;
        public string Clock
        {
            get => clock;
            private set => SetValue(ref clock, value);
        }

        private string dateText;
        public string DateText
        {
            get => dateText;
            private set => SetValue(ref dateText, value);
        }

        #region Carousel and hero

        private List<HomeGameItem> carousel = new List<HomeGameItem>();
        public List<HomeGameItem> Carousel
        {
            get => carousel;
            private set => SetValue(ref carousel, value);
        }

        private HomeGameItem selectedItem;
        public HomeGameItem SelectedItem
        {
            get => selectedItem;
            set
            {
                if (selectedItem == value)
                {
                    return;
                }

                if (selectedItem != null)
                {
                    selectedItem.IsSelected = false;
                    selectedItem.PropertyChanged -= SelectedItem_PropertyChanged;
                }

                SetValue(ref selectedItem, value);
                if (value != null)
                {
                    value.IsSelected = true;
                    value.PropertyChanged += SelectedItem_PropertyChanged;
                }

                UpdateHero();
            }
        }

        private void SelectedItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateHero();
        }

        private string heroName;
        public string HeroName
        {
            get => heroName;
            private set => SetValue(ref heroName, value);
        }

        private string heroImage;
        public string HeroImage
        {
            get => heroImage;
            private set => SetValue(ref heroImage, value);
        }

        /// <summary>Hero is the cover of a game without wide artwork, shown blurred.</summary>
        public bool HeroIsCover { get; private set; }

        private string heroLogo;
        public string HeroLogo
        {
            get => heroLogo;
            private set
            {
                SetValue(ref heroLogo, value);
                OnPropertyChanged(nameof(HasHeroLogo));
            }
        }

        public bool HasHeroLogo => !HeroLogo.IsNullOrWhiteSpace();

        private string heroLabel;
        public string HeroLabel
        {
            get => heroLabel;
            private set => SetValue(ref heroLabel, value);
        }

        private string heroMeta;
        public string HeroMeta
        {
            get => heroMeta;
            private set => SetValue(ref heroMeta, value);
        }

        private string heroAchievements;
        public string HeroAchievements
        {
            get => heroAchievements;
            private set => SetValue(ref heroAchievements, value);
        }

        private Color ambient = defaultAmbient;
        public Color Ambient
        {
            get => ambient;
            private set => SetValue(ref ambient, value);
        }

        private void UpdateHero()
        {
            var game = SelectedItem?.Game;
            if (game == null)
            {
                HeroName = null;
                HeroImage = null;
                HeroLogo = null;
                HeroLabel = null;
                HeroMeta = null;
                HeroAchievements = null;
                return;
            }

            HeroName = game.Name;
            HeroIsCover = SelectedItem.HeroImage == null ? game.BackgroundImage.IsNullOrWhiteSpace() : SelectedItem.HeroIsCover;
            HeroImage = SelectedItem.HeroImage ?? (game.BackgroundImage.IsNullOrWhiteSpace() ? game.CoverImage : game.BackgroundImage);
            HeroLogo = SelectedItem.LogoPath;
            Ambient = SelectedItem.Ambient ?? defaultAmbient;
            HeroLabel = ResourceProvider.GetString(game.IsRunning
                ? "LOCGameroomGameRunning"
                : game.LastActivity != null ? "LOCGameroomHomeContinue" : "LOCGameroomHomeNewInLibrary");
            HeroMeta = game.LastActivity != null
                ? $"{FormatLastPlayed(game.LastActivity)}  ·  {string.Format(ResourceProvider.GetString("LOCGameroomHomePlayedFor"), FormatPlaytime(game.Playtime))}"
                : FormatPlaytime(game.Playtime);
            HeroAchievements = achievementCounts.TryGetValue(game.Id, out var counts)
                ? $"{counts.Unlocked} / {counts.Total}"
                : null;
        }

        #endregion Carousel and hero

        private List<HomeStatTile> stats = new List<HomeStatTile>();
        public List<HomeStatTile> Stats
        {
            get => stats;
            private set => SetValue(ref stats, value);
        }

        private List<HomeDayActivity> weekActivity = new List<HomeDayActivity>();
        public List<HomeDayActivity> WeekActivity
        {
            get => weekActivity;
            private set => SetValue(ref weekActivity, value);
        }

        private string weekTotal;
        public string WeekTotal
        {
            get => weekTotal;
            private set => SetValue(ref weekTotal, value);
        }

        private List<HomeGameItem> recentlyAdded = new List<HomeGameItem>();
        public List<HomeGameItem> RecentlyAdded
        {
            get => recentlyAdded;
            private set
            {
                SetValue(ref recentlyAdded, value);
                OnPropertyChanged(nameof(HasRecentlyAdded));
            }
        }

        public bool HasRecentlyAdded => RecentlyAdded.Count > 0;

        private List<HomeGameItem> notPlayed = new List<HomeGameItem>();
        public List<HomeGameItem> NotPlayed
        {
            get => notPlayed;
            private set
            {
                SetValue(ref notPlayed, value);
                OnPropertyChanged(nameof(HasNotPlayed));
            }
        }

        public bool HasNotPlayed => NotPlayed.Count > 0;

        private List<HomeProgressItem> almostDone = new List<HomeProgressItem>();
        public List<HomeProgressItem> AlmostDone
        {
            get => almostDone;
            private set
            {
                SetValue(ref almostDone, value);
                OnPropertyChanged(nameof(HasAlmostDone));
            }
        }

        public bool HasAlmostDone => AlmostDone.Count > 0;

        private List<HomeAchievementItem> recentAchievementItems = new List<HomeAchievementItem>();
        public List<HomeAchievementItem> RecentAchievements
        {
            get => recentAchievementItems;
            private set
            {
                SetValue(ref recentAchievementItems, value);
                OnPropertyChanged(nameof(HasRecentAchievements));
            }
        }

        public bool HasRecentAchievements => RecentAchievements.Count > 0;

        private string achievementsTotal;
        public string AchievementsTotal
        {
            get => achievementsTotal;
            private set => SetValue(ref achievementsTotal, value);
        }

        public void UpdateClock()
        {
            var now = DateTime.Now;
            Clock = now.ToString("t", CultureInfo.CurrentCulture);
            var date = now.ToString("dddd, d MMMM", GameroomLocalization.Culture);
            DateText = date.Length > 0 ? char.ToUpper(date[0], GameroomLocalization.Culture) + date.Substring(1) : date;
            var hour = now.Hour;
            Greeting = ResourceProvider.GetString(hour >= 5 && hour < 12 ? "LOCGameroomHomeGoodMorning" : hour >= 12 && hour < 20 ? "LOCGameroomHomeGoodAfternoon" : "LOCGameroomHomeGoodEvening");
        }

        private void Play(Game game)
        {
            if (game == null)
            {
                return;
            }

            if (game.IsInstalled)
            {
                model.StartGameCommand.Execute(game);
            }
            else
            {
                model.InstallGameCommand.Execute(game);
            }
        }

        private void OpenAchievements(Guid gameId)
        {
            var game = model.Database.Games.Get(gameId);
            var service = GameroomService.Instance;
            if (game != null && service != null)
            {
                new GameAchievementsViewModel(new GameAchievementsWindowFactory(), service, game).OpenView();
            }
        }

        /// <summary>
        /// Keeps the current row when it shows the same games, so returning to the start page doesn't rebuild it
        /// and its covers don't blink while they load again.
        /// </summary>
        private List<HomeGameItem> Reuse(List<HomeGameItem> current, List<Game> games, Func<Game, string> subtitle, Action<HomeGameItem> created = null)
        {
            if (current.Count == games.Count && current.Select(a => a.Game.Id).SequenceEqual(games.Select(a => a.Id)))
            {
                for (var i = 0; i < games.Count; i++)
                {
                    current[i].Update(games[i]);
                    current[i].Subtitle = subtitle(games[i]);
                }

                return current;
            }

            return games.Select(game =>
            {
                var item = CreateItem(game, subtitle(game));
                created?.Invoke(item);
                return item;
            }).ToList();
        }

        private HomeGameItem CreateItem(Game game, string subtitle)
        {
            var item = new HomeGameItem(game)
            {
                Subtitle = subtitle,
                PlayCommand = new RelayCommand(() => Play(game)),
                OpenCommand = new RelayCommand(() => model.OpenGamePage(game))
            };

            item.SelectCommand = new RelayCommand(() => SelectedItem = item);
            return item;
        }

        /// <summary>
        /// Formats a count with the localized string, using the "One" variant of the key for a single item.
        /// </summary>
        public static string FormatCount(int count, string key)
        {
            return string.Format(ResourceProvider.GetString(count == 1 ? key + "One" : key), count);
        }

        public static string FormatPlaytime(ulong seconds)
        {
            if (seconds < 60)
            {
                return ResourceProvider.GetString("LOCGameroomHomeNotPlayed");
            }

            return GameroomService.FormatDuration(seconds);
        }

        public static string FormatLastPlayed(DateTime? date)
        {
            if (date == null)
            {
                return string.Empty;
            }

            var days = (DateTime.Today - date.Value.Date).Days;
            if (days <= 0)
            {
                return ResourceProvider.GetString("LOCGameroomHomeToday");
            }

            if (days == 1)
            {
                return ResourceProvider.GetString("LOCGameroomHomeYesterday");
            }

            if (days < 7)
            {
                return string.Format(ResourceProvider.GetString("LOCGameroomHomeDaysAgo"), days);
            }

            return date.Value.ToString("d MMM", GameroomLocalization.Culture);
        }

        public void Refresh()
        {
            var database = model.Database;
            if (database?.IsOpen != true)
            {
                return;
            }

            UpdateClock();
            var games = database.Games.Where(a => !a.Hidden).ToList();
            HasGames = games.Count > 0;

            // Recently played first, then what's new in the library.
            var played = games.Where(a => a.LastActivity != null).OrderByDescending(a => a.LastActivity).ToList();
            var previous = SelectedItem?.Game.Id;
            var carouselGames = played
                .Concat(games.Where(a => a.LastActivity == null).OrderByDescending(a => a.Added ?? DateTime.MinValue))
                .Take(carouselSize)
                .ToList();
            var oldItems = Carousel.ToDictionary(a => a.Game.Id);
            Carousel = Reuse(Carousel, carouselGames,
                game => game.LastActivity != null ? FormatLastPlayed(game.LastActivity) : ResourceProvider.GetString("LOCGameroomHomeNew"),
                item =>
                {
                    if (oldItems.TryGetValue(item.Game.Id, out var old) && old.ArtworkLoaded && old.ShownArtwork == item.ShownArtwork)
                    {
                        item.HeroIsCover = old.HeroIsCover;
                        item.HeroImage = old.HeroImage;
                        item.LogoPath = old.LogoPath;
                        item.Ambient = old.Ambient;
                        item.ArtworkLoaded = true;
                    }
                });
            SelectedItem = Carousel.FirstOrDefault(a => a.Game.Id == previous) ?? Carousel.FirstOrDefault();
            UpdateHero();

            var inCarousel = new HashSet<Guid>(carouselGames.Select(a => a.Id));
            NotPlayed = Reuse(NotPlayed, games.Where(a => a.IsInstalled && a.Playtime == 0 && !inCarousel.Contains(a.Id))
                .OrderByDescending(a => a.Added ?? DateTime.MinValue).Take(10).ToList(),
                _ => ResourceProvider.GetString("LOCGameroomHomeNotPlayed"));
            RecentlyAdded = Reuse(RecentlyAdded, games.Where(a => a.Added != null).OrderByDescending(a => a.Added).Take(10).ToList(),
                game => FormatPlaytime(game.Playtime));

            var service = GameroomService.Instance;
            var weekStart = DateTime.Today.AddDays(-6);
            var sessions = service?.Database.GetSessions(weekStart, DateTime.Today.AddDays(1)) ?? new List<GameSession>();
            var perDay = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Select(day => new
            {
                Day = day,
                Seconds = (ulong)sessions.Where(a => a.Start.Date == day).Sum(a => (long)a.Duration)
            }).ToList();
            var maxDay = Math.Max(1, perDay.Max(a => a.Seconds));
            WeekActivity = perDay.Select(a => new HomeDayActivity
            {
                Label = a.Day.ToString("ddd", GameroomLocalization.Culture).TrimEnd('.'),
                Tooltip = $"{a.Day:D}: {GameroomService.FormatDuration(a.Seconds)}",
                BarHeight = Math.Max(4, maxBarHeight * a.Seconds / maxDay),
                IsToday = a.Day == DateTime.Today
            }).ToList();

            var weekSeconds = (ulong)perDay.Sum(a => (long)a.Seconds);
            WeekTotal = GameroomService.FormatDuration(weekSeconds);
            var totalPlaytime = (ulong)games.Sum(a => (long)a.Playtime);
            var cloudSaves = service?.Database.GetAllBackups().Count(a => !a.CloudObjectPath.IsNullOrEmpty()) ?? 0;
            Stats = new List<HomeStatTile>
            {
                new HomeStatTile
                {
                    Icon = "",
                    Title = ResourceProvider.GetString("LOCGameroomHomeThisWeek"),
                    Value = GameroomService.FormatDuration(weekSeconds),
                    Detail = FormatCount(sessions.Count, "LOCGameroomHomeSessions")
                },
                new HomeStatTile
                {
                    Icon = "",
                    Title = ResourceProvider.GetString("LOCGameroomHomeTotalPlaytime"),
                    Value = GameroomService.FormatDuration(totalPlaytime),
                    Detail = FormatCount(games.Count(a => a.Playtime > 0), "LOCGameroomHomePlayedGames")
                },
                new HomeStatTile
                {
                    Icon = "",
                    Title = ResourceProvider.GetString("LOCGameroomHomeLibrary"),
                    Value = games.Count.ToString(),
                    Detail = FormatCount(games.Count(a => a.IsInstalled), "LOCGameroomHomeInstalled")
                },
                new HomeStatTile
                {
                    Icon = "",
                    Title = ResourceProvider.GetString("LOCGameroomHomeCloud"),
                    Value = service?.Cloud.IsSignedIn == true ? cloudSaves.ToString() : "—",
                    Detail = ResourceProvider.GetString(service?.Cloud.IsSignedIn == true ? "LOCGameroomHomeCloudBackups" : "LOCGameroomHomeCloudSignedOut")
                }
            };

            LoadInBackground(games);
        }

        /// <summary>
        /// Artwork and achievements need files and network, they fill in once loaded.
        /// </summary>
        private async void LoadInBackground(List<Game> games)
        {
            var service = GameroomService.Instance;
            if (service == null)
            {
                return;
            }

            var version = ++loadVersion;
            var items = Carousel.Where(a => !a.ArtworkLoaded).ToList();
            // The selected game is shown first.
            if (SelectedItem != null && items.Remove(SelectedItem))
            {
                items.Insert(0, SelectedItem);
            }

            foreach (var item in items)
            {
                try
                {
                    var (hero, logo, color) = await Task.Run(() =>
                    {
                        // Games from other stores are looked up on Steam once, for its artwork.
                        service.Achievements.GetAppId(item.Game, true);
                        var heroPath = service.Media.GetHeroPath(item.Game);
                        var logoFile = service.Media.GetLogoPath(item.Game);
                        var tint = ArtworkColor.FromImage(heroPath) ??
                            (item.Game.CoverImage.IsNullOrWhiteSpace() ? null : ArtworkColor.FromImage(model.Database.GetFullFilePath(item.Game.CoverImage)));
                        return (heroPath, logoFile, tint);
                    });

                    if (version != loadVersion)
                    {
                        return;
                    }

                    item.HeroIsCover = hero != null && !item.Game.CoverImage.IsNullOrWhiteSpace() &&
                        string.Equals(hero, model.Database.GetFullFilePath(item.Game.CoverImage), StringComparison.OrdinalIgnoreCase);
                    item.HeroImage = hero;
                    item.LogoPath = logo;
                    item.Ambient = color;
                    item.ArtworkLoaded = true;
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to load artwork of {item.Game.Name}");
                }
            }

            await LoadAchievements(service, games, version);
        }

        private async Task LoadAchievements(GameroomService service, List<Game> games, int version)
        {
            List<RecentAchievement> recent;
            Dictionary<Guid, (int, int)> counts;
            var total = 0;
            try
            {
                recent = await Task.Run(() => service.Achievements.GetRecentAchievements(recentAchievements, out total));
                counts = await Task.Run(() =>
                {
                    var result = new Dictionary<Guid, (int, int)>();
                    foreach (var game in games)
                    {
                        var appId = service.Achievements.GetKnownAppId(game);
                        // Only games with progress, so the whole Steam library isn't downloaded.
                        if (appId == null || !service.Achievements.GetProgress(appId).Values.Any(a => a.Unlocked))
                        {
                            continue;
                        }

                        var achievements = service.Achievements.GetGameAchievements(appId, false);
                        if (achievements?.Total > 0)
                        {
                            result[game.Id] = (achievements.UnlockedCount, achievements.Total);
                        }
                    }

                    return result;
                });
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to load achievements for the start page.");
                return;
            }

            if (version != loadVersion)
            {
                return;
            }

            achievementCounts = counts;
            UpdateHero();
            AchievementsTotal = FormatCount(total, "LOCGameroomHomeAchievementsTotal");
            var recentItems = recent.Select(a => new HomeAchievementItem
            {
                Name = a.Achievement.Definition.Name,
                Description = a.Achievement.Definition.Description,
                IconPath = a.Achievement.IconPath,
                Subtitle = $"{a.GameName}  ·  {FormatLastPlayed(a.Achievement.UnlockTime)}",
                OpenCommand = new RelayCommand(() => OpenAchievements(a.GameId))
            }).ToList();
            // Unchanged lists are kept so their icons don't reload.
            if (!recentItems.Select(a => a.Name + a.Subtitle).SequenceEqual(RecentAchievements.Select(a => a.Name + a.Subtitle)))
            {
                RecentAchievements = recentItems;
            }

            var almostDoneItems = games
                .Where(a => counts.ContainsKey(a.Id) && counts[a.Id].Item1 < counts[a.Id].Item2)
                .Select(a => new { Game = a, Unlocked = counts[a.Id].Item1, Total = counts[a.Id].Item2 })
                .OrderByDescending(a => (double)a.Unlocked / a.Total)
                .Take(almostDoneCount)
                .Select(a =>
                {
                    var percent = 100.0 * a.Unlocked / a.Total;
                    return new HomeProgressItem
                    {
                        Name = a.Game.Name,
                        CoverImage = a.Game.CoverImage,
                        ProgressText = string.Format(ResourceProvider.GetString("LOCGameroomHomeAlmostDoneProgress"), a.Unlocked, a.Total),
                        Percent = percent,
                        PercentText = $"{Math.Floor(percent).ToString(CultureInfo.CurrentCulture)} %",
                        OpenCommand = new RelayCommand(() => OpenAchievements(a.Game.Id))
                    };
                }).ToList();
            if (!almostDoneItems.Select(a => a.Name + a.ProgressText).SequenceEqual(AlmostDone.Select(a => a.Name + a.ProgressText)))
            {
                AlmostDone = almostDoneItems;
            }
        }
    }
}
