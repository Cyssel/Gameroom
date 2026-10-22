using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Gameroom
{
    public class SaveWriteCandidate
    {
        public string Path { get; set; }
        public int FileCount { get; set; }
        public string Reason { get; set; }

        public override string ToString()
        {
            return $"{Path} ({Reason}, {FileCount} files)";
        }
    }

    /// <summary>
    /// Records files written to the usual save roots while games run, so save folders of games
    /// without any known location (including cracked or obscure games) can be discovered.
    /// Writes can't be attributed to a process, so candidates are filtered by the game's name and app id.
    /// </summary>
    public class SaveWriteWatcher : IDisposable
    {
        private const int maxRecordedFiles = 20000;
        private static readonly ILogger logger = LogManager.GetLogger();

        private static readonly Regex noisePath = new Regex(
            @"\\(temp|tmp|cache|caches|cache2|gpucache|code cache|shadercache|d3dscache|nvidia|amd|intel|microsoft|windows|packages|google|mozilla|bravesoftware|opera software|discord|spotify|steam\\(htmlcache|logs|appcache|config)|epicgameslauncher|crashdumps|crashes|logs?|inetcache|webcache|thumbnails|recent|onedrive|dropbox|jetbrains|code|npm|nuget|pip|gameroom|playnite|obs-studio|nvidia corporation)(\\|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex noiseFile = new Regex(
            @"\.(log|tmp|temp|etl|dmp|mdmp|lock|lck|pid|crdownload|part|db-journal|db-wal|db-shm)$|^~|^thumbs\.db$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex saveKeyword = new Regex(
            @"^(save|saves|savegame|savegames|savedata|saved|saved games|profile|profiles|userdata|player|players|slot\d*|remote)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex saveFileExtension = new Regex(
            @"\.(sav|save|savegame|sl2|ess|fos|lsv|dsv|bak)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object watchLock = new object();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<Guid, HashSet<string>> sessions = new Dictionary<Guid, HashSet<string>>();
        private readonly Dictionary<Guid, string> sessionInstallDirs = new Dictionary<Guid, string>();

        public static List<string> GetWatchRoots()
        {
            return new[]
            {
                SavePaths.Documents,
                SavePaths.SavedGames,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SavePaths.LocalLow,
                SavePaths.PublicDocuments,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            }
            .Where(a => !a.IsNullOrWhiteSpace() && Directory.Exists(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        }

        public void Dispose()
        {
            lock (watchLock)
            {
                StopWatchers();
                sessions.Clear();
            }
        }

        public void StartSession(Guid gameId, string installDir)
        {
            lock (watchLock)
            {
                if (sessions.ContainsKey(gameId))
                {
                    return;
                }

                sessions[gameId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sessionInstallDirs[gameId] = installDir;
                if (watchers.Count == 0)
                {
                    StartWatchers(GetWatchRoots());
                }

                if (!installDir.IsNullOrWhiteSpace() && Directory.Exists(installDir) &&
                    !watchers.Any(a => SavePaths.IsSubPathOrEqual(installDir, a.Path)))
                {
                    StartWatchers(new List<string> { installDir });
                }
            }
        }

        /// <summary>
        /// Files written so far during a running session.
        /// </summary>
        public List<string> PeekSession(Guid gameId)
        {
            lock (watchLock)
            {
                return sessions.TryGetValue(gameId, out var files) ? files.ToList() : new List<string>();
            }
        }

        /// <summary>
        /// Stops recording for a game and returns the files written during its session.
        /// </summary>
        public List<string> EndSession(Guid gameId)
        {
            lock (watchLock)
            {
                if (!sessions.TryGetValue(gameId, out var files))
                {
                    return new List<string>();
                }

                sessions.Remove(gameId);
                sessionInstallDirs.Remove(gameId);
                if (sessions.Count == 0)
                {
                    StopWatchers();
                }
                else
                {
                    // Stop watching folders of games that are no longer running.
                    foreach (var watcher in watchers.ToList())
                    {
                        if (!GetWatchRoots().Contains(watcher.Path, StringComparer.OrdinalIgnoreCase) &&
                            !sessionInstallDirs.Values.Any(a => SavePaths.IsSubPathOrEqual(a, watcher.Path)))
                        {
                            watcher.Dispose();
                            watchers.Remove(watcher);
                        }
                    }
                }

                return files.ToList();
            }
        }

        private void StartWatchers(List<string> roots)
        {
            foreach (var root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };

                    watcher.Created += Watcher_Changed;
                    watcher.Changed += Watcher_Changed;
                    watcher.Renamed += Watcher_Renamed;
                    watcher.Error += Watcher_Error;
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to watch {root} for save files.");
                }
            }
        }

        private void StopWatchers()
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            watchers.Clear();
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            logger.Debug($"Save watcher error: {e.GetException()?.Message}");
        }

        private void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            Record(e.FullPath);
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            Record(e.FullPath);
        }

        private void Record(string path)
        {
            if (noiseFile.IsMatch(Path.GetFileName(path)) || noisePath.IsMatch(Path.GetDirectoryName(path)))
            {
                return;
            }

            lock (watchLock)
            {
                foreach (var files in sessions.Values)
                {
                    if (files.Count < maxRecordedFiles)
                    {
                        files.Add(path);
                    }
                }
            }
        }

        /// <summary>
        /// Picks folders that most likely hold saves of the game from files written during its session.
        /// </summary>
        public static List<SaveWriteCandidate> FindCandidates(IEnumerable<string> writtenFiles, string gameName, string installDir, string steamAppId)
        {
            var roots = GetWatchRoots().Select(a => GameMatcher.NormalizePath(a)).Where(a => a != null).OrderByDescending(a => a.Length).ToList();
            var install = GameMatcher.NormalizePath(installDir);
            var nameKeys = GetNameKeys(gameName);
            var candidates = new Dictionary<string, SaveWriteCandidate>(StringComparer.OrdinalIgnoreCase);

            void addCandidate(string path, string reason)
            {
                if (!candidates.TryGetValue(path, out var candidate))
                {
                    candidate = new SaveWriteCandidate { Path = path, Reason = reason };
                    candidates.Add(path, candidate);
                }

                candidate.FileCount++;
            }

            foreach (var file in writtenFiles)
            {
                var path = GameMatcher.NormalizePath(file);
                if (path == null)
                {
                    continue;
                }

                if (install != null && SavePaths.IsSubPathOrEqual(path, install))
                {
                    var relative = path.Substring(install.Length).Trim('\\').Split('\\');
                    var current = install;
                    var found = false;
                    for (int i = 0; i < relative.Length - 1; i++)
                    {
                        current += "\\" + relative[i];
                        if (saveKeyword.IsMatch(relative[i]))
                        {
                            addCandidate(current, "game folder");
                            found = true;
                            break;
                        }
                    }

                    if (!found && saveFileExtension.IsMatch(path))
                    {
                        addCandidate(Path.GetDirectoryName(path), "save file");
                    }

                    continue;
                }

                var root = roots.FirstOrDefault(a => SavePaths.IsSubPathOrEqual(path, a));
                if (root == null)
                {
                    continue;
                }

                var segments = path.Substring(root.Length).Trim('\\').Split('\\');
                var dir = root;
                // Last segment is the file itself.
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    dir += "\\" + segments[i];
                    var segment = SavePaths.NormalizeName(segments[i]);
                    if (segment.Length == 0)
                    {
                        continue;
                    }

                    if (!steamAppId.IsNullOrWhiteSpace() && segments[i] == steamAppId)
                    {
                        addCandidate(dir, "app id");
                        break;
                    }

                    // A developer folder alone is too broad, "Developer\Game" is matched by the next segment.
                    if (MatchesName(segment, nameKeys))
                    {
                        addCandidate(dir, "name");
                        break;
                    }
                }
            }

            return candidates.Values.OrderByDescending(a => a.FileCount).ToList();
        }

        private static List<string> GetNameKeys(string gameName)
        {
            var keys = new List<string>();
            if (gameName.IsNullOrWhiteSpace())
            {
                return keys;
            }

            keys.Add(SavePaths.NormalizeName(gameName));
            // "Game: Subtitle" or "Game - Subtitle" are often stored under the main title only.
            var main = Regex.Split(gameName, @"\s*[:\-–]\s+").FirstOrDefault();
            if (!main.IsNullOrWhiteSpace())
            {
                keys.Add(SavePaths.NormalizeName(main));
            }

            return keys.Where(a => a.Length >= 4).Distinct().ToList();
        }

        private static bool MatchesName(string segment, List<string> nameKeys)
        {
            foreach (var key in nameKeys)
            {
                if (segment == key)
                {
                    return true;
                }

                // Allow small additions like "HollowKnight" vs "HollowKnightSaves" or "Fallout4" vs "Fallout4VR".
                if (segment.Length >= 5 && key.Length >= 5 && (segment.StartsWith(key) || key.StartsWith(segment)) &&
                    Math.Abs(segment.Length - key.Length) <= Math.Max(4, key.Length / 2))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
