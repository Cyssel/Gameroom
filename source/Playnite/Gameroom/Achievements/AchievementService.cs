using Newtonsoft.Json.Linq;
using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace Playnite.Gameroom.Achievements
{
    /// <summary>
    /// Steam achievements of library games: names and icons come from the public Steam web API, unlocks from
    /// the Steam client cache and from Steam emulators. Watches those files to report unlocks while playing.
    /// </summary>
    public class AchievementService : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string iconHost = "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/";
        private static readonly TimeSpan schemaMaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan failedDownloadRetry = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan appSearchRetry = TimeSpan.FromDays(30);
        // Unlocks older than this are restored or synced files, not something that just happened in game.
        private static readonly TimeSpan announceMaxAge = TimeSpan.FromMinutes(15);
        private const int changeDelayMs = 800;

        private readonly IGameDatabaseMain database;
        private readonly GameroomDatabase gameroomDb;
        private readonly Func<string> getLanguage;
        private readonly string cacheDir;
        private readonly Dictionary<string, AchievementSchema> schemas = new Dictionary<string, AchievementSchema>();
        private readonly Dictionary<string, DateTime> failedDownloads = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, (DateTime WriteTime, Dictionary<string, AchievementProgress> Progress)> progressCache =
            new Dictionary<string, (DateTime, Dictionary<string, AchievementProgress>)>(StringComparer.OrdinalIgnoreCase);
        // Unlocked achievements per file, to find out what changed.
        private readonly Dictionary<string, HashSet<string>> knownUnlocks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Timer> pendingChecks = new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        // Achievement files of running games, checked often in case their folder didn't exist when watching started.
        private readonly Dictionary<string, (string AppId, Guid GameId, DateTime WriteTime)> polledFiles =
            new Dictionary<string, (string, Guid, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> watchedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Timer pollTimer;
        private Timer rootsTimer;
        private string steamStatsDir;
        private bool disposed;

        /// <summary>Raised from a background thread with achievements unlocked just now.</summary>
        public event EventHandler<AchievementsUnlockedEventArgs> AchievementsUnlocked;

        public AchievementService(IGameDatabaseMain database, GameroomDatabase gameroomDb, string libraryDir, Func<string> getLanguage)
        {
            this.database = database;
            this.gameroomDb = gameroomDb;
            this.getLanguage = getLanguage;
            cacheDir = Path.Combine(libraryDir, "achievements");
        }

        public void Start()
        {
            steamStatsDir = AchievementReaders.FindSteamStatsDirectory();
            Task.Run(() =>
            {
                WatchEmulatorRoots();
                if (steamStatsDir != null)
                {
                    BaselineSteamStats();
                    AddWatcher(steamStatsDir, "UserGameStats_*.bin", false);
                }
            });

            // Emulators create their folder with the first game that uses them.
            rootsTimer = new Timer(_ => WatchEmulatorRoots(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
            pollTimer = new Timer(_ => PollRunningGames(), null, 3000, 3000);
        }

        public void Dispose()
        {
            disposed = true;
            pollTimer?.Dispose();
            rootsTimer?.Dispose();
            lock (watchers)
            {
                watchers.ForEach(a => a.Dispose());
                watchers.Clear();
            }

            lock (pendingChecks)
            {
                pendingChecks.Values.ToList().ForEach(a => a.Dispose());
                pendingChecks.Clear();
            }
        }

        private string SteamLanguage => SteamStoreMetadata.GetSteamLanguage(getLanguage());

        #region App ids

        /// <summary>
        /// Steam app id known without searching: from the Steam library, game files or a previous search.
        /// </summary>
        public string GetKnownAppId(Game game)
        {
            var data = gameroomDb.GetGameData(game.Id);
            if (!data?.SteamAppId.IsNullOrWhiteSpace() == true)
            {
                return data.SteamAppId;
            }

            return game.PluginId == BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary) && !game.GameId.IsNullOrWhiteSpace()
                ? game.GameId
                : null;
        }

        /// <summary>
        /// Steam app id of the game. Games from other stores or without Steam files are searched on the Steam store by name.
        /// </summary>
        public string GetAppId(Game game, bool allowSearch)
        {
            var data = gameroomDb.GetGameData(game.Id);
            var appId = SaveLocationResolver.GetSteamAppId(game, data);
            if (!appId.IsNullOrWhiteSpace())
            {
                if (data?.SteamAppId.IsNullOrWhiteSpace() != false && game.PluginId != BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary))
                {
                    // Found in game files, which takes a while.
                    data = data ?? new GameroomGameData { Id = game.Id };
                    data.SteamAppId = appId;
                    gameroomDb.SaveGameData(data);
                }

                return appId;
            }

            if (!allowSearch || game.Name.IsNullOrWhiteSpace() ||
                (data?.AchievementAppSearched != null && DateTime.Now - data.AchievementAppSearched.Value < appSearchRetry))
            {
                return null;
            }

            string found;
            try
            {
                found = new SteamStoreMetadata().FindAppId(game.Name);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to search Steam app of {game.Name}");
                return null;
            }

            data = gameroomDb.GetGameData(game.Id) ?? new GameroomGameData { Id = game.Id };
            data.AchievementAppSearched = DateTime.Now;
            if (!found.IsNullOrWhiteSpace())
            {
                data.SteamAppId = found;
            }

            gameroomDb.SaveGameData(data);
            return found;
        }

        public Game FindGameByAppId(string appId)
        {
            return database.Games.FirstOrDefault(a => GetKnownAppId(a) == appId);
        }

        #endregion App ids

        #region Schema and icons

        /// <summary>
        /// Achievement names, descriptions and icons in the app language. Cached for a week.
        /// </summary>
        public AchievementSchema GetSchema(string appId, bool allowDownload = true)
        {
            if (appId.IsNullOrWhiteSpace() || !appId.All(char.IsDigit))
            {
                return null;
            }

            var language = SteamLanguage;
            var key = $"{appId}_{language}";
            lock (schemas)
            {
                if (schemas.TryGetValue(key, out var loaded) && (!allowDownload || DateTime.Now - loaded.Updated < schemaMaxAge))
                {
                    return loaded;
                }

                if (failedDownloads.TryGetValue(key, out var failed) && DateTime.Now - failed < failedDownloadRetry)
                {
                    allowDownload = false;
                }
            }

            var file = Path.Combine(cacheDir, "schemas", key + ".json");
            AchievementSchema cached = null;
            if (File.Exists(file))
            {
                try
                {
                    cached = Serialization.FromJsonFile<AchievementSchema>(file);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read achievement cache {file}");
                }
            }

            var schema = cached;
            if (allowDownload && (cached == null || DateTime.Now - cached.Updated >= schemaMaxAge))
            {
                try
                {
                    schema = DownloadSchema(appId, language);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, Serialization.ToJson(schema));
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to download achievements of Steam app {appId}");
                    lock (schemas)
                    {
                        failedDownloads[key] = DateTime.Now;
                    }
                }
            }

            schema = schema ?? ReadSteamClientSchema(appId, language);
            if (schema != null)
            {
                lock (schemas)
                {
                    schemas[key] = schema;
                }
            }

            return schema;
        }

        private static AchievementSchema DownloadSchema(string appId, string language)
        {
            var json = JObject.Parse(SteamStoreMetadata.Download(
                $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={appId}&language={language}"));
            var items = json["response"]?["achievements"] as JArray ?? new JArray();
            return new AchievementSchema
            {
                AppId = appId,
                Language = language,
                Updated = DateTime.Now,
                Achievements = items.OfType<JObject>()
                    .Where(a => !a["internal_name"]?.ToString().IsNullOrWhiteSpace() == true)
                    .Select(a => new AchievementDefinition
                    {
                        ApiName = a["internal_name"].ToString(),
                        Name = a["localized_name"]?.ToString(),
                        Description = a["localized_desc"]?.ToString(),
                        Hidden = a["hidden"]?.Type == JTokenType.Boolean && a["hidden"].Value<bool>(),
                        Icon = a["icon"]?.ToString(),
                        IconGray = a["icon_gray"]?.ToString(),
                        GlobalPercent = double.TryParse(a["player_percent_unlocked"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                            ? percent
                            : (double?)null
                    }).ToList()
            };
        }

        private AchievementSchema ReadSteamClientSchema(string appId, string language)
        {
            var file = steamStatsDir == null ? null : AchievementReaders.GetSteamSchemaFile(steamStatsDir, appId);
            if (file == null || !File.Exists(file))
            {
                return null;
            }

            try
            {
                return new AchievementSchema
                {
                    AppId = appId,
                    Language = language,
                    // Not cached on disk, the web version replaces it as soon as it can be downloaded.
                    Updated = DateTime.MinValue,
                    Achievements = AchievementReaders.ParseSteamSchema(BinaryKeyValues.Read(AchievementReaders.ReadSharedBytes(file)), language)
                };
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to read Steam achievement schema {file}");
                return null;
            }
        }

        /// <summary>
        /// Local copy of an achievement icon, downloaded from the Steam CDN when needed.
        /// </summary>
        public string GetIconPath(string appId, string icon, bool allowDownload = true)
        {
            if (icon.IsNullOrWhiteSpace() || appId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var fileName = Path.GetFileName(icon);
            var path = Path.Combine(cacheDir, "icons", appId, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            if (!allowDownload)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".download";
                using (var client = new WebClient())
                {
                    client.Headers.Add(HttpRequestHeader.UserAgent, "Gameroom");
                    client.DownloadFile($"{iconHost}{appId}/{fileName}", temp);
                }

                if (File.Exists(path))
                {
                    File.Delete(temp);
                }
                else
                {
                    File.Move(temp, path);
                }

                return path;
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to download achievement icon {appId}/{fileName}");
                return null;
            }
        }

        #endregion Schema and icons

        #region Unlocks

        private Dictionary<string, AchievementProgress> ReadCached(string path, Func<Dictionary<string, AchievementProgress>> read)
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            lock (progressCache)
            {
                if (progressCache.TryGetValue(path, out var cached) && cached.WriteTime == writeTime)
                {
                    return cached.Progress;
                }
            }

            var progress = read();
            lock (progressCache)
            {
                progressCache[path] = (writeTime, progress);
            }

            return progress;
        }

        private Dictionary<string, AchievementProgress> ReadSteamStats(string statsFile, string appId)
        {
            var schemaFile = AchievementReaders.GetSteamSchemaFile(Path.GetDirectoryName(statsFile), appId);
            if (!File.Exists(schemaFile))
            {
                return null;
            }

            return ReadCached(statsFile, () => AchievementReaders.ParseSteamStats(
                BinaryKeyValues.Read(AchievementReaders.ReadSharedBytes(statsFile)),
                BinaryKeyValues.Read(AchievementReaders.ReadSharedBytes(schemaFile))));
        }

        /// <summary>
        /// Unlocks of the app from the Steam client and all emulators. An achievement counts as unlocked if any source has it.
        /// </summary>
        public Dictionary<string, AchievementProgress> GetProgress(string appId, List<AchievementSource> sources = null)
        {
            var merged = new Dictionary<string, AchievementProgress>(StringComparer.Ordinal);
            void merge(Dictionary<string, AchievementProgress> found, AchievementSource source)
            {
                if (found == null || found.Count == 0)
                {
                    return;
                }

                if (sources != null && !sources.Contains(source))
                {
                    sources.Add(source);
                }

                foreach (var progress in found.Values)
                {
                    if (!merged.TryGetValue(progress.ApiName, out var existing))
                    {
                        merged[progress.ApiName] = new AchievementProgress
                        {
                            ApiName = progress.ApiName,
                            Unlocked = progress.Unlocked,
                            UnlockTime = progress.UnlockTime,
                            CurrentProgress = progress.CurrentProgress,
                            MaxProgress = progress.MaxProgress
                        };
                        continue;
                    }

                    if (progress.Unlocked && (!existing.Unlocked || (progress.UnlockTime != null && (existing.UnlockTime == null || progress.UnlockTime < existing.UnlockTime))))
                    {
                        existing.UnlockTime = progress.UnlockTime ?? existing.UnlockTime;
                    }

                    existing.Unlocked |= progress.Unlocked;
                    existing.CurrentProgress = Math.Max(existing.CurrentProgress, progress.CurrentProgress);
                    existing.MaxProgress = Math.Max(existing.MaxProgress, progress.MaxProgress);
                }
            }

            try
            {
                var statsFile = AchievementReaders.FindSteamUserStatsFile(steamStatsDir, appId);
                if (statsFile != null)
                {
                    merge(ReadSteamStats(statsFile, appId), AchievementSource.Steam);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to read Steam achievements of app {appId}");
            }

            foreach (var file in AchievementReaders.GetEmulatorFiles(appId))
            {
                if (!File.Exists(file.Path))
                {
                    continue;
                }

                try
                {
                    merge(ReadCached(file.Path, () => AchievementReaders.ReadEmulatorFile(file.Path)), file.Source);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read achievements {file.Path}");
                }
            }

            return merged;
        }

        public GameAchievements GetGameAchievements(Game game, bool allowSearch = true)
        {
            var appId = GetAppId(game, allowSearch);
            return appId == null ? null : GetGameAchievements(appId);
        }

        /// <summary>
        /// All achievements of the app with their state. Null when the app has no achievements or they couldn't be loaded.
        /// </summary>
        public GameAchievements GetGameAchievements(string appId, bool downloadIcons = true)
        {
            var schema = GetSchema(appId);
            if (schema == null || schema.Achievements.Count == 0)
            {
                return null;
            }

            return Build(appId, schema, downloadIcons);
        }

        /// <summary>
        /// Achievements of the game from what's already cached, without network or scanning game files,
        /// so they can be shown right away. Null when they have to be loaded with <see cref="GetGameAchievements(Game, bool)"/>;
        /// <paramref name="knownEmpty"/> tells when the game is known to have none.
        /// </summary>
        public GameAchievements GetCachedGameAchievements(Game game, out bool knownEmpty)
        {
            knownEmpty = false;
            var appId = GetKnownAppId(game);
            if (appId == null)
            {
                var data = gameroomDb.GetGameData(game.Id);
                knownEmpty = data?.AchievementAppSearched != null && DateTime.Now - data.AchievementAppSearched.Value < appSearchRetry;
                return null;
            }

            var schema = GetSchema(appId, false);
            if (schema == null)
            {
                return null;
            }

            if (schema.Achievements.Count == 0)
            {
                knownEmpty = true;
                return null;
            }

            return Build(appId, schema, false);
        }

        private GameAchievements Build(string appId, AchievementSchema schema, bool downloadIcons)
        {
            var result = new GameAchievements { AppId = appId };
            var progress = GetProgress(appId, result.Sources);
            foreach (var definition in schema.Achievements)
            {
                progress.TryGetValue(definition.ApiName, out var state);
                result.Items.Add(new GameAchievement
                {
                    Definition = definition,
                    Unlocked = state?.Unlocked == true,
                    UnlockTime = state?.UnlockTime,
                    CurrentProgress = state?.CurrentProgress ?? 0,
                    MaxProgress = state?.MaxProgress ?? 0
                });
            }

            Parallel.ForEach(result.Items, new ParallelOptions { MaxDegreeOfParallelism = 6 }, item =>
            {
                var icon = item.Unlocked ? item.Definition.Icon : item.Definition.IconGray ?? item.Definition.Icon;
                item.IconPath = GetIconPath(appId, icon, downloadIcons);
            });

            return result;
        }

        /// <summary>
        /// Latest unlocks of the whole library, for the start page.
        /// </summary>
        public List<RecentAchievement> GetRecentAchievements(int count, out int totalUnlocked)
        {
            var unlocks = new List<(Game Game, string AppId, AchievementProgress Progress)>();
            totalUnlocked = 0;
            foreach (var game in database.Games.Where(a => !a.Hidden).ToList())
            {
                var appId = GetKnownAppId(game);
                if (appId == null)
                {
                    continue;
                }

                var unlocked = GetProgress(appId).Values.Where(a => a.Unlocked).ToList();
                totalUnlocked += unlocked.Count;
                unlocks.AddRange(unlocked.Where(a => a.UnlockTime != null).Select(a => (game, appId, a)));
            }

            var result = new List<RecentAchievement>();
            foreach (var unlock in unlocks.OrderByDescending(a => a.Progress.UnlockTime))
            {
                var definition = GetSchema(unlock.AppId)?.Achievements.FirstOrDefault(a => a.ApiName == unlock.Progress.ApiName);
                if (definition == null)
                {
                    continue;
                }

                result.Add(new RecentAchievement
                {
                    GameId = unlock.Game.Id,
                    GameName = unlock.Game.Name,
                    Achievement = new GameAchievement
                    {
                        Definition = definition,
                        Unlocked = true,
                        UnlockTime = unlock.Progress.UnlockTime,
                        IconPath = GetIconPath(unlock.AppId, definition.Icon)
                    }
                });

                if (result.Count >= count)
                {
                    break;
                }
            }

            return result;
        }

        #endregion Unlocks

        #region Watching

        private void WatchEmulatorRoots()
        {
            foreach (var root in AchievementReaders.GetEmulatorRoots())
            {
                lock (watchers)
                {
                    if (disposed || watchedRoots.Contains(root) || !Directory.Exists(root))
                    {
                        continue;
                    }

                    watchedRoots.Add(root);
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "achievements.*", SearchOption.AllDirectories))
                    {
                        Baseline(file, () => AchievementReaders.ReadEmulatorFile(file));
                    }
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to list achievement files in {root}");
                }

                AddWatcher(root, "achievements.*", true);
            }
        }

        private void BaselineSteamStats()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(steamStatsDir, "UserGameStats_*.bin"))
                {
                    var appId = AchievementReaders.GetAppIdFromSteamStatsFile(file);
                    if (appId != null)
                    {
                        Baseline(file, () => ReadSteamStats(file, appId));
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to read Steam achievement cache.");
            }
        }

        private void Baseline(string file, Func<Dictionary<string, AchievementProgress>> read)
        {
            try
            {
                var unlocked = read()?.Values.Where(a => a.Unlocked).Select(a => a.ApiName);
                if (unlocked != null)
                {
                    lock (knownUnlocks)
                    {
                        knownUnlocks[file] = new HashSet<string>(unlocked);
                    }
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Failed to read achievements {file}");
            }
        }

        private void AddWatcher(string path, string filter, bool subdirectories)
        {
            try
            {
                var watcher = new FileSystemWatcher(path, filter)
                {
                    IncludeSubdirectories = subdirectories,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };

                watcher.Changed += (_, e) => QueueCheck(e.FullPath);
                watcher.Created += (_, e) => QueueCheck(e.FullPath);
                watcher.Renamed += (_, e) => QueueCheck(e.FullPath);
                watcher.Error += (_, e) => logger.Warn(e.GetException(), $"Achievement watcher of {path} failed.");
                watcher.EnableRaisingEvents = true;
                lock (watchers)
                {
                    if (disposed)
                    {
                        watcher.Dispose();
                        return;
                    }

                    watchers.Add(watcher);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to watch achievements in {path}");
            }
        }

        /// <summary>
        /// Starts checking the achievement files of a game that was just started.
        /// </summary>
        public void OnSessionStarted(Game game)
        {
            string appId;
            try
            {
                appId = GetAppId(game, false);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to get Steam app of {game.Name}");
                return;
            }

            if (appId == null)
            {
                return;
            }

            foreach (var file in AchievementReaders.GetEmulatorFiles(appId))
            {
                var exists = File.Exists(file.Path);
                lock (knownUnlocks)
                {
                    if (exists && !knownUnlocks.ContainsKey(file.Path))
                    {
                        Baseline(file.Path, () => AchievementReaders.ReadEmulatorFile(file.Path));
                    }
                }

                lock (polledFiles)
                {
                    polledFiles[file.Path] = (appId, game.Id, exists ? File.GetLastWriteTimeUtc(file.Path) : DateTime.MinValue);
                }
            }
        }

        public void OnSessionEnded(Game game)
        {
            List<string> files;
            lock (polledFiles)
            {
                files = polledFiles.Where(a => a.Value.GameId == game.Id).Select(a => a.Key).ToList();
                files.ForEach(a => polledFiles.Remove(a));
            }

            // Games often write the last unlock while closing.
            files.Where(File.Exists).ForEach(QueueCheck);
        }

        private void PollRunningGames()
        {
            List<string> changed;
            lock (polledFiles)
            {
                changed = new List<string>();
                foreach (var entry in polledFiles.ToList())
                {
                    var writeTime = File.Exists(entry.Key) ? File.GetLastWriteTimeUtc(entry.Key) : DateTime.MinValue;
                    if (writeTime != entry.Value.WriteTime)
                    {
                        polledFiles[entry.Key] = (entry.Value.AppId, entry.Value.GameId, writeTime);
                        changed.Add(entry.Key);
                    }
                }
            }

            changed.ForEach(QueueCheck);
        }

        /// <summary>
        /// Games write their achievement file several times in a row, it's read once they're done.
        /// </summary>
        private void QueueCheck(string path)
        {
            lock (pendingChecks)
            {
                if (disposed)
                {
                    return;
                }

                if (pendingChecks.TryGetValue(path, out var timer))
                {
                    timer.Change(changeDelayMs, Timeout.Infinite);
                    return;
                }

                pendingChecks[path] = new Timer(_ =>
                {
                    lock (pendingChecks)
                    {
                        if (pendingChecks.TryGetValue(path, out var done))
                        {
                            done.Dispose();
                            pendingChecks.Remove(path);
                        }
                    }

                    CheckFile(path);
                }, null, changeDelayMs, Timeout.Infinite);
            }
        }

        private string GetEmulatorAppId(string path)
        {
            lock (polledFiles)
            {
                if (polledFiles.TryGetValue(path, out var polled))
                {
                    return polled.AppId;
                }
            }

            return AchievementReaders.GetEmulatorRoots()
                .Select(root => AchievementReaders.GetAppId(root, path))
                .FirstOrDefault(a => a != null);
        }

        private void CheckFile(string path)
        {
            if (disposed || !File.Exists(path))
            {
                return;
            }

            string appId;
            AchievementSource source;
            Func<Dictionary<string, AchievementProgress>> read;
            var steamAppId = AchievementReaders.GetAppIdFromSteamStatsFile(path);
            if (steamAppId != null)
            {
                appId = steamAppId;
                source = AchievementSource.Steam;
                read = () => ReadSteamStats(path, steamAppId);
            }
            else if (AchievementReaders.IsAchievementFile(path))
            {
                appId = GetEmulatorAppId(path);
                source = AchievementReaders.GetSource(path);
                read = () => AchievementReaders.ReadEmulatorFile(path);
            }
            else
            {
                return;
            }

            if (appId == null)
            {
                return;
            }

            List<AchievementProgress> unlocked;
            try
            {
                unlocked = read()?.Values.Where(a => a.Unlocked).ToList();
            }
            catch (Exception e)
            {
                // Probably still being written, the next change event reads it again.
                logger.Debug(e, $"Failed to read achievements {path}");
                return;
            }

            if (unlocked == null)
            {
                return;
            }

            List<AchievementProgress> fresh;
            lock (knownUnlocks)
            {
                if (!knownUnlocks.TryGetValue(path, out var known))
                {
                    known = new HashSet<string>();
                    knownUnlocks[path] = known;
                }

                fresh = unlocked.Where(a => !known.Contains(a.ApiName)).ToList();
                // Never forgotten, so a half written file doesn't announce everything again.
                fresh.ForEach(a => known.Add(a.ApiName));
            }

            fresh = fresh.Where(a => a.UnlockTime == null || DateTime.Now - a.UnlockTime.Value < announceMaxAge).ToList();
            if (fresh.Count == 0)
            {
                return;
            }

            logger.Info($"Achievements unlocked in Steam app {appId} ({source}): {string.Join(", ", fresh.Select(a => a.ApiName))}");
            AchievementsUnlocked?.Invoke(this, new AchievementsUnlockedEventArgs { AppId = appId, Source = source, Unlocked = fresh });
        }

        #endregion Watching
    }
}
