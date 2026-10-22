using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace Playnite.Gameroom.InstalledGames
{
    public class InstalledGamesResult
    {
        public List<Game> Added { get; } = new List<Game>();
        /// <summary>Library games that were marked as not installed and were found installed.</summary>
        public List<Game> Reinstalled { get; } = new List<Game>();
    }

    /// <summary>
    /// Adds installed games found by <see cref="InstalledGameScanner"/> to the library. Every application is
    /// checked once and remembered: games removed from the library aren't added again.
    /// </summary>
    public class InstalledGameImporter
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        // Applications that weren't games are looked at again after a while, e.g. a launcher that downloaded its game.
        private static readonly TimeSpan notGameRecheck = TimeSpan.FromDays(7);
        private static readonly TimeSpan fullScanInterval = TimeSpan.FromHours(2);
        private DateTime lastFullScan = DateTime.MinValue;
        private readonly IGameDatabaseMain database;
        private readonly GameroomDatabase gameroomDb;
        private readonly InstalledGameScanner scanner;
        private readonly Func<ICollection<Guid>> getLibraryPlugins;
        private readonly object scanLock = new object();

        // Stores whose library extension imports their installed games itself.
        private static readonly Dictionary<string, BuiltinExtension> storeExtensions = new Dictionary<string, BuiltinExtension>
        {
            { "Steam", BuiltinExtension.SteamLibrary },
            { "Epic", BuiltinExtension.EpicLibrary },
            { "Battle.net", BuiltinExtension.BattleNetLibrary },
            { "EA app", BuiltinExtension.OriginLibrary },
            { "Xbox", BuiltinExtension.XboxLibrary }
        };

        public InstalledGameImporter(IGameDatabaseMain database, GameroomDatabase gameroomDb, UnknownGameDetector detector, Func<ICollection<Guid>> getLibraryPlugins = null)
        {
            this.database = database;
            this.gameroomDb = gameroomDb;
            this.getLibraryPlugins = getLibraryPlugins ?? (() => new List<Guid>());
            scanner = new InstalledGameScanner(detector);
        }

        private bool IsImportedByExtension(DetectedInstalledGame detected, ICollection<Guid> plugins)
        {
            return detected.Source != null && storeExtensions.TryGetValue(detected.Source, out var extension) &&
                plugins.Contains(BuiltinExtensions.GetIdFromExtension(extension));
        }

        /// <summary>
        /// Finds installed games and adds the new ones: registered applications, shortcuts, launcher manifests and
        /// folders of the drives. <paramref name="recheck"/> also looks again at applications that weren't games.
        /// </summary>
        public InstalledGamesResult Import(bool recheck)
        {
            var programs = InstalledGameScanner.ReadInstalledPrograms();
            var launcherGames = InstalledGameScanner.ReadEpicGames();
            var folderRoots = new List<string>();
            // Shortcuts, store packages and drives take seconds to read, most installs show up in the registry.
            if (recheck || DateTime.Now - lastFullScan > fullScanInterval)
            {
                lastFullScan = DateTime.Now;
                programs.AddRange(InstalledGameScanner.ReadShortcuts());
                launcherGames.AddRange(InstalledGameScanner.ReadXboxGames());
                folderRoots = InstalledGameScanner.GetDriveRoots();
            }

            return Import(programs, launcherGames, folderRoots, recheck);
        }

        public InstalledGamesResult Import(IEnumerable<InstalledProgram> programs, IEnumerable<DetectedInstalledGame> launcherGames, bool recheck)
        {
            return Import(programs, launcherGames, new string[0], recheck);
        }

        /// <param name="folderRoots">Roots whose folders may hold games copied without an installer, e.g. drives.</param>
        public InstalledGamesResult Import(IEnumerable<InstalledProgram> programs, IEnumerable<DetectedInstalledGame> launcherGames, IEnumerable<string> folderRoots, bool recheck)
        {
            lock (scanLock)
            {
                var result = new InstalledGamesResult();
                var records = gameroomDb.GetInstalledApps().ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                var library = new LibraryIndex(database, gameroomDb);
                var plugins = getLibraryPlugins();
                var programList = programs.ToList();

                foreach (var program in programList)
                {
                    Check(program, records, recheck, library, plugins, result);
                }

                foreach (var detected in launcherGames)
                {
                    if (!records.ContainsKey(detected.Key) && !IsImportedByExtension(detected, plugins))
                    {
                        Process(detected, null, library, result);
                    }
                }

                // Last, so folders of everything found so far are known and aren't taken for other games.
                var knownDirectories = InstalledGameScanner.GetProgramDirectories(programList)
                    .Concat(library.Directories)
                    .Concat(new[] { PlaynitePaths.ProgramPath })
                    .ToList();
                foreach (var folder in InstalledGameScanner.FindGameFolders(folderRoots, knownDirectories))
                {
                    Check(folder, records, recheck, library, plugins, result);
                }

                if (result.Added.Count > 0 || result.Reinstalled.Count > 0)
                {
                    logger.Info($"Installed games: added {string.Join(", ", result.Added.Select(a => a.Name))}; " +
                        $"found installed {string.Join(", ", result.Reinstalled.Select(a => a.Name))}");
                }

                return result;
            }
        }

        private void Check(InstalledProgram program, Dictionary<string, InstalledAppRecord> records, bool recheck, LibraryIndex library,
            ICollection<Guid> plugins, InstalledGamesResult result)
        {
            if (records.TryGetValue(program.Key, out var record) &&
                (record.Decision != InstalledAppDecision.NotGame || (!recheck && DateTime.Now - record.Checked < notGameRecheck)))
            {
                return;
            }

            List<DetectedInstalledGame> games;
            try
            {
                games = scanner.Classify(program);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to check installed application {program.Name}");
                return;
            }

            if (games.Count == 0)
            {
                Save(program.Key, program.Name, InstalledAppDecision.NotGame, Guid.Empty);
                return;
            }

            foreach (var detected in games)
            {
                // Games of a launcher are remembered one by one, the launcher is looked at again for new ones.
                if (detected.Key != program.Key && records.TryGetValue(detected.Key, out var gameRecord) &&
                    gameRecord.Decision != InstalledAppDecision.NotGame)
                {
                    continue;
                }

                // Not remembered: if the extension is removed later, the game is added then.
                if (IsImportedByExtension(detected, plugins))
                {
                    continue;
                }

                Process(detected, detected.Key == program.Key ? program.DisplayIcon : null, library, result);
            }

            if (games.All(a => a.Key != program.Key))
            {
                Save(program.Key, program.Name, InstalledAppDecision.NotGame, Guid.Empty);
            }
        }

        private void Save(string key, string name, InstalledAppDecision decision, Guid gameId)
        {
            gameroomDb.SaveInstalledApp(new InstalledAppRecord
            {
                Id = key,
                Name = name,
                Decision = decision,
                GameId = gameId,
                Checked = DateTime.Now
            });
        }

        private void Process(DetectedInstalledGame detected, string displayIcon, LibraryIndex library, InstalledGamesResult result)
        {
            var existing = library.Find(detected);
            if (existing != null)
            {
                if (!existing.IsInstalled && existing.PluginId == Guid.Empty)
                {
                    // Reinstalled: playtime, sessions and backups stay with the existing entry.
                    existing.InstallDirectory = detected.InstallDirectory?.TrimEnd('\\');
                    existing.IsInstalled = true;
                    if (existing.GameActions?.Any(a => a.IsPlayAction) != true)
                    {
                        existing.GameActions = new ObservableCollection<GameAction> { CreatePlayAction(detected) };
                    }

                    database.Games.Update(existing);
                    result.Reinstalled.Add(existing);
                }

                SaveGameData(existing.Id, detected);
                Save(detected.Key, detected.Name, InstalledAppDecision.Added, existing.Id);
                return;
            }

            var game = new Game(detected.Name)
            {
                InstallDirectory = detected.InstallDirectory?.TrimEnd('\\'),
                IsInstalled = true,
                Added = DateTime.Now,
                GameActions = new ObservableCollection<GameAction> { CreatePlayAction(detected) }
            };

            if (!detected.Source.IsNullOrWhiteSpace())
            {
                game.SourceId = database.Sources.Add(detected.Source).Id;
            }

            // Metadata providers look games up by platform, IGDB fails on games without one.
            game.PlatformIds = new List<Guid> { GetPcPlatformId() };

            database.Games.Add(game);
            library.Add(game, detected.SteamAppId);
            SaveGameData(game.Id, detected);
            SetIcon(game, detected, displayIcon);
            Save(detected.Key, detected.Name, InstalledAppDecision.Added, game.Id);
            result.Added.Add(game);
        }

        private void SaveGameData(Guid gameId, DetectedInstalledGame detected)
        {
            if (detected.SteamAppId.IsNullOrWhiteSpace() && detected.GameExecutable.IsNullOrWhiteSpace())
            {
                return;
            }

            var data = gameroomDb.GetOrCreateGameData(gameId);
            if (data.SteamAppId.IsNullOrWhiteSpace())
            {
                data.SteamAppId = detected.SteamAppId;
            }

            // A game started through its launcher is found running by its own program.
            if (!detected.GameExecutable.IsNullOrWhiteSpace() && !data.KnownExecutables.Contains(detected.GameExecutable, StringComparer.OrdinalIgnoreCase))
            {
                data.KnownExecutables.Add(detected.GameExecutable);
            }

            gameroomDb.SaveGameData(data);
        }

        private Guid GetPcPlatformId()
        {
            var platform = database.Platforms.FirstOrDefault(a => a.SpecificationId == "pc_windows") ??
                database.Platforms.FirstOrDefault(a => string.Equals(a.Name, "PC (Windows)", StringComparison.OrdinalIgnoreCase));
            if (platform == null)
            {
                platform = new Platform("PC (Windows)") { SpecificationId = "pc_windows" };
                database.Platforms.Add(platform);
            }

            return platform.Id;
        }

        private static GameAction CreatePlayAction(DetectedInstalledGame detected)
        {
            if (!detected.Url.IsNullOrWhiteSpace())
            {
                return new GameAction
                {
                    Name = ResourceProvider.GetString("LOCPlayGame"),
                    Type = GameActionType.URL,
                    Path = detected.Url,
                    IsPlayAction = true
                };
            }

            var dir = detected.InstallDirectory?.TrimEnd('\\');
            var path = dir != null && detected.Executable.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
                ? ExpandableVariables.InstallationDirectory + detected.Executable.Substring(dir.Length)
                : detected.Executable;
            return new GameAction
            {
                Name = ResourceProvider.GetString("LOCPlayGame"),
                Type = GameActionType.File,
                Path = path,
                Arguments = detected.Arguments,
                WorkingDir = Path.GetDirectoryName(path),
                IsPlayAction = true
            };
        }

        /// <summary>
        /// Icon of the registered application, or of the game executable.
        /// </summary>
        private void SetIcon(Game game, DetectedInstalledGame detected, string displayIcon)
        {
            var icon = detected.Icon != null && File.Exists(detected.Icon)
                ? detected.Icon
                : detected.GameExecutable ?? InstalledGameScanner.GetCommandPath(displayIcon);
            if (icon == null || !File.Exists(icon) ||
                !(icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            {
                // The Riot client icon would be the same for every Riot game.
                icon = detected.Source == "Riot Games" ? null : detected.Executable;
            }

            if (icon == null || !File.Exists(icon))
            {
                return;
            }

            try
            {
                game.Icon = database.AddFile(icon, game.Id, true, CancellationToken.None);
                database.Games.Update(game);
            }
            catch (Exception e)
            {
                logger.Warn(e, $"Failed to set icon of {game.Name} from {icon}");
            }
        }

        public void OnGamesRemoved(IEnumerable<Game> games)
        {
            var removed = new HashSet<Guid>(games.Select(a => a.Id));
            foreach (var record in gameroomDb.GetInstalledApps().Where(a => a.Decision == InstalledAppDecision.Added && removed.Contains(a.GameId)))
            {
                // Removed on purpose, it isn't added back.
                record.Decision = InstalledAppDecision.Removed;
                gameroomDb.SaveInstalledApp(record);
            }
        }

        /// <summary>
        /// Lookup of library games by Steam app, folder and name.
        /// </summary>
        private class LibraryIndex
        {
            private readonly Dictionary<string, Game> bySteamApp = new Dictionary<string, Game>();
            private readonly Dictionary<string, Game> byDirectory = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Game> byName = new Dictionary<string, Game>();

            /// <summary>Installation folders of the library games.</summary>
            public IEnumerable<string> Directories => byDirectory.Keys;

            public LibraryIndex(IGameDatabaseMain database, GameroomDatabase gameroomDb)
            {
                var steamPlugin = BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary);
                var steamApps = gameroomDb.GetAllGameData().Where(a => !a.SteamAppId.IsNullOrWhiteSpace()).ToDictionary(a => a.Id, a => a.SteamAppId);
                foreach (var game in database.Games)
                {
                    var appId = game.PluginId == steamPlugin ? game.GameId : steamApps.TryGetValue(game.Id, out var id) ? id : null;
                    string dir = null;
                    try
                    {
                        dir = SaveLocationResolver.GetInstallDir(game);
                    }
                    catch
                    {
                    }

                    Add(game, appId, dir);
                }
            }

            public void Add(Game game, string steamAppId, string installDir = null)
            {
                if (!steamAppId.IsNullOrWhiteSpace() && !bySteamApp.ContainsKey(steamAppId))
                {
                    bySteamApp[steamAppId] = game;
                }

                var dir = (installDir ?? game.InstallDirectory)?.TrimEnd('\\');
                if (!dir.IsNullOrWhiteSpace() && !byDirectory.ContainsKey(dir))
                {
                    byDirectory[dir] = game;
                }

                var name = SavePaths.NormalizeName(game.Name);
                if (!name.IsNullOrEmpty() && !byName.ContainsKey(name))
                {
                    byName[name] = game;
                }
            }

            public Game Find(DetectedInstalledGame detected)
            {
                if (!detected.SteamAppId.IsNullOrWhiteSpace() && bySteamApp.TryGetValue(detected.SteamAppId, out var steamGame))
                {
                    return steamGame;
                }

                var dir = detected.InstallDirectory?.TrimEnd('\\');
                if (!dir.IsNullOrWhiteSpace() && byDirectory.TryGetValue(dir, out var dirGame))
                {
                    return dirGame;
                }

                var name = SavePaths.NormalizeName(detected.Name);
                return !name.IsNullOrEmpty() && byName.TryGetValue(name, out var nameGame) ? nameGame : null;
            }
        }
    }
}
