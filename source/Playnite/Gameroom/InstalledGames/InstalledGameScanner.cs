using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Playnite.Common;
using Playnite.Gameroom.Achievements;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom.InstalledGames
{
    public enum InstalledProgramKind
    {
        /// <summary>Registered in Windows "Installed apps".</summary>
        Registered,
        /// <summary>Shortcut on the desktop or in the start menu, <see cref="InstalledProgram.DisplayIcon"/> is its target.</summary>
        Shortcut,
        /// <summary>Folder on a drive, for games copied or extracted without an installer.</summary>
        Folder,
        /// <summary>Root of a drive, only the programs right in it are looked at.</summary>
        DriveRoot
    }

    /// <summary>
    /// Application that may be a game: registered in Windows, started from a shortcut or found in a folder.
    /// </summary>
    public class InstalledProgram
    {
        /// <summary>Identifies the application, e.g. the registry key name.</summary>
        public string Key { get; set; }
        public InstalledProgramKind Kind { get; set; }
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string InstallLocation { get; set; }
        public string DisplayIcon { get; set; }
        public string UninstallString { get; set; }
        /// <summary>Arguments of a shortcut.</summary>
        public string Arguments { get; set; }
    }

    /// <summary>
    /// Installed game found on this PC, with how to start it.
    /// </summary>
    public class DetectedInstalledGame
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string InstallDirectory { get; set; }
        /// <summary>Program to run, used when <see cref="Url"/> is empty.</summary>
        public string Executable { get; set; }
        public string Arguments { get; set; }
        /// <summary>Program of the game itself when <see cref="Executable"/> is its launcher.</summary>
        public string GameExecutable { get; set; }
        /// <summary>Icon file when the programs don't hold one, e.g. the logo of a store package.</summary>
        public string Icon { get; set; }
        /// <summary>Launcher link, e.g. steam://rungameid/...</summary>
        public string Url { get; set; }
        public string SteamAppId { get; set; }
        /// <summary>Store or launcher the game belongs to, null for standalone games.</summary>
        public string Source { get; set; }

        public override string ToString()
        {
            return $"{Name} ({Source ?? "Windows"}): {Url ?? Executable}";
        }
    }

    /// <summary>
    /// Finds games among the applications installed on Windows, shortcuts, folders of the drives and launcher
    /// manifests. An application counts as a game only with strong evidence: a launcher registration (Steam, Riot,
    /// Battle.net, HoYoPlay, EA, Epic, Xbox), a game engine or Steam API in its folder, or a game repack installer.
    /// </summary>
    public class InstalledGameScanner
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const int maxScannedDirectories = 400;
        private const int maxInspectedExecutables = 80;
        private const int maxDepth = 3;

        // Launchers and stores: they install games but aren't games themselves.
        private static readonly HashSet<string> launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steam", "Battle.net", "EA app", "Origin", "Riot Client", "Riot Vanguard", "HoYoPlay", "Google Play Games",
            "Ankama Launcher", "TapTap(Global)", "TapTap", "Epic Games Launcher", "GOG GALAXY", "Ubisoft Connect", "Uplay",
            "Rockstar Games Launcher", "Amazon Games", "itch", "Xbox", "Playnite", "Gameroom", "Unity", "Unity Hub",
            "Roblox Studio", "LaunchBox", "Heroic Games Launcher", "Prism Launcher", "CurseForge", "Overwolf", "WeMod", "Wand"
        };

        // Development tools and runtimes, some of them ship engine files.
        private static readonly Regex developerTools = new Regex(
            @"^(unity\b|android studio|visual studio|microsoft (visual c\+\+|\.net|asp\.net|windows desktop runtime)|windows (sdk|software development)|" +
            @"python|java\b|node\.js|git$|cmake|llvm|qemu|blender|godot engine|unreal engine|epic online services|directx)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Clients of games that don't ship engine files, recognized by name.
        private static readonly Regex knownGameClients = new Regex(
            @"^(roblox player|osu!|minecraft|hytale)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Installers of game repacks name the game like "Game MULTi11 - ElAmigos version 1.0".
        private static readonly Regex repackName = new Regex(
            @"\b(elamigos|fitgirl|dodi|kaoskrew|xatab|chovka|repack)\b|\bMULTi\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex[] nameNoise =
        {
            new Regex(@"\s+MULTi\d+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s*[-–]\s*(elamigos|fitgirl|dodi).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s*[\[(](elamigos|fitgirl|dodi|kaoskrew|xatab|chovka)[^\])]*[\])]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+(version|versión|v)\s*\d+(\.\d+)+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+for\s+\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"\s+launcher$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?<=^roblox)\s+player$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        // Folders inside games that only hold tools, redistributables or engine internals.
        private static readonly HashSet<string> skippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "DirectX", "DXSETUP", "Support", "__Installer",
            "Installer", "vcredist", "Prerequisites", "MonoBleedingEdge", "steam_settings", "Engine", "jre", "java",
            "node_modules", "locales", "swiftshader", "Logs", "Saved", "Crashes", "Tools", "Uninstall"
        };

        private static readonly Regex helperExecutable = new Regex(
            @"helper|cefsharp|qtwebengine|notification|elevate|service|agent|daemon|bootstrap|repair",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Folders of a drive that never are a game copied there.
        private static readonly HashSet<string> nonGameFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "Archivos de programa", "ProgramData", "Users", "Usuarios",
            "Documents and Settings", "Recovery", "PerfLogs", "inetpub", "Intel", "AMD", "NVIDIA", "Drivers", "WindowsApps",
            "XboxGames", "WpSystem", "WUDownloadCache", "OneDriveTemp", "Config.Msi", "msdownld.tmp", "dotnet", "nuget-cache",
            "DevTools", "Downloads", "Descargas", "Videos", "Music", "Música", "Pictures", "Imágenes", "Documents", "Documentos"
        };

        private static readonly Regex uninstallShortcut = new Regex(
            @"^(uninstall|desinstalar|remove|eliminar|repair|reparar)\b|\b(uninstall|uninstaller|desinstalar)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Folders holding one game per subfolder.
        private static readonly Regex gameCollectionFolder = new Regex(
            @"^(games|juegos|my games|mis juegos|jogos|spiele|jeux)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Engines that are a single game, their name is the game's name.
        private static readonly HashSet<string> singleGameEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UnknownGameDetector.RagnarokOnline
        };

        private readonly UnknownGameDetector detector;

        public InstalledGameScanner(UnknownGameDetector detector)
        {
            this.detector = detector;
        }

        #region Reading

        /// <summary>
        /// Applications in the Windows uninstall registry of the machine and the current user, both 64 and 32 bit views.
        /// </summary>
        public static List<InstalledProgram> ReadInstalledPrograms()
        {
            var result = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.CurrentUser, RegistryView.Registry32)
            };

            foreach (var (hive, view) in roots)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null)
                        {
                            continue;
                        }

                        foreach (var keyName in uninstall.GetSubKeyNames())
                        {
                            using (var key = uninstall.OpenSubKey(keyName))
                            {
                                var program = ReadProgram(key, keyName);
                                if (program != null && !result.ContainsKey(program.Key))
                                {
                                    result[program.Key] = program;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read installed applications of {hive} {view}");
                }
            }

            return result.Values.ToList();
        }

        private static InstalledProgram ReadProgram(RegistryKey key, string keyName)
        {
            if (key == null)
            {
                return null;
            }

            var name = key.GetValue("DisplayName") as string;
            if (name.IsNullOrWhiteSpace() ||
                (key.GetValue("SystemComponent") is int system && system == 1) ||
                key.GetValue("ParentKeyName") != null ||
                !(key.GetValue("ReleaseType") as string).IsNullOrEmpty())
            {
                return null;
            }

            return new InstalledProgram
            {
                Key = "uninstall:" + keyName,
                // Some installers pad the name with null characters.
                Name = name.Replace("\0", string.Empty).Trim(),
                Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                InstallLocation = key.GetValue("InstallLocation") as string,
                DisplayIcon = key.GetValue("DisplayIcon") as string,
                UninstallString = key.GetValue("UninstallString") as string
            };
        }

        /// <summary>
        /// Games installed with the Epic Games launcher, which doesn't register them in Windows.
        /// </summary>
        public static List<DetectedInstalledGame> ReadEpicGames()
        {
            var result = new List<DetectedInstalledGame>();
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(manifests))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    var manifest = JObject.Parse(File.ReadAllText(file));
                    var location = manifest["InstallLocation"]?.ToString();
                    var appName = manifest["AppName"]?.ToString();
                    if (manifest["bIsIncompleteInstall"]?.Value<bool>() == true || location.IsNullOrWhiteSpace() || !Directory.Exists(location) ||
                        appName.IsNullOrWhiteSpace() || manifest["AppCategories"]?.Any(a => a.ToString() == "applications") == true)
                    {
                        continue;
                    }

                    var id = $"{manifest["CatalogNamespace"]}:{manifest["CatalogItemId"]}:{appName}";
                    result.Add(new DetectedInstalledGame
                    {
                        Key = "epic:" + appName,
                        Name = CleanName(manifest["DisplayName"]?.ToString() ?? appName),
                        InstallDirectory = GameMatcher.NormalizeDirectory(location),
                        Url = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(id)}?action=launch&silent=true",
                        Source = "Epic"
                    });
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read Epic manifest {file}");
                }
            }

            return result;
        }

        /// <summary>
        /// Games of the Xbox app and Microsoft Store: packages shipping a MicrosoftGame.config.
        /// </summary>
        public static List<DetectedInstalledGame> ReadXboxGames()
        {
            var result = new List<DetectedInstalledGame>();
            foreach (var app in Programs.GetUWPApps())
            {
                try
                {
                    if (app.WorkDir.IsNullOrWhiteSpace() || !File.Exists(Path.Combine(app.WorkDir, "MicrosoftGame.config")))
                    {
                        continue;
                    }

                    result.Add(new DetectedInstalledGame
                    {
                        Key = "xbox:" + app.AppId,
                        Name = CleanName(app.Name),
                        InstallDirectory = GameMatcher.NormalizeDirectory(app.WorkDir),
                        // Store apps are started through the shell, the game controller follows their process.
                        Executable = "explorer.exe",
                        Arguments = app.Arguments,
                        GameExecutable = Directory.EnumerateFiles(app.WorkDir, "*.exe").FirstOrDefault(a => !IsHelperExecutable(a)),
                        // Programs of store packages can't be read for their icon.
                        Icon = app.Icon,
                        Source = "Xbox"
                    });
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read Xbox game {app.Name}");
                }
            }

            return result;
        }

        /// <summary>
        /// Programs started from shortcuts on the desktop and in the start menu, for games that were copied or
        /// extracted instead of installed.
        /// </summary>
        public static List<InstalledProgram> ReadShortcuts()
        {
            var result = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
            var folders = new[]
            {
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms
            }.Select(Environment.GetFolderPath).Where(a => !a.IsNullOrEmpty() && Directory.Exists(a)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                string[] links;
                try
                {
                    links = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to read shortcuts in {folder}");
                    continue;
                }

                foreach (var link in links)
                {
                    try
                    {
                        var shortcut = Programs.GetLnkShortcutData(link);
                        var target = GameMatcher.NormalizePath(shortcut.Path);
                        if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                        {
                            continue;
                        }

                        var arguments = shortcut.Arguments.IsNullOrWhiteSpace() ? null : shortcut.Arguments.Trim();
                        var key = ("shortcut:" + target + (arguments == null ? string.Empty : " " + arguments)).ToLowerInvariant();
                        if (!result.ContainsKey(key))
                        {
                            result[key] = new InstalledProgram
                            {
                                Key = key,
                                Kind = InstalledProgramKind.Shortcut,
                                Name = Path.GetFileNameWithoutExtension(link),
                                DisplayIcon = target,
                                Arguments = arguments
                            };
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, $"Failed to read shortcut {link}");
                    }
                }
            }

            return result.Values.ToList();
        }

        /// <summary>
        /// Roots of the fixed drives, where games copied without an installer are looked for.
        /// </summary>
        public static List<string> GetDriveRoots()
        {
            try
            {
                return DriveInfo.GetDrives().Where(a => a.DriveType == DriveType.Fixed && a.IsReady).Select(a => a.RootDirectory.FullName).ToList();
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to list drives");
                return new List<string>();
            }
        }

        /// <summary>
        /// Folders that may hold a game copied or extracted without an installer: each root, its top level folders and
        /// the folders inside "Games" or "Juegos". Folders of known programs and games, and the folders around them,
        /// are skipped: a folder holding a known game isn't another game.
        /// </summary>
        public static List<InstalledProgram> FindGameFolders(IEnumerable<string> roots, IEnumerable<string> knownDirectories)
        {
            var known = knownDirectories
                .Select(GameMatcher.NormalizeDirectory)
                .Where(a => a != null && a.Length > 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool isKnown(string dir) => known.Any(a => a.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(a, StringComparison.OrdinalIgnoreCase));

            var result = new List<InstalledProgram>();
            void add(string dir, string name)
            {
                if (!isKnown(dir) && !Directory.Exists(Path.Combine(dir, "steamapps")))
                {
                    result.Add(new InstalledProgram
                    {
                        Key = "folder:" + dir.ToLowerInvariant(),
                        Kind = InstalledProgramKind.Folder,
                        Name = name,
                        InstallLocation = dir
                    });
                }
            }

            foreach (var root in roots)
            {
                var rootDir = GameMatcher.NormalizeDirectory(root);
                if (rootDir == null || !Directory.Exists(rootDir))
                {
                    continue;
                }

                result.Add(new InstalledProgram
                {
                    Key = "drive:" + rootDir.ToLowerInvariant(),
                    Kind = InstalledProgramKind.DriveRoot,
                    Name = rootDir,
                    InstallLocation = rootDir
                });
                foreach (var dir in GetVisibleDirectories(rootDir))
                {
                    var name = Path.GetFileName(dir.TrimEnd('\\'));
                    if (nonGameFolders.Contains(name) || developerTools.IsMatch(name) || launchers.Contains(name))
                    {
                        continue;
                    }

                    if (gameCollectionFolder.IsMatch(name))
                    {
                        foreach (var game in GetVisibleDirectories(dir))
                        {
                            add(game, Path.GetFileName(game.TrimEnd('\\')));
                        }
                    }
                    else
                    {
                        add(dir, name);
                    }
                }
            }

            return result;
        }

        private static List<string> GetVisibleDirectories(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).GetDirectories()
                    .Where(a => (a.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !a.Name.StartsWith("$") && !a.Name.StartsWith("."))
                    .Select(a => GameMatcher.NormalizeDirectory(a.FullName))
                    .Where(a => a != null)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Folders of registered applications that exist, to tell their folders from copied games.
        /// </summary>
        public static List<string> GetProgramDirectories(IEnumerable<InstalledProgram> programs)
        {
            var result = new List<string>();
            foreach (var program in programs.Where(a => a.Kind == InstalledProgramKind.Registered))
            {
                var dir = ExistingDirectory(program.InstallLocation) ?? GetProgramDirectory(program, GetCommandPath(program.DisplayIcon));
                if (dir != null)
                {
                    result.Add(dir);
                }
            }

            return result;
        }

        #endregion Reading

        #region Parsing

        /// <summary>
        /// Name without repack tags, versions or "Launcher": "Pragmata MULTi14 - ElAmigos versión 1.0" is "Pragmata".
        /// </summary>
        public static string CleanName(string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return name;
            }

            var clean = name.Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty).Trim();
            foreach (var noise in nameNoise)
            {
                var stripped = noise.Replace(clean, string.Empty).Trim();
                if (stripped.Length > 0)
                {
                    clean = stripped;
                }
            }

            return clean;
        }

        /// <summary>
        /// Program path of a command line like <c>"C:\Game\unins000.exe" /SILENT</c> or an icon like <c>C:\Game\game.exe,0</c>.
        /// </summary>
        public static string GetCommandPath(string command)
        {
            if (command.IsNullOrWhiteSpace())
            {
                return null;
            }

            command = command.Trim();
            string path;
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                path = end > 1 ? command.Substring(1, end - 1) : command.Trim('"');
            }
            else
            {
                var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe > 0 ? command.Substring(0, exe + 4) : command.Split(',')[0];
            }

            return path.Trim().Replace('/', '\\');
        }

        public static string GetSteamAppId(string uninstallString)
        {
            var match = Regex.Match(uninstallString ?? string.Empty, @"steam://uninstall/(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Riot games uninstall with <c>RiotClientServices.exe --uninstall-product=valorant --uninstall-patchline=live</c>.
        /// </summary>
        public static (string Client, string Product, string Patchline)? GetRiotProduct(string uninstallString)
        {
            var match = Regex.Match(uninstallString ?? string.Empty,
                @"^""?(?<client>[^""]*RiotClientServices\.exe)""?\s+--uninstall-product=(?<product>[\w.-]+)(\s+--uninstall-patchline=(?<line>[\w.-]+))?",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            return (match.Groups["client"].Value, match.Groups["product"].Value, match.Groups["line"].Success ? match.Groups["line"].Value : "live");
        }

        /// <summary>
        /// HoYoPlay games uninstall with <c>HoYoPlay\launcher.exe --uninstall_game=hk4e_global</c>.
        /// </summary>
        public static (string Launcher, string Game)? GetHoYoPlayGame(string uninstallString)
        {
            var match = Regex.Match(uninstallString ?? string.Empty,
                @"^""?(?<launcher>[^""]*\\launcher\.exe)""?\s+--uninstall_game=(?<game>[\w.-]+)",
                RegexOptions.IgnoreCase);
            return match.Success ? (match.Groups["launcher"].Value, match.Groups["game"].Value) : ((string, string)?)null;
        }

        #endregion Parsing

        #region Classification

        /// <summary>
        /// The games an installed application is or holds: none when it isn't a game or isn't installed anymore,
        /// several for a launcher with its games in its folder.
        /// </summary>
        public List<DetectedInstalledGame> Classify(InstalledProgram program)
        {
            if (launchers.Contains(program.Name) || launchers.Contains(CleanName(program.Name)) || developerTools.IsMatch(program.Name))
            {
                return new List<DetectedInstalledGame>();
            }

            var uninstall = program.UninstallString ?? string.Empty;
            var steamAppId = GetSteamAppId(uninstall);
            var riot = GetRiotProduct(uninstall);
            var hoyo = GetHoYoPlayGame(uninstall);
            if (steamAppId == null && riot == null && hoyo == null)
            {
                return ClassifyStandalone(program);
            }

            var game = steamAppId != null ? ClassifySteam(program, steamAppId)
                : riot != null ? ClassifyRiot(program, riot.Value.Client, riot.Value.Product, riot.Value.Patchline)
                : ClassifyHoYoPlay(program, hoyo.Value.Launcher, hoyo.Value.Game);
            return game == null ? new List<DetectedInstalledGame>() : new List<DetectedInstalledGame> { game };
        }

        private static DetectedInstalledGame ClassifySteam(InstalledProgram program, string appId)
        {
            // Steam leaves the registration of games removed outside of it.
            if (!SteamLibraries.IsAppInstalled(appId))
            {
                return null;
            }

            return new DetectedInstalledGame
            {
                Key = program.Key,
                Name = CleanName(program.Name),
                InstallDirectory = SteamLibraries.GetAppDirectory(appId) ?? ExistingDirectory(program.InstallLocation),
                Url = $"steam://rungameid/{appId}",
                SteamAppId = appId,
                Source = "Steam"
            };
        }

        private static DetectedInstalledGame ClassifyRiot(InstalledProgram program, string client, string product, string patchline)
        {
            var dir = ExistingDirectory(program.InstallLocation);
            if (product.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase) || dir == null || !File.Exists(client))
            {
                return null;
            }

            return new DetectedInstalledGame
            {
                Key = program.Key,
                Name = CleanName(program.Name),
                InstallDirectory = dir,
                Executable = client,
                Arguments = $"--launch-product={product} --launch-patchline={patchline}",
                Source = "Riot Games"
            };
        }

        private DetectedInstalledGame ClassifyHoYoPlay(InstalledProgram program, string launcher, string game)
        {
            string dir = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Cognosphere\HYP\1_0\{game}"))
                {
                    dir = ExistingDirectory(key?.GetValue("GameInstallPath") as string);
                }
            }
            catch
            {
            }

            // Registered but not downloaded.
            if (dir == null)
            {
                return null;
            }

            var exe = FindGameExecutable(dir, null, program.Name, out _);
            return new DetectedInstalledGame
            {
                Key = program.Key,
                Name = CleanName(program.Name),
                InstallDirectory = dir,
                Executable = exe ?? launcher,
                Arguments = exe == null ? $"--game={game}" : null,
                Source = "HoYoPlay"
            };
        }

        private List<DetectedInstalledGame> ClassifyStandalone(InstalledProgram program)
        {
            var result = new List<DetectedInstalledGame>();
            if (program.Kind == InstalledProgramKind.DriveRoot)
            {
                return ClassifyDriveRoot(program);
            }

            var icon = GetCommandPath(program.DisplayIcon);
            // "Uninstall Game" shortcuts sit next to the game, but start its uninstaller.
            if (program.Kind == InstalledProgramKind.Shortcut && (icon == null || IsHelperExecutable(icon) || uninstallShortcut.IsMatch(program.Name)))
            {
                return result;
            }

            var dir = GetProgramDirectory(program, icon);
            // Store clients ship Steam API files and engine runtimes but aren't games, e.g. "EA Desktop\EAUpdater.exe".
            if (dir == null || IsStoreClientFolder(dir))
            {
                return result;
            }

            var preferred = icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) && !IsHelperExecutable(icon)
                ? icon
                : null;
            var hosted = FindLauncherGames(program, dir, preferred);
            if (hosted.Count > 0)
            {
                return hosted;
            }

            var exe = FindGameExecutable(dir, preferred, program.Name, out var engineFound);
            if (exe == null)
            {
                return result;
            }

            var uninstall = program.UninstallString ?? string.Empty;
            var battleNet = uninstall.IndexOf("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) >= 0;
            var ea = uninstall.IndexOf("EAInstaller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                program.Publisher?.IndexOf("Electronic Arts", StringComparison.OrdinalIgnoreCase) >= 0;
            var repack = repackName.IsMatch(program.Name);
            var knownClient = knownGameClients.IsMatch(program.Name);
            if (!engineFound && !battleNet && !ea && !repack && !knownClient)
            {
                return result;
            }

            result.Add(new DetectedInstalledGame
            {
                Key = program.Key,
                Name = program.Kind == InstalledProgramKind.Folder
                    // Folder names are release names like "Some.Game.v1.2-CODEX", Unity knows the real one.
                    ? GetUnityProductName(exe) ?? CleanName(UnknownGameDetector.GetFolderGameName(program.Name))
                    : CleanName(program.Name),
                InstallDirectory = dir,
                Executable = exe,
                Arguments = exe.Equals(icon, StringComparison.OrdinalIgnoreCase) ? program.Arguments : null,
                // Steam API files name the Steam app, so its store data is found even when the name differs.
                SteamAppId = detector.InspectExecutable(exe)?.SteamAppId ?? UnknownGameDetector.FindSteamAppIdInGameFolder(dir),
                Source = battleNet ? "Battle.net" : ea ? "EA app" : null
            });
            return result;
        }

        /// <summary>
        /// Game installed straight into a drive, like a Ragnarok Online client in "D:\". Only the programs in the root
        /// are looked at, and the game gets no installation folder: a whole drive is never watched for saves.
        /// </summary>
        private List<DetectedInstalledGame> ClassifyDriveRoot(InstalledProgram program)
        {
            var result = new List<DetectedInstalledGame>();
            var root = GameMatcher.NormalizeDirectory(program.InstallLocation);
            List<(string Path, UnknownGameInfo Info)> games;
            try
            {
                games = Directory.EnumerateFiles(root, "*.exe")
                    .Where(a => !IsHelperExecutable(a))
                    .Select(a => (a, detector.InspectExecutable(a)))
                    .Where(a => a.Item2?.Engine != null)
                    .ToList();
            }
            catch
            {
                return result;
            }

            if (games.Count == 0)
            {
                return result;
            }

            var engine = games[0].Info.Engine;
            var engineName = singleGameEngines.Contains(engine) ? engine : null;
            var exe = FindGameExecutable(root, null, engineName ?? string.Empty, out _, 0);
            if (exe == null)
            {
                return result;
            }

            result.Add(new DetectedInstalledGame
            {
                Key = program.Key,
                Name = engineName ?? GetGameName(exe) ?? CleanName(Path.GetFileNameWithoutExtension(exe)),
                Executable = exe,
                // The launcher starts the client, which is how the running game is recognized.
                GameExecutable = games.Select(a => a.Path)
                    .Where(a => !a.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(GetFileSize)
                    .FirstOrDefault()
            });
            return result;
        }

        private static bool IsStoreClientFolder(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*.exe").Any(GameMatcher.IsStoreClient);
            }
            catch
            {
                return false;
            }
        }

        private static long GetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Games in the "games" folder of a launcher, like "GRYPHLINK\games\EndField Game". They're started with
        /// the launcher, which signs the player in and updates them.
        /// </summary>
        private List<DetectedInstalledGame> FindLauncherGames(InstalledProgram program, string dir, string launcher)
        {
            var result = new List<DetectedInstalledGame>();
            var gamesDir = Path.Combine(dir, "games");
            if (launcher == null || !Directory.Exists(gamesDir))
            {
                return result;
            }

            string source = null;
            foreach (var gameDir in Directory.GetDirectories(gamesDir))
            {
                var folder = Path.GetFileName(gameDir);
                var exe = FindGameExecutable(gameDir, null, folder, out var engineFound);
                if (exe == null || !engineFound)
                {
                    continue;
                }

                source = source ?? GetProductName(launcher) ?? CleanName(program.Name);
                var installDir = GameMatcher.NormalizeDirectory(gameDir);
                result.Add(new DetectedInstalledGame
                {
                    Key = $"{program.Key}|{folder}",
                    Name = GetGameName(exe) ?? CleanName(Regex.Replace(folder, @"\s+game$", string.Empty, RegexOptions.IgnoreCase)),
                    InstallDirectory = installDir,
                    Executable = launcher,
                    GameExecutable = exe,
                    SteamAppId = detector.InspectExecutable(exe)?.SteamAppId ?? UnknownGameDetector.FindSteamAppIdInGameFolder(installDir),
                    Source = source
                });
            }

            return result;
        }

        /// <summary>
        /// Product name of a game executable: from the Unity player settings, otherwise from its version information.
        /// </summary>
        public static string GetGameName(string exe)
        {
            var unity = GetUnityProductName(exe);
            if (unity != null)
            {
                return unity;
            }

            var product = GetProductName(exe);
            return product == null || Regex.IsMatch(product, @"\b(unity|unreal|launcher|application)\b", RegexOptions.IgnoreCase) ? null : CleanName(product);
        }

        /// <summary>
        /// Product name in the Unity player settings: "Game_Data\app.info" holds the company and the product.
        /// </summary>
        public static string GetUnityProductName(string exe)
        {
            try
            {
                var appInfo = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + "_Data", "app.info");
                if (File.Exists(appInfo))
                {
                    var lines = File.ReadAllLines(appInfo);
                    if (lines.Length > 1 && !lines[1].IsNullOrWhiteSpace())
                    {
                        return CleanName(lines[1].Trim());
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string GetProductName(string exe)
        {
            try
            {
                var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
                return product.IsNullOrEmpty() ? null : product;
            }
            catch
            {
                return null;
            }
        }

        private static string ExistingDirectory(string path)
        {
            var dir = GameMatcher.NormalizeDirectory(path?.Trim().Trim('"').Replace('/', '\\'));
            return dir != null && Directory.Exists(dir) ? dir : null;
        }

        private static bool IsSystemDirectory(string dir)
        {
            var trimmed = dir.TrimEnd('\\');
            if (trimmed.Length <= 3)
            {
                return true;
            }

            var system = new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile
            }.Select(a => Environment.GetFolderPath(a).TrimEnd('\\')).Where(a => !a.IsNullOrEmpty());
            // Shared program data holds launcher services and installer caches, not games.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
            return system.Any(a => trimmed.Equals(a, StringComparison.OrdinalIgnoreCase)) ||
                (!programData.IsNullOrEmpty() && trimmed.StartsWith(programData + "\\", StringComparison.OrdinalIgnoreCase)) ||
                trimmed.IndexOf(@"\Package Cache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.IndexOf(@"\Common Files", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trimmed.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Folder of the application: its install location, or the folder holding both its icon and uninstaller.
        /// </summary>
        private static string GetProgramDirectory(InstalledProgram program, string icon)
        {
            if (!program.InstallLocation.IsNullOrWhiteSpace())
            {
                // Uninstalled outside of its uninstaller: Windows keeps the registration of a folder that's gone.
                var location = ExistingDirectory(program.InstallLocation);
                return location == null || IsSystemDirectory(location) ? null : location;
            }

            var iconDir = icon == null ? null : ExistingDirectory(Path.GetDirectoryName(icon));
            var uninstallPath = GetCommandPath(program.UninstallString);
            var uninstallDir = uninstallPath == null || !uninstallPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? null
                : ExistingDirectory(Path.GetDirectoryName(uninstallPath));
            if (uninstallDir != null && IsSystemDirectory(uninstallDir))
            {
                uninstallDir = null;
            }

            string dir;
            if (iconDir != null && uninstallDir != null)
            {
                // "Game\launcher\launcher.exe" and "Game\uninstall.exe" are both in "Game".
                dir = CommonDirectory(iconDir, uninstallDir) ?? iconDir;
            }
            else
            {
                dir = uninstallDir ?? iconDir;
            }

            return dir == null || IsSystemDirectory(dir) ? null : dir;
        }

        private static string CommonDirectory(string first, string second)
        {
            var a = first.TrimEnd('\\').Split('\\');
            var b = second.TrimEnd('\\').Split('\\');
            var count = 0;
            while (count < a.Length && count < b.Length && a[count].Equals(b[count], StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }

            // A common drive root isn't a game folder.
            return count < 2 ? null : string.Join("\\", a.Take(count)) + "\\";
        }

        private static bool IsHelperExecutable(string path)
        {
            return GameMatcher.IsNonGameExecutable(path) || helperExecutable.IsMatch(Path.GetFileNameWithoutExtension(path));
        }

        /// <summary>
        /// Executable to start the game: the application's own icon when it's a program, otherwise the best
        /// candidate in the folder. Reports whether any executable in the folder belongs to a game engine.
        /// </summary>
        public string FindGameExecutable(string dir, string preferred, string name, out bool engineFound, int depthLimit = maxDepth)
        {
            engineFound = false;
            var candidates = new List<(string Path, int Depth)>();
            var pending = new Queue<(string Dir, int Depth)>();
            pending.Enqueue((dir, 0));
            var visited = 0;
            while (pending.Count > 0 && visited < maxScannedDirectories)
            {
                var (current, depth) = pending.Dequeue();
                visited++;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current, "*.exe"))
                    {
                        if (!IsHelperExecutable(file))
                        {
                            candidates.Add((file, depth));
                        }
                    }

                    if (depth < depthLimit)
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(current))
                        {
                            var folder = Path.GetFileName(subDir);
                            if (!skippedFolders.Contains(folder) && !folder.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                            {
                                pending.Enqueue((subDir, depth + 1));
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var normalizedName = SavePaths.NormalizeName(CleanName(name));
            (string Path, int Score)? best = null;
            foreach (var (path, depth) in candidates.OrderBy(a => a.Depth).Take(maxInspectedExecutables))
            {
                var engine = detector.InspectExecutable(path)?.Engine != null;
                engineFound |= engine;
                var exeName = SavePaths.NormalizeName(Path.GetFileNameWithoutExtension(path));
                var score = (engine ? 100 : 0) + (depth == 0 ? 20 : depth == 1 ? 10 : 0);
                if (!normalizedName.IsNullOrEmpty() && !exeName.IsNullOrEmpty() &&
                    (normalizedName.Contains(exeName) || exeName.Contains(normalizedName)))
                {
                    score += 30;
                }

                if (Path.GetFileNameWithoutExtension(path).IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score -= 15;
                }

                try
                {
                    score += (int)Math.Min(10, new FileInfo(path).Length / (20 * 1024 * 1024));
                }
                catch
                {
                }

                if (best == null || score > best.Value.Score)
                {
                    best = (path, score);
                }
            }

            // The registered program is how its publisher wants it started, e.g. the launcher of an online game.
            return preferred ?? best?.Path;
        }

        #endregion Classification

        /// <summary>
        /// All installed games found on this PC. Applications already classified can be skipped with <paramref name="skip"/>.
        /// </summary>
        public List<DetectedInstalledGame> Scan(Func<InstalledProgram, bool> skip = null)
        {
            var result = new List<DetectedInstalledGame>();
            foreach (var program in ReadInstalledPrograms())
            {
                if (skip?.Invoke(program) == true)
                {
                    continue;
                }

                try
                {
                    result.AddRange(Classify(program));
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to check installed application {program.Name}");
                }
            }

            result.AddRange(ReadEpicGames());
            return result;
        }
    }

    /// <summary>
    /// Steam library folders, to tell installed Steam games from leftover registrations.
    /// </summary>
    public static class SteamLibraries
    {
        private static List<string> GetLibraryDirectories()
        {
            var steam = AchievementReaders.FindSteamPath();
            var result = new List<string>();
            if (steam == null)
            {
                return result;
            }

            result.Add(steam);
            var config = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(config))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(config), @"""path""\s+""([^""]+)"""))
                {
                    var path = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (!result.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(path);
                    }
                }
            }

            return result;
        }

        private static string GetManifest(string appId)
        {
            return GetLibraryDirectories()
                .Select(a => Path.Combine(a, "steamapps", $"appmanifest_{appId}.acf"))
                .FirstOrDefault(File.Exists);
        }

        public static bool IsAppInstalled(string appId)
        {
            return GetManifest(appId) != null;
        }

        public static string GetAppDirectory(string appId)
        {
            var manifest = GetManifest(appId);
            if (manifest == null)
            {
                return null;
            }

            var match = Regex.Match(File.ReadAllText(manifest), @"""installdir""\s+""([^""]+)""");
            if (!match.Success)
            {
                return null;
            }

            var dir = Path.Combine(Path.GetDirectoryName(manifest), "common", match.Groups[1].Value);
            return Directory.Exists(dir) ? GameMatcher.NormalizeDirectory(dir) : null;
        }
    }
}
