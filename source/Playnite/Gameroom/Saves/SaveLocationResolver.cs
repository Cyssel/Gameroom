using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Gameroom
{
    public class ResolvedSaveLocation
    {
        /// <summary>Portable template stored in the backup.</summary>
        public string Template { get; set; }
        public string Path { get; set; }
        public SaveLocationSource Source { get; set; }
        public string Description { get; set; }

        public override string ToString()
        {
            return Path;
        }
    }

    /// <summary>
    /// Finds folders with save files of a game. Works without any store integration, so it also covers
    /// games added manually or running through Steam emulators.
    /// </summary>
    public class SaveLocationResolver
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Save folders used by Steam emulators and scene releases, keyed by Steam app id.
        private static readonly string[] steamAppIdTemplates =
        {
            @"%APPDATA%\Goldberg SteamEmu Saves\{SteamAppId}",
            @"%APPDATA%\GSE Saves\{SteamAppId}",
            @"%APPDATA%\SmartSteamEmu\{SteamAppId}",
            @"%APPDATA%\Steam\CODEX\{SteamAppId}",
            @"%APPDATA%\EMPRESS\{SteamAppId}",
            @"%APPDATA%\CPY_SAVES\{SteamAppId}",
            @"%APPDATA%\OnlineFix\{SteamAppId}",
            @"%APPDATA%\ALI213\{SteamAppId}",
            @"%LOCALAPPDATA%\SKIDROW\{SteamAppId}",
            @"%PUBLIC%\Documents\Steam\CODEX\{SteamAppId}",
            @"%PUBLIC%\Documents\Steam\RUNE\{SteamAppId}",
            @"%PUBLIC%\Documents\EMPRESS\{SteamAppId}",
            @"%PUBLIC%\Documents\OnlineFix\{SteamAppId}",
            @"%USERPROFILE%\Documents\CPY_SAVES\{SteamAppId}",
            @"%PROGRAMDATA%\Steam\*\{SteamAppId}",
            @"{InstallDir}\steam_settings\saves\{SteamAppId}",
        };

        // Folders inside the game folder that commonly hold saves (older games, portable and repacked games).
        private static readonly string[] installDirSaveFolders =
        {
            "save", "saves", "savegame", "savegames", "savedata", "saved games", "Profile", "profiles", "SaveGame",
            @"Saved\SaveGames", @"steam_settings\saves"
        };

        private readonly GameroomDatabase gameroomDb;

        public SaveLocationResolver(GameroomDatabase gameroomDb)
        {
            this.gameroomDb = gameroomDb;
        }

        public static string GetInstallDir(Game game)
        {
            var expanded = game.ExpandGame(true);
            return GameMatcher.NormalizePath(expanded.InstallDirectory);
        }

        public static string GetSteamAppId(Game game, GameroomGameData data)
        {
            if (!data?.SteamAppId.IsNullOrWhiteSpace() == true)
            {
                return data.SteamAppId;
            }

            if (game.PluginId == BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary) && !game.GameId.IsNullOrWhiteSpace())
            {
                return game.GameId;
            }

            var installDir = GetInstallDir(game);
            if (installDir != null && Directory.Exists(installDir))
            {
                try
                {
                    return UnknownGameDetector.FindSteamAppIdInGameFolder(installDir);
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>
        /// All existing save folders for the game. User and learned locations come first.
        /// </summary>
        public List<ResolvedSaveLocation> Resolve(Game game)
        {
            var data = gameroomDb.GetGameData(game.Id);
            var installDir = GetInstallDir(game);
            var steamAppId = GetSteamAppId(game, data);
            if (!steamAppId.IsNullOrWhiteSpace() && data?.SteamAppId.IsNullOrWhiteSpace() != false)
            {
                // Remember it, finding it in game files takes a while.
                data = data ?? new GameroomGameData { Id = game.Id };
                data.SteamAppId = steamAppId;
                gameroomDb.SaveGameData(data);
            }

            var result = new List<ResolvedSaveLocation>();
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void add(string template, SaveLocationSource source, string description)
            {
                foreach (var path in SavePaths.ExpandWildcards(SavePaths.Expand(template, installDir, steamAppId)))
                {
                    if (path == null || disabled.Contains(path) || !Directory.Exists(path))
                    {
                        continue;
                    }

                    // Nested locations would back up the same files twice.
                    if (result.Any(a => SavePaths.IsSubPathOrEqual(path, a.Path)))
                    {
                        continue;
                    }

                    result.RemoveAll(a => SavePaths.IsSubPathOrEqual(a.Path, path));
                    result.Add(new ResolvedSaveLocation
                    {
                        Template = template.Contains("*") ? SavePaths.Contract(path, installDir) : template,
                        Path = path,
                        Source = source,
                        Description = description
                    });
                }
            }

            if (data?.SaveLocations != null)
            {
                foreach (var location in data.SaveLocations.Where(a => !a.Enabled))
                {
                    var path = SavePaths.Expand(location.Path, installDir, steamAppId);
                    if (path != null)
                    {
                        disabled.Add(path);
                    }
                }

                foreach (var location in data.SaveLocations.Where(a => a.Enabled))
                {
                    add(location.Path, location.Source, location.Source.ToString());
                }
            }

            try
            {
                foreach (var template in steamAppIdTemplates)
                {
                    add(template, SaveLocationSource.Known, "Steam emulator");
                }

                var steamPath = SavePaths.GetSteamPath();
                if (steamPath != null && !steamAppId.IsNullOrWhiteSpace())
                {
                    add(Path.Combine(steamPath, "userdata", "*", SavePaths.SteamAppIdToken), SaveLocationSource.Known, "Steam Cloud");
                }

                foreach (var folder in installDirSaveFolders)
                {
                    add(Path.Combine(SavePaths.InstallDirToken, folder), SaveLocationSource.Known, "Game folder");
                }

                foreach (var template in GetEngineTemplates(installDir))
                {
                    add(template, SaveLocationSource.Known, "Engine");
                }

                foreach (var template in GetNameTemplates(game))
                {
                    add(template, SaveLocationSource.Known, "Name");
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to resolve save locations of {game.Name}");
            }

            return result;
        }

        /// <summary>
        /// Unity stores saves in LocalLow\Company\Product (from app.info), Unreal in LocalAppData\Project\Saved.
        /// </summary>
        private static IEnumerable<string> GetEngineTemplates(string installDir)
        {
            if (installDir == null || !Directory.Exists(installDir))
            {
                yield break;
            }

            var appInfos = new List<string>();
            try
            {
                appInfos.AddRange(Directory.GetFiles(installDir, "app.info", SearchOption.TopDirectoryOnly));
                foreach (var dataDir in Directory.GetDirectories(installDir, "*_Data"))
                {
                    appInfos.AddRange(Directory.GetFiles(dataDir, "app.info", SearchOption.TopDirectoryOnly));
                }
            }
            catch
            {
            }

            foreach (var appInfo in appInfos)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(appInfo);
                }
                catch
                {
                    continue;
                }

                if (lines.Length >= 2 && !lines[0].IsNullOrWhiteSpace() && !lines[1].IsNullOrWhiteSpace())
                {
                    yield return $@"%USERPROFILE%\AppData\LocalLow\{lines[0].Trim()}\{lines[1].Trim()}";
                }
            }

            string[] projectDirs;
            try
            {
                projectDirs = Directory.GetDirectories(installDir)
                    .Where(a => Directory.Exists(Path.Combine(a, "Binaries")) && !Path.GetFileName(a).Equals("Engine", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch
            {
                projectDirs = new string[0];
            }

            foreach (var projectDir in projectDirs)
            {
                var project = Path.GetFileName(projectDir);
                yield return $@"%LOCALAPPDATA%\{project}\Saved\SaveGames";
                yield return $@"%USERPROFILE%\Documents\My Games\{project}\Saved\SaveGames";
            }
        }

        private static IEnumerable<string> GetNameTemplates(Game game)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!game.Name.IsNullOrWhiteSpace())
            {
                var name = SavePaths.ToFolderName(game.Name.Replace(":", string.Empty));
                names.Add(name);
                names.Add(name.Replace(" ", string.Empty));
            }

            var developers = game.Developers?.Select(a => SavePaths.ToFolderName(a.Name)).Where(a => !a.IsNullOrWhiteSpace()).ToList() ?? new List<string>();
            foreach (var name in names)
            {
                yield return $@"%USERPROFILE%\Saved Games\{name}";
                yield return $@"%USERPROFILE%\Documents\My Games\{name}";
                yield return $@"%USERPROFILE%\Documents\{name}";
                yield return $@"%APPDATA%\{name}";
                yield return $@"%LOCALAPPDATA%\{name}";
                yield return $@"%USERPROFILE%\AppData\LocalLow\{name}";
                foreach (var developer in developers)
                {
                    yield return $@"%USERPROFILE%\Documents\{developer}\{name}";
                    yield return $@"%APPDATA%\{developer}\{name}";
                    yield return $@"%LOCALAPPDATA%\{developer}\{name}";
                    yield return $@"%USERPROFILE%\AppData\LocalLow\{developer}\{name}";
                }
            }
        }
    }
}
