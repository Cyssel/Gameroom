using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom
{
    public class GameMatchInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<string> InstallDirectories { get; set; } = new List<string>();
        public List<string> Executables { get; set; } = new List<string>();
    }

    /// <summary>
    /// Maps running executables to library games, either by an exact executable path
    /// (play actions, executables seen before) or by the game's installation folder.
    /// </summary>
    public class GameMatcher
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Executables that live in game folders but aren't the game itself.
        // Store clients start games of their store, the running game is found by its folder instead.
        private static readonly Regex launcherExecutable = new Regex(
            @"^(riotclientservices|riotclientux|steam|epicgameslauncher|battle\.net|galaxyclient|eadesktop|ealauncher|origin|upc|ubisoftconnect|rockstarlauncher|explorer)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex nonGameExecutable = new Regex(
            @"crash|unins\d*|uninstall|setup|redist|dxsetup|prereq|dotnetfx|oalinst|physx|vcredist|easyanticheat|beservice|battleye|steamerrorreporter|reporter|cefsharp|qtwebengineprocess|webhelper|updater|^update\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Folders holding many games, never usable as an installation folder of a single game.
        private static readonly Regex libraryRootFolder = new Regex(
            @"\\(steamapps(\\common)?|epic games|gog galaxy\\games|gog games|ubisoft game launcher\\games|ea games|origin games|xboxgames|games|juegos|steamlibrary|program files( \(x86\))?|windowsapps)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Dictionary<string, Guid> executables = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, Guid>> directories = new List<KeyValuePair<string, Guid>>();
        private readonly HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> genericDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int GameCount { get; }

        public GameMatcher(IEnumerable<GameMatchInfo> games, IEnumerable<string> ignoredExecutables)
        {
            foreach (var special in new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData
            })
            {
                var path = NormalizeDirectory(Environment.GetFolderPath(special));
                if (path != null)
                {
                    genericDirectories.Add(path);
                }
            }

            var programDir = NormalizeDirectory(PlaynitePaths.ProgramPath);
            if (programDir != null)
            {
                genericDirectories.Add(programDir);
            }

            foreach (var exe in ignoredExecutables ?? Enumerable.Empty<string>())
            {
                var path = NormalizePath(exe);
                if (path != null)
                {
                    ignored.Add(path);
                }
            }

            var dirOwners = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var game in games)
            {
                count++;
                foreach (var exe in game.Executables)
                {
                    var path = NormalizePath(exe);
                    if (path != null && !ignored.Contains(path))
                    {
                        executables[path] = game.Id;
                    }
                }

                foreach (var dir in game.InstallDirectories)
                {
                    var path = NormalizeDirectory(dir);
                    if (path == null || IsGenericDirectory(path))
                    {
                        continue;
                    }

                    if (!dirOwners.TryGetValue(path, out var owners))
                    {
                        owners = new HashSet<Guid>();
                        dirOwners.Add(path, owners);
                    }

                    owners.Add(game.Id);
                }
            }

            GameCount = count;

            // A folder shared by several games (launchers, compilations) can't tell them apart.
            foreach (var pair in dirOwners.Where(a => a.Value.Count == 1))
            {
                directories.Add(new KeyValuePair<string, Guid>(pair.Key, pair.Value.First()));
            }

            // Longest path first, so nested installations win over their parent folder.
            directories.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        }

        public static GameMatcher FromLibrary(IEnumerable<Game> games, GameroomDatabase gameroomDb, IEnumerable<string> ignoredExecutables)
        {
            var knownExes = new Dictionary<Guid, List<string>>();
            if (gameroomDb != null)
            {
                foreach (var data in gameroomDb.GetAllGameData())
                {
                    if (data.KnownExecutables.HasItems())
                    {
                        knownExes[data.Id] = data.KnownExecutables;
                    }
                }
            }

            var infos = new List<GameMatchInfo>();
            foreach (var game in games)
            {
                try
                {
                    var info = GetMatchInfo(game);
                    if (knownExes.TryGetValue(game.Id, out var exes))
                    {
                        info.Executables.AddRange(exes);
                    }

                    if (info.InstallDirectories.Count > 0 || info.Executables.Count > 0)
                    {
                        infos.Add(info);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to get detection info for {game.Name}");
                }
            }

            return new GameMatcher(infos, ignoredExecutables);
        }

        private static GameMatchInfo GetMatchInfo(Game game)
        {
            var info = new GameMatchInfo { Id = game.Id, Name = game.Name };
            var expanded = game.ExpandGame(true);
            if (!expanded.InstallDirectory.IsNullOrWhiteSpace() && (game.IsInstalled || Directory.Exists(expanded.InstallDirectory)))
            {
                info.InstallDirectories.Add(expanded.InstallDirectory);
                try
                {
                    // Process paths are reported with symlinks and junctions resolved.
                    var finalPath = Paths.GetFinalPathName(expanded.InstallDirectory);
                    if (!finalPath.IsNullOrWhiteSpace())
                    {
                        info.InstallDirectories.Add(finalPath);
                    }
                }
                catch
                {
                }
            }

            if (game.GameActions.HasItems())
            {
                foreach (var action in game.GameActions.Where(a => a.Type == GameActionType.File && !a.Path.IsNullOrWhiteSpace()))
                {
                    var path = game.ExpandVariables(action.Path, true);
                    if (!Path.IsPathRooted(path))
                    {
                        var workDir = action.WorkingDir.IsNullOrWhiteSpace() ? expanded.InstallDirectory : game.ExpandVariables(action.WorkingDir, true);
                        if (workDir.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        path = Path.Combine(workDir, path);
                    }

                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsLauncherAction(path, action, expanded.InstallDirectory))
                    {
                        info.Executables.Add(path);
                    }
                }
            }

            return info;
        }

        /// <summary>
        /// The action starts a launcher, e.g. "RiotClientServices.exe --launch-product=valorant" or "GRYPHLINK\Launcher.exe"
        /// for "GRYPHLINK\games\EndField Game": the launcher running doesn't mean the game is running.
        /// </summary>
        private static bool IsLauncherAction(string path, GameAction action, string installDir)
        {
            if (IsStoreClient(path))
            {
                return true;
            }

            if (installDir.IsNullOrWhiteSpace() || path.StartsWith(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var programDir = Path.GetDirectoryName(path)?.TrimEnd('\\');
            return !action.Arguments.IsNullOrWhiteSpace() ||
                (!programDir.IsNullOrEmpty() && installDir.StartsWith(programDir + "\\", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns id of a game the executable belongs to.
        /// </summary>
        public Guid? Match(string executablePath)
        {
            var path = NormalizePath(executablePath);
            if (path == null || ignored.Contains(path))
            {
                return null;
            }

            if (executables.TryGetValue(path, out var exeGame))
            {
                return exeGame;
            }

            if (IsNonGameExecutable(path))
            {
                return null;
            }

            foreach (var dir in directories)
            {
                if (path.StartsWith(dir.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return dir.Value;
                }
            }

            return null;
        }

        public bool IsIgnored(string executablePath)
        {
            var path = NormalizePath(executablePath);
            return path != null && ignored.Contains(path);
        }

        public static bool IsNonGameExecutable(string path)
        {
            return nonGameExecutable.IsMatch(Path.GetFileNameWithoutExtension(path));
        }

        /// <summary>
        /// Client of a store or launcher, like steam.exe or EADesktop.exe.
        /// </summary>
        public static bool IsStoreClient(string path)
        {
            return launcherExecutable.IsMatch(Path.GetFileNameWithoutExtension(path));
        }

        private bool IsGenericDirectory(string normalizedDir)
        {
            // Drive roots like "C:\".
            if (normalizedDir.Length <= 3)
            {
                return true;
            }

            if (genericDirectories.Contains(normalizedDir))
            {
                return true;
            }

            return libraryRootFolder.IsMatch(normalizedDir.TrimEnd('\\'));
        }

        public static string NormalizePath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!Path.IsPathRooted(path))
                {
                    return null;
                }

                path = Path.GetFullPath(path);
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    path = path.Substring(4);
                }

                return path.TrimEnd('\\');
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Normalized directory path with a trailing separator, so "C:\Fallout\" doesn't match "C:\Fallout 2\".
        /// </summary>
        public static string NormalizeDirectory(string path)
        {
            var normalized = NormalizePath(path);
            return normalized == null ? null : normalized + "\\";
        }
    }
}
