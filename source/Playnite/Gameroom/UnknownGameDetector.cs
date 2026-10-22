using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom
{
    public class UnknownGameInfo
    {
        public string Executable { get; set; }
        /// <summary>Root folder of the game installation.</summary>
        public string RootDirectory { get; set; }
        public string SuggestedName { get; set; }
        public string Engine { get; set; }
        public string SteamAppId { get; set; }
        public List<string> Evidence { get; set; } = new List<string>();
    }

    /// <summary>
    /// Decides whether an executable that isn't in the library looks like a game,
    /// based on files shipped next to it (engines, Steam API, Steam emulators) and how it's displayed.
    /// </summary>
    public class UnknownGameDetector
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string RagnarokOnline = "Ragnarok Online";

        // Applications that commonly run fullscreen or ship game-like files but aren't games.
        private static readonly HashSet<string> knownApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "msedgewebview2",
            "discord", "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "galaxyclient", "gog galaxy",
            "origin", "eadesktop", "ealauncher", "battle.net", "agent", "upc", "ubisoftconnect", "uplay", "rockstarlauncher",
            "socialclubhelper", "playnite.desktopapp", "playnite.fullscreenapp", "launchbox", "bigbox", "gamebar", "xboxapp",
            "obs64", "obs32", "streamlabs obs", "vlc", "mpc-hc", "mpc-hc64", "mpc-be64", "potplayermini64", "wmplayer", "spotify",
            "teams", "ms-teams", "slack", "telegram", "whatsapp", "zoom", "skype", "code", "devenv", "rider64", "idea64",
            "powershell", "pwsh", "cmd", "windowsterminal", "conhost", "notepad", "notepad++", "taskmgr", "mmc", "regedit",
            "applicationframehost", "systemsettings", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
            "textinputhost", "lockapp", "dwm", "nvidia share", "nvcontainer", "radeonsoftware", "msiafterburner",
            "rtss", "parsecd", "moonlight", "sunshine", "anydesk", "teamviewer", "mstsc", "vmware", "virtualboxvm",
            "photoshop", "blender", "unity", "unityhub", "unrealeditor", "ue4editor", "godot", "retroarch", "dolphin",
            "pcsx2", "pcsx2-qt", "rpcs3", "yuzu", "ryujinx", "cemu", "duckstation-qt-x64-releaseltcg", "ppsspp", "ppssppwindows64",
            "xenia", "xenia_canary", "citra-qt", "lime3ds", "melonds", "mgba", "snes9x-x64", "project64", "flycast", "redream"
        };

        private static readonly string[] steamEmulatorMarkers =
        {
            "steam_emu.ini", "SmartSteamEmu.ini", "CODEX.ini", "cream_api.ini", "OnlineFix.ini", "OnlineFix64.dll",
            "ALI213.ini", "3DMGAME.ini", "CPY.ini", "steam_api64.cdx", "Goldberg.ini", "ColdClientLoader.ini",
            "EMPRESS.ini", "RUNE.ini", "dodi.ini", "hlm.ini", "valve.ini", "SteamConfig.ini"
        };

        private static readonly Regex genericProductName = new Regex(
            @"^(unity|unreal ?engine.*|unrealgame|ue4game|ue5game|godot.*|game|launcher|shipping|nw\.js|electron|rpg ?maker.*|ren'?py|gamemaker.*|application|setup)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex releaseTagSuffix = new Regex(
            @"[\s._-]*(codex|plaza|cpy|empress|rune|tenoke|skidrow|reloaded|razor1911|hoodlum|flt|dodi|fitgirl|elamigos|gog|repack|multi\d+|v?\d+(\.\d+)+.*|build[\s._-]?\d+.*|update.*|x64|x86|win64|win32)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] wrapperDirectoryNames =
        {
            "bin", "bin64", "bin32", "x64", "x86", "win64", "win32", "binaries", "game", "exe", "app", "retail", "release"
        };

        private readonly ConcurrentDictionary<string, UnknownGameInfo> cache = new ConcurrentDictionary<string, UnknownGameInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly string windowsDir = GameMatcher.NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        private readonly string programDir = GameMatcher.NormalizeDirectory(PlaynitePaths.ProgramPath);

        /// <summary>
        /// Returns game information when the process looks like a game, otherwise null.
        /// File system checks are cached per executable, window state is evaluated every time.
        /// </summary>
        public UnknownGameInfo Inspect(ProcessSnapshot process)
        {
            if (!process.HasWindow || process.Path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var info = cache.GetOrAdd(process.Path, InspectFiles);
            if (info == null)
            {
                return null;
            }

            // File evidence alone is strong enough for engines and Steam API.
            if (info.Engine != null)
            {
                return info;
            }

            // Weaker evidence needs the game to be on screen in fullscreen.
            if (info.Evidence.Count > 0 && process.IsFullscreen && process.IsForeground)
            {
                return info;
            }

            return null;
        }

        /// <summary>
        /// Game information from the files next to an executable, without looking at a running process.
        /// Null when nothing points to a game.
        /// </summary>
        public UnknownGameInfo InspectExecutable(string exePath)
        {
            return exePath.IsNullOrWhiteSpace() ? null : cache.GetOrAdd(exePath, InspectFiles);
        }

        private UnknownGameInfo InspectFiles(string exePath)
        {
            try
            {
                var path = GameMatcher.NormalizePath(exePath);
                if (path == null ||
                    (windowsDir != null && path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) ||
                    (programDir != null && path.StartsWith(programDir, StringComparison.OrdinalIgnoreCase)) ||
                    path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return null;
                }

                var exeName = Path.GetFileNameWithoutExtension(path);
                if (knownApplications.Contains(exeName) || GameMatcher.IsNonGameExecutable(path))
                {
                    return null;
                }

                var exeDir = Path.GetDirectoryName(path);
                var info = new UnknownGameInfo { Executable = path, RootDirectory = exeDir };
                var files = SafeGetFiles(exeDir);
                var dirs = SafeGetDirectories(exeDir);

                bool hasFile(string name) => files.Contains(name);

                if (hasFile("UnityPlayer.dll") || dirs.Contains(exeName + "_Data"))
                {
                    info.Engine = "Unity";
                }
                else if (Regex.IsMatch(path, @"\\Binaries\\Win(64|32)\\[^\\]+\.exe$", RegexOptions.IgnoreCase))
                {
                    info.Engine = "Unreal Engine";
                    info.RootDirectory = GetUnrealRoot(exeDir);
                }
                else if (files.Any(a => a.Equals(exeName + ".pck", StringComparison.OrdinalIgnoreCase)))
                {
                    info.Engine = "Godot";
                }
                else if (hasFile("data.win") || hasFile("options.ini") && hasFile("audiogroup1.dat"))
                {
                    info.Engine = "GameMaker";
                }
                else if (dirs.Contains("renpy") || dirs.Contains("game") && hasFile(exeName + ".py"))
                {
                    info.Engine = "Ren'Py";
                }
                else if (files.Any(a => a.StartsWith("RGSS", StringComparison.OrdinalIgnoreCase) && a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ||
                    (dirs.Contains("www") && hasFile("nw.dll")))
                {
                    info.Engine = "RPG Maker";
                }
                else if (files.Any(a => a.EndsWith(".grf", StringComparison.OrdinalIgnoreCase)))
                {
                    // Gravity resource files of Ragnarok Online clients, official and private servers.
                    info.Engine = RagnarokOnline;
                }
                else if (hasFile("steam_api.dll") || hasFile("steam_api64.dll"))
                {
                    info.Engine = "Steam";
                }

                var emuMarker = steamEmulatorMarkers.FirstOrDefault(a => hasFile(a));
                if (emuMarker != null || dirs.Contains("steam_settings"))
                {
                    info.Evidence.Add($"Steam emulator ({emuMarker ?? "steam_settings"})");
                    info.Engine = info.Engine ?? "Steam";
                }

                foreach (var marker in new[] { "bink2w64.dll", "binkw32.dll", "binkw64.dll", "fmod.dll", "fmod64.dll", "fmodex.dll", "xinput1_3.dll", "PhysX3_x64.dll", "PhysXLoader.dll", "openvr_api.dll", "d3dx9_43.dll", "galaxy.dll", "galaxy64.dll", "EOSSDK-Win64-Shipping.dll" })
                {
                    if (hasFile(marker))
                    {
                        info.Evidence.Add(marker);
                    }
                }

                if (files.Any(a => a.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)))
                {
                    info.Evidence.Add("game archives");
                }

                if (Regex.IsMatch(path, @"\\(steamapps\\common|epic games|gog galaxy\\games|games|juegos)\\", RegexOptions.IgnoreCase))
                {
                    info.Evidence.Add("game library folder");
                }

                if (info.Engine == null && info.Evidence.Count == 0)
                {
                    return null;
                }

                if (info.Engine != "Unreal Engine")
                {
                    info.RootDirectory = GetRootDirectory(exeDir);
                }

                info.SteamAppId = FindSteamAppId(exeDir, info.RootDirectory) ?? FindSteamAppIdInGameFolder(info.RootDirectory);
                info.SuggestedName = GetSuggestedName(path, info.RootDirectory);
                return info;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to inspect {exePath}");
                return null;
            }
        }

        private static string GetUnrealRoot(string exeDir)
        {
            // <Root>\<Project>\Binaries\Win64\Game-Win64-Shipping.exe
            var projectDir = Directory.GetParent(exeDir)?.Parent;
            if (projectDir == null)
            {
                return exeDir;
            }

            var rootDir = projectDir.Parent;
            if (rootDir != null && Directory.Exists(Path.Combine(rootDir.FullName, "Engine")))
            {
                return rootDir.FullName;
            }

            return projectDir.FullName;
        }

        private static string GetRootDirectory(string exeDir)
        {
            var dir = new DirectoryInfo(exeDir);
            while (dir.Parent != null && wrapperDirectoryNames.Contains(dir.Name, StringComparer.OrdinalIgnoreCase) && dir.Parent.Parent != null)
            {
                dir = dir.Parent;
            }

            return dir.FullName;
        }

        /// <summary>
        /// Looks for Steam API or Steam emulator config in the game folder and up to three levels below
        /// (e.g. "bin\x64", "Game\Binaries\Win64" or Unity's "Game_Data\Plugins\x86_64").
        /// Walking the whole installation would be too slow for large games.
        /// </summary>
        public static string FindSteamAppIdInGameFolder(string rootDir)
        {
            // In a drive root the folders below are other games, like a game installed straight into "D:\".
            if (rootDir.IsNullOrWhiteSpace() || rootDir.TrimEnd('\\').Length <= 2 || !Directory.Exists(rootDir))
            {
                return null;
            }

            var pending = new Queue<(string dir, int depth)>();
            pending.Enqueue((rootDir, 0));
            var visited = 0;
            while (pending.Count > 0 && visited < 200)
            {
                var (dir, depth) = pending.Dequeue();
                visited++;
                var id = FindSteamAppId(dir, dir);
                if (id != null)
                {
                    return id;
                }

                if (depth >= 3)
                {
                    continue;
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(dir))
                    {
                        pending.Enqueue((subDir, depth + 1));
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        public static string FindSteamAppId(string exeDir, string rootDir)
        {
            foreach (var dir in new[] { exeDir, rootDir }.Distinct())
            {
                foreach (var candidate in new[] { "steam_appid.txt", @"steam_settings\steam_appid.txt" })
                {
                    var file = Path.Combine(dir, candidate);
                    if (File.Exists(file))
                    {
                        var id = File.ReadAllText(file).Trim();
                        if (Regex.IsMatch(id, @"^\d+$"))
                        {
                            return id;
                        }
                    }
                }

                foreach (var ini in new[] { "steam_emu.ini", "CODEX.ini", "SmartSteamEmu.ini", "cream_api.ini", "OnlineFix.ini", "EMPRESS.ini", "RUNE.ini", "ColdClientLoader.ini" })
                {
                    var file = Path.Combine(dir, ini);
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var match = Regex.Match(File.ReadAllText(file), @"^\s*(AppId|appid|RealAppId)\s*=\s*(\d+)", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        return match.Groups[2].Value;
                    }
                }
            }

            return null;
        }

        private static string GetSuggestedName(string exePath, string rootDir)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(exePath);
                var product = version.ProductName?.Trim();
                if (!product.IsNullOrWhiteSpace() && !genericProductName.IsMatch(product) && product.Any(char.IsLetter))
                {
                    return product;
                }
            }
            catch
            {
            }

            var name = GetFolderGameName(Path.GetFileName(rootDir.TrimEnd('\\')));
            return name.IsNullOrWhiteSpace() ? Path.GetFileNameWithoutExtension(exePath) : name;
        }

        /// <summary>
        /// Game name from a release folder name like "Some.Game.v1.2-CODEX".
        /// </summary>
        public static string GetFolderGameName(string folder)
        {
            var name = releaseTagSuffix.Replace(folder ?? string.Empty, string.Empty);
            return Regex.Replace(name, @"[._]+", " ").Trim();
        }

        private static HashSet<string> SafeGetFiles(string dir)
        {
            try
            {
                return new HashSet<string>(Directory.EnumerateFiles(dir).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static HashSet<string> SafeGetDirectories(string dir)
        {
            try
            {
                return new HashSet<string>(Directory.EnumerateDirectories(dir).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
