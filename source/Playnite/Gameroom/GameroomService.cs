using Playnite.Gameroom.Achievements;
using Playnite.Gameroom.Cloud;
using Playnite.Gameroom.InstalledGames;
using Playnite.Gameroom.Media;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Entry point of Gameroom specific features. Created once the library is open
    /// and shared by Desktop and Fullscreen mode.
    /// </summary>
    public class GameroomService : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        // Playtime of games started from Gameroom is updated this often while they run.
        private const int liveCreditSeconds = 60;
        private readonly PlayniteApplication app;
        private readonly Dictionary<Guid, LaunchedSession> launchedSessions = new Dictionary<Guid, LaunchedSession>();
        private readonly System.Threading.Timer liveCreditTimer;
        private readonly System.Threading.Timer cloudSyncTimer;
        private const int cloudSyncMinutes = 30;
        private const int installedGamesMinutes = 20;
        private readonly System.Threading.Timer installedGamesTimer;
        // Games whose saves were already reported as backed up during the current session.
        private readonly HashSet<Guid> liveBackupNotified = new HashSet<Guid>();
        private readonly HashSet<Guid> restoreOffered = new HashSet<Guid>();

        private class LaunchedSession
        {
            public DateTime Start;
            public ulong CreditedSeconds;
            public bool Ended;
        }

        public static GameroomService Instance { get; private set; }

        public GameroomDatabase Database { get; }
        public GameActivityMonitor Monitor { get; }
        public SaveBackupService Saves { get; }
        public GameroomCloud Cloud { get; }
        public CloudSaveSync CloudSync { get; }
        public AchievementService Achievements { get; }
        public GameMediaService Media { get; }
        public InstalledGameImporter InstalledGames { get; }
        public GameroomSettings Settings => app.AppSettings.Gameroom;

        private GameroomService(PlayniteApplication app)
        {
            this.app = app;
            Database = new GameroomDatabase(app.Database.DatabasePath);
            Monitor = new GameActivityMonitor(app.Database, Database, () => app.AppSettings.Gameroom);
            Monitor.SessionStarted += Monitor_SessionStarted;
            Monitor.SessionEnded += Monitor_SessionEnded;
            Monitor.UnknownGameDetected += Monitor_UnknownGameDetected;
            Saves = new SaveBackupService(app.Database, Database, () => app.AppSettings.Gameroom);
            Saves.BackupCompleted += Saves_BackupCompleted;
            app.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;
            app.Database.Games.ItemUpdated += Games_ItemUpdated;
            liveCreditTimer = new System.Threading.Timer(_ => CreditLaunchedSessions(), null, liveCreditSeconds * 1000, liveCreditSeconds * 1000);
            Cloud = new GameroomCloud(PlaynitePaths.ConfigRootPath, Settings.CloudServerUrl, Settings.CloudServerKey);
            CloudSync = new CloudSaveSync(Cloud, Saves, Database, app.Database, () => app.AppSettings.Gameroom);
            CloudSync.SyncCompleted += CloudSync_SyncCompleted;
            var syncPeriod = TimeSpan.FromMinutes(cloudSyncMinutes);
            cloudSyncTimer = new System.Threading.Timer(_ => Task.Run(CloudSync.SyncAsync), null, syncPeriod, syncPeriod);
            Achievements = new AchievementService(app.Database, Database, app.Database.DatabasePath, () => app.AppSettings.Language);
            Achievements.AchievementsUnlocked += Achievements_AchievementsUnlocked;
            Media = new GameMediaService(app.Database, app.Database.DatabasePath, Achievements.GetKnownAppId);
            InstalledGames = new InstalledGameImporter(app.Database, Database, new UnknownGameDetector(),
                () => app.Extensions?.LibraryPlugins.Select(a => a.Id).ToList() ?? new List<Guid>());
            var installedPeriod = TimeSpan.FromMinutes(installedGamesMinutes);
            installedGamesTimer = new System.Threading.Timer(_ =>
            {
                if (Settings.DetectInstalledGames)
                {
                    ScanInstalledGames(false);
                }
            }, null, installedPeriod, installedPeriod);
        }

        public static void Start(PlayniteApplication app)
        {
            if (Instance != null || app?.Database?.IsOpen != true)
            {
                return;
            }

            try
            {
                Instance = new GameroomService(app);
                Instance.ApplyVisualDefaults();
                Instance.Monitor.Start();
                Instance.Achievements.Start();
                Task.Run(() => Instance?.RunStartupChecks());
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to start Gameroom services.");
                Instance?.Dispose();
                Instance = null;
            }
        }

        /// <summary>
        /// Settings imported from Playnite keep its defaults, the ones the Gameroom look depends on are set once.
        /// </summary>
        private void ApplyVisualDefaults()
        {
            if (Settings.VisualDefaultsVersion >= 1)
            {
                return;
            }

            // Background images cross fade when another game is selected instead of switching abruptly.
            app.AppSettings.BackgroundImageAnimation = true;
            Settings.VisualDefaultsVersion = 1;
            app.AppSettings.SaveSettings();
        }

        public static void Shutdown()
        {
            Instance?.Dispose();
            Instance = null;
        }

        public void Dispose()
        {
            liveCreditTimer.Dispose();
            cloudSyncTimer.Dispose();
            installedGamesTimer.Dispose();
            Achievements.AchievementsUnlocked -= Achievements_AchievementsUnlocked;
            Achievements.Dispose();
            CloudSync.SyncCompleted -= CloudSync_SyncCompleted;
            CloudSync.Dispose();
            Cloud.Dispose();
            Saves.BackupCompleted -= Saves_BackupCompleted;
            app.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
            app.Database.Games.ItemUpdated -= Games_ItemUpdated;
            Monitor.SessionStarted -= Monitor_SessionStarted;
            Monitor.SessionEnded -= Monitor_SessionEnded;
            Monitor.UnknownGameDetected -= Monitor_UnknownGameDetected;
            Monitor.Dispose();
            Saves.Dispose();
            Database.Dispose();
        }

        #region Sessions started from Gameroom

        public void OnLaunchedSessionStarted(Game game)
        {
            lock (launchedSessions)
            {
                launchedSessions[game.Id] = new LaunchedSession { Start = DateTime.Now };
            }

            Saves.OnSessionStarted(game);
            Task.Run(() => OfferRestoreIfMissing(game));
            Task.Run(() => Achievements.OnSessionStarted(game));
        }

        /// <summary>
        /// Adds playtime of games started from Gameroom while they run, so it doesn't jump only when the game exits.
        /// </summary>
        private void CreditLaunchedSessions()
        {
            var updated = new List<Game>();
            try
            {
                lock (launchedSessions)
                {
                    var now = DateTime.Now;
                    foreach (var pair in launchedSessions.Where(a => !a.Value.Ended))
                    {
                        var elapsed = (ulong)Math.Max(0, (now - pair.Value.Start).TotalSeconds);
                        if (elapsed < pair.Value.CreditedSeconds + liveCreditSeconds)
                        {
                            continue;
                        }

                        var game = app.Database.Games.Get(pair.Key);
                        if (game == null)
                        {
                            continue;
                        }

                        game.Playtime += elapsed - pair.Value.CreditedSeconds;
                        pair.Value.CreditedSeconds = elapsed;
                        updated.Add(game);
                    }
                }

                updated.ForEach(a => app.Database.Games.Update(a));
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to update playtime of running games.");
            }
        }

        /// <summary>
        /// Called by the play controller when a session started from Gameroom ends.
        /// Returns the part of the session that wasn't added to playtime while the game was running.
        /// </summary>
        public ulong TakeUncreditedPlaytime(Guid gameId, ulong sessionLength)
        {
            lock (launchedSessions)
            {
                if (!launchedSessions.TryGetValue(gameId, out var session) || session.Ended)
                {
                    return sessionLength;
                }

                session.Ended = true;
                return sessionLength > session.CreditedSeconds ? sessionLength - session.CreditedSeconds : 0;
            }
        }

        public void OnLaunchedSessionEnded(Game game, ulong sessionLength)
        {
            Monitor.NotifyLaunchedSessionEnded(game.Id);
            Achievements.OnSessionEnded(game);
            DateTime start;
            lock (launchedSessions)
            {
                start = launchedSessions.TryGetValue(game.Id, out var launched) ? launched.Start : DateTime.Now.AddSeconds(-(double)sessionLength);
                launchedSessions.Remove(game.Id);
            }

            if (sessionLength == 0)
            {
                // Game failed to start or tracking was cancelled right away.
                Saves.OnSessionCancelled(game);
                return;
            }

            var session = new GameSession
            {
                GameId = game.Id,
                GameName = game.Name,
                Start = start,
                End = DateTime.Now,
                Duration = sessionLength,
                Source = SessionSource.Launched
            };

            try
            {
                Database.AddSession(session);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to store session of {game.Name}");
            }

            lock (liveBackupNotified)
            {
                liveBackupNotified.Remove(game.Id);
            }

            Saves.OnSessionEnded(game);
        }

        #endregion Sessions started from Gameroom

        #region Saves and metadata

        private void Saves_BackupCompleted(object sender, SaveBackupResult result)
        {
            if (result.Trigger != SaveBackupTrigger.WhilePlaying || !result.Success || result.Game == null)
            {
                return;
            }

            lock (liveBackupNotified)
            {
                if (!liveBackupNotified.Add(result.Game.Id))
                {
                    return;
                }
            }

            // Shown once per session, so it's clear saving in the game is also backed up.
            app.Notifications.Add(new NotificationMessage(
                $"gameroom_live_backup_{result.Game.Id}",
                string.Format(ResourceProvider.GetString("LOCGameroomSavesLiveBackup"), result.Game.Name),
                NotificationType.Info));
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            foreach (var game in e.AddedItems ?? new List<Game>())
            {
                // Give imports and the detector a moment to finish setting up the game.
                Task.Delay(5000).ContinueWith(_ => OfferRestoreIfMissing(game));
            }

            if (e.RemovedItems?.Count > 0)
            {
                InstalledGames.OnGamesRemoved(e.RemovedItems);
            }
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            foreach (var update in e.UpdatedItems)
            {
                if (!update.OldData.IsInstalled && update.NewData.IsInstalled)
                {
                    var game = update.NewData;
                    Task.Delay(5000).ContinueWith(_ => OfferRestoreIfMissing(game));
                }
            }
        }

        private void CloudSync_SyncCompleted(object sender, int downloaded)
        {
            if (downloaded == 0)
            {
                return;
            }

            // Backups from another PC may be the only copy of saves of games installed here.
            foreach (var game in app.Database.Games.Where(a => a.IsInstalled).ToList())
            {
                OfferRestoreIfMissing(game);
            }
        }

        private void RunStartupChecks()
        {
            try
            {
                Saves.ImportExistingBackups();
                if (Cloud.IsSignedIn)
                {
                    CloudSync.SyncAsync().Wait();
                }

                foreach (var game in app.Database.Games.Where(a => a.IsInstalled).ToList())
                {
                    OfferRestoreIfMissing(game);
                }

                // Detected games get artwork without needing a metadata extension.
                foreach (var game in app.Database.Games.Where(a => a.CoverImage.IsNullOrEmpty()).ToList())
                {
                    var data = Database.GetGameData(game.Id);
                    if (data?.KnownExecutables.HasItems() == true && !data.SteamMetadataFetched)
                    {
                        DownloadSteamMetadata(game);
                    }
                }

                if (Settings.DetectInstalledGames)
                {
                    ScanInstalledGames(false);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Gameroom startup checks failed.");
            }
        }

        /// <summary>
        /// Connects accounts and cloud saves to the user's Supabase project, an empty address disconnects it.
        /// </summary>
        public void ConfigureCloudServer(string url, string publishableKey)
        {
            Settings.CloudServerUrl = GameroomCloud.NormalizeServerUrl(url);
            Settings.CloudServerKey = Settings.CloudServerUrl == null ? null : publishableKey?.Trim();
            app.AppSettings.SaveSettings();
            Cloud.Configure(Settings.CloudServerUrl, Settings.CloudServerKey);
        }

        /// <summary>
        /// Adds the games installed on this PC that aren't in the library yet. <paramref name="recheck"/> also looks
        /// again at applications found not to be games.
        /// </summary>
        public InstalledGamesResult ScanInstalledGames(bool recheck)
        {
            InstalledGamesResult result;
            try
            {
                result = InstalledGames.Import(recheck);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to look for installed games.");
                return new InstalledGamesResult();
            }

            if (result.Added.Count > 0)
            {
                var names = result.Added.Select(a => a.Name).ToList();
                var shown = string.Join(", ", names.Take(3));
                if (names.Count > 3)
                {
                    shown = string.Format(ResourceProvider.GetString("LOCGameroomInstalledGamesMore"), shown, names.Count - 3);
                }

                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_installed_games_{DateTime.Now.Ticks}",
                    string.Format(ResourceProvider.GetString(names.Count == 1 ? "LOCGameroomInstalledGamesAddedOne" : "LOCGameroomInstalledGamesAdded"), names.Count, shown),
                    NotificationType.Info));

                // Covers and details from Steam, one game after another.
                Task.Run(() =>
                {
                    foreach (var game in result.Added)
                    {
                        DownloadSteamMetadata(game);
                    }

                    DownloadMissingMetadata(result.Added);
                });
            }

            return result;
        }

        /// <summary>
        /// Games that aren't on Steam get their artwork from the metadata sources set up in the settings, e.g. IGDB.
        /// </summary>
        private void DownloadMissingMetadata(List<Game> games)
        {
            var missing = games.Select(a => app.Database.Games.Get(a.Id)).Where(a => a != null && a.CoverImage.IsNullOrEmpty()).ToList();
            if (missing.Count == 0 || app.Extensions?.MetadataPlugins.HasItems() != true)
            {
                return;
            }

            try
            {
                using (var downloader = new Metadata.MetadataDownloader(app.Database, app.Extensions.MetadataPlugins, app.Extensions.LibraryPlugins))
                {
                    downloader.DownloadMetadataAsync(missing, app.AppSettings.MetadataSettings, app.AppSettings, null, CancellationToken.None).Wait();
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to download metadata of installed games.");
            }
        }

        /// <summary>
        /// Offers to restore saves when a game has backups but no saves on this PC, e.g. after reinstalling it.
        /// </summary>
        public void OfferRestoreIfMissing(Game game, bool reinstalled = false)
        {
            try
            {
                lock (restoreOffered)
                {
                    if (restoreOffered.Contains(game.Id))
                    {
                        return;
                    }
                }

                var backup = Saves.GetRestoreCandidate(game, reinstalled);
                if (backup == null)
                {
                    return;
                }

                lock (restoreOffered)
                {
                    if (!restoreOffered.Add(game.Id))
                    {
                        return;
                    }
                }

                logger.Info($"Offering restore of {game.Name} saves from {backup.ArchivePath}");
                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_restore_{game.Id}",
                    string.Format(ResourceProvider.GetString("LOCGameroomRestoreOffer"), game.Name, backup.Created.ToString("g")),
                    NotificationType.Info,
                    () => AskRestore(game, backup)));
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to check saves of {game.Name}");
            }
        }

        private void AskRestore(Game game, SaveBackup backup)
        {
            var restoreOption = new MessageBoxOption(ResourceProvider.GetString("LOCGameroomSavesRestore"), true, false);
            var neverOption = new MessageBoxOption(ResourceProvider.GetString("LOCGameroomRestoreNever"), false, false);
            var cancelOption = new MessageBoxOption(ResourceProvider.GetString("LOCCancelLabel"), false, true);
            var result = app.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCGameroomSavesRestoreConfirm"), game.Name, backup.Created.ToString("g")),
                ResourceProvider.GetString("LOCGameroomRestoreOfferTitle"),
                MessageBoxImage.Question,
                new List<MessageBoxOption> { restoreOption, neverOption, cancelOption });

            app.Notifications.Remove($"gameroom_restore_{game.Id}");
            if (result == restoreOption)
            {
                SaveBackupResult restoreResult = null;
                app.Dialogs.ActivateGlobalProgress(_ =>
                {
                    restoreResult = Saves.RestoreBackup(game, backup);
                }, new GlobalProgressOptions("LOCGameroomSavesRestoreInProgress"));

                if (restoreResult?.Success == true)
                {
                    app.Dialogs.ShowMessage(restoreResult.Message, game.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    app.Dialogs.ShowErrorMessage(restoreResult?.Message, game.Name);
                    lock (restoreOffered)
                    {
                        restoreOffered.Remove(game.Id);
                    }
                }
            }
            else if (result == neverOption)
            {
                var data = Database.GetOrCreateGameData(game.Id);
                data.RestoreOfferDeclined = true;
                Database.SaveGameData(data);
            }
            else
            {
                lock (restoreOffered)
                {
                    restoreOffered.Remove(game.Id);
                }
            }
        }

        /// <summary>
        /// Fills missing cover, background, icon and details of a game from the Steam store.
        /// </summary>
        public bool DownloadSteamMetadata(Game game)
        {
            try
            {
                return new SteamStoreMetadata().Apply(app.Database, Database, game, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to download Steam store data of {game.Name}");
                return false;
            }
        }

        #endregion Saves and metadata

        #region Achievements

        private void Achievements_AchievementsUnlocked(object sender, AchievementsUnlockedEventArgs e)
        {
            var game = Achievements.FindGameByAppId(e.AppId);
            var schema = Achievements.GetSchema(e.AppId);
            var toasts = e.Unlocked.Select(unlock =>
            {
                var definition = schema?.Achievements.FirstOrDefault(a => a.ApiName == unlock.ApiName);
                return new AchievementToast
                {
                    Name = definition?.Name ?? unlock.ApiName,
                    Description = definition?.Description,
                    GameName = game?.Name,
                    IconPath = Achievements.GetIconPath(e.AppId, definition?.Icon),
                    GlobalPercent = definition?.GlobalPercent
                };
            }).ToList();

            // The Steam client shows its own popup.
            if (Settings.AchievementOverlayEnabled && (e.Source != AchievementSource.Steam || Settings.AchievementOverlayForSteam))
            {
                AchievementOverlay.Show(toasts, Settings.AchievementSoundEnabled);
            }

            foreach (var toast in toasts)
            {
                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_achievement_{e.AppId}_{toast.Name}",
                    toast.GameName.IsNullOrWhiteSpace()
                        ? string.Format(ResourceProvider.GetString("LOCGameroomAchievementNotificationNoGame"), toast.Name)
                        : string.Format(ResourceProvider.GetString("LOCGameroomAchievementNotification"), toast.GameName, toast.Name),
                    NotificationType.Info,
                    game == null ? (Action)null : () => app.ProcessUriRequest(new PlayniteUriEventArgs
                    {
                        Arguments = new[] { UriCommands.Achievements, game.Id.ToString() }
                    })));
            }
        }

        /// <summary>
        /// Shows the overlay with the latest unlocked achievement of the library, or an example.
        /// </summary>
        public void ShowTestAchievement()
        {
            Task.Run(() =>
            {
                AchievementToast toast;
                try
                {
                    var recent = Achievements.GetRecentAchievements(1, out _).FirstOrDefault();
                    toast = recent == null ? null : new AchievementToast
                    {
                        Name = recent.Achievement.Definition.Name,
                        Description = recent.Achievement.Definition.Description,
                        GameName = recent.GameName,
                        IconPath = recent.Achievement.IconPath,
                        GlobalPercent = recent.Achievement.Definition.GlobalPercent
                    };
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Failed to load recent achievements.");
                    toast = null;
                }

                AchievementOverlay.Show(new[]
                {
                    toast ?? new AchievementToast
                    {
                        Name = ResourceProvider.GetString("LOCGameroomAchievementTestName"),
                        Description = ResourceProvider.GetString("LOCGameroomAchievementTestDescription"),
                        GameName = "Gameroom",
                        GlobalPercent = 4.2
                    }
                }, Settings.AchievementSoundEnabled);
            });
        }

        #endregion Achievements

        #region Detected sessions

        private void Monitor_SessionStarted(object sender, TrackedSession session)
        {
            var game = app.Database.Games.Get(session.GameId);
            if (game == null)
            {
                return;
            }

            Saves.OnSessionStarted(game);
            OfferRestoreIfMissing(game);
            Achievements.OnSessionStarted(game);
            if (Settings.NotifyDetectedSessions)
            {
                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_session_{game.Id}",
                    string.Format(ResourceProvider.GetString("LOCGameroomDetectedSessionStarted"), game.Name),
                    NotificationType.Info));
            }
        }

        private void Monitor_SessionEnded(object sender, GameSession session)
        {
            app.Notifications.Remove($"gameroom_session_{session.GameId}");
            lock (liveBackupNotified)
            {
                liveBackupNotified.Remove(session.GameId);
            }

            var game = app.Database.Games.Get(session.GameId);
            if (game != null)
            {
                Saves.OnSessionEnded(game);
                Achievements.OnSessionEnded(game);
            }

            if (Settings.NotifyDetectedSessions)
            {
                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_session_end_{session.Id}",
                    string.Format(ResourceProvider.GetString("LOCGameroomDetectedSessionEnded"), session.GameName, FormatDuration(session.Duration)),
                    NotificationType.Info));
            }
        }

        private void Monitor_UnknownGameDetected(object sender, UnknownGameSession session)
        {
            if (Settings.AutoAddUnknownGames)
            {
                AddUnknownGame(session);
                return;
            }

            var info = session.Info;
            app.Notifications.Add(new NotificationMessage(
                $"gameroom_unknown_{info.Executable}",
                string.Format(ResourceProvider.GetString("LOCGameroomUnknownGameDetected"), info.SuggestedName, info.Executable),
                NotificationType.Info,
                () => AskAddUnknownGame(session)));
        }

        private void AskAddUnknownGame(UnknownGameSession session)
        {
            var info = session.Info;
            var addOption = new MessageBoxOption(ResourceProvider.GetString("LOCGameroomAddToLibrary"), true, false);
            var ignoreOption = new MessageBoxOption(ResourceProvider.GetString("LOCGameroomNotAGame"), false, false);
            var cancelOption = new MessageBoxOption(ResourceProvider.GetString("LOCCancelLabel"), false, true);
            var result = app.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCGameroomUnknownGameQuestion"), info.SuggestedName, info.Executable, info.Engine ?? "-"),
                ResourceProvider.GetString("LOCGameroomUnknownGameTitle"),
                MessageBoxImage.Question,
                new List<MessageBoxOption> { addOption, ignoreOption, cancelOption });

            app.Notifications.Remove($"gameroom_unknown_{info.Executable}");
            if (result == addOption)
            {
                var game = AddUnknownGame(session);
                if (game != null)
                {
                    app.MainModelBase?.SelectGame(game.Id);
                }
            }
            else if (result == ignoreOption)
            {
                Monitor.IgnoreExecutable(info.Executable);
                app.AppSettings.SaveSettings();
            }
        }

        /// <summary>
        /// Adds a detected game to the library and keeps tracking its current session.
        /// </summary>
        public Game AddUnknownGame(UnknownGameSession session)
        {
            var info = session.Info;
            try
            {
                var existing = app.Database.Games.FirstOrDefault(a =>
                    !a.InstallDirectory.IsNullOrWhiteSpace() &&
                    string.Equals(GameMatcher.NormalizePath(a.InstallDirectory), GameMatcher.NormalizePath(info.RootDirectory), StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    Monitor.AdoptUnknownSession(info.Executable, existing);
                    return existing;
                }

                var exeRelative = info.Executable.StartsWith(info.RootDirectory, StringComparison.OrdinalIgnoreCase)
                    ? ExpandableVariables.InstallationDirectory + info.Executable.Substring(info.RootDirectory.TrimEnd('\\').Length)
                    : info.Executable;

                // Same game installed again in another folder: keep its playtime, sessions and backups.
                var reinstalled = FindMovedGame(info.SuggestedName);
                if (reinstalled != null)
                {
                    logger.Info($"{reinstalled.Name} was reinstalled to {info.RootDirectory}");
                    reinstalled.InstallDirectory = info.RootDirectory;
                    reinstalled.IsInstalled = true;
                    var action = reinstalled.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File);
                    if (action != null)
                    {
                        action.Path = exeRelative;
                        action.WorkingDir = Path.GetDirectoryName(exeRelative);
                    }

                    app.Database.Games.Update(reinstalled);
                    Monitor.AdoptUnknownSession(info.Executable, reinstalled);
                    Task.Run(() => OfferRestoreIfMissing(reinstalled, true));
                    return reinstalled;
                }
                var game = new Game(info.SuggestedName)
                {
                    InstallDirectory = info.RootDirectory,
                    IsInstalled = true,
                    Added = DateTime.Now,
                    GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
                    {
                        new GameAction
                        {
                            Name = ResourceProvider.GetString("LOCPlayGame"),
                            Type = GameActionType.File,
                            Path = exeRelative,
                            WorkingDir = Path.GetDirectoryName(exeRelative),
                            IsPlayAction = true
                        }
                    }
                };

                app.Database.Games.Add(game);
                if (!info.SteamAppId.IsNullOrWhiteSpace())
                {
                    var data = Database.GetOrCreateGameData(game.Id);
                    data.SteamAppId = info.SteamAppId;
                    Database.SaveGameData(data);
                }

                Monitor.AdoptUnknownSession(info.Executable, game);
                logger.Info($"Added detected game {game.Name} from {info.Executable}");
                Task.Run(() => DownloadSteamMetadata(game));
                app.Notifications.Add(new NotificationMessage(
                    $"gameroom_added_{game.Id}",
                    string.Format(ResourceProvider.GetString("LOCGameroomUnknownGameAdded"), game.Name),
                    NotificationType.Info,
                    () => app.MainModelBase?.SelectGame(game.Id)));
                return game;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to add detected game {info.Executable}");
                return null;
            }
        }

        /// <summary>
        /// Library game with the same name whose installation folder no longer exists.
        /// </summary>
        private Game FindMovedGame(string name)
        {
            var normalized = SavePaths.NormalizeName(name);
            if (normalized.Length == 0)
            {
                return null;
            }

            return app.Database.Games.FirstOrDefault(a =>
            {
                if (SavePaths.NormalizeName(a.Name) != normalized)
                {
                    return false;
                }

                var installDir = SaveLocationResolver.GetInstallDir(a);
                return installDir == null || !Directory.Exists(installDir);
            });
        }

        #endregion Detected sessions

        public static string FormatDuration(ulong seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours} h {time.Minutes} min";
            }

            if (time.TotalMinutes >= 1)
            {
                return $"{time.Minutes} min";
            }

            return $"{time.Seconds} s";
        }
    }
}
