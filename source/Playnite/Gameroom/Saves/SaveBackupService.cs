using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.Gameroom
{
    public class SaveBackupResult
    {
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public string Message { get; set; }
        public SaveBackup Backup { get; set; }
        public Game Game { get; set; }
        public SaveBackupTrigger Trigger { get; set; }
    }

    public class SaveBackupManifest
    {
        public class Location
        {
            public int Index { get; set; }
            public string Template { get; set; }
            public string Path { get; set; }
        }

        public int Version { get; set; } = 1;
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public DateTime Created { get; set; }
        public string Machine { get; set; }
        public string SteamAppId { get; set; }
        public string ContentHash { get; set; }
        public List<Location> Locations { get; set; } = new List<Location>();
    }

    /// <summary>
    /// Creates and restores versioned save backups (zip archives) of games.
    /// </summary>
    public class SaveBackupService : IDisposable
    {
        public const string ManifestFileName = "gameroom-backup.json";
        private const long maxBackupSize = 2L * 1024 * 1024 * 1024;
        private const int maxBackupFiles = 20000;
        // Saves of running games are checked this often, so saving in the game is backed up shortly after.
        private const int liveCheckSeconds = 60;
        // Backups made while playing are kept apart from the per session ones, only the newest few are kept.
        private const int maxWhilePlayingBackups = 5;
        private static readonly ILogger logger = LogManager.GetLogger();

        // Data inside save folders that isn't worth keeping.
        private static readonly Regex excludedDirectory = new Regex(
            @"^(logs?|cache|caches|shadercache|shader_cache|shaders|gpucache|crashes|crashdumps|crashreports|webcache|temp|tmp|screenshots|videos)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex excludedFile = new Regex(
            @"\.(log|tmp|dmp|mdmp|etl|lock)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IGameDatabaseMain database;
        private readonly GameroomDatabase gameroomDb;
        private readonly Func<GameroomSettings> getSettings;
        private readonly SaveWriteWatcher writeWatcher = new SaveWriteWatcher();
        private readonly ConcurrentDictionary<Guid, object> gameLocks = new ConcurrentDictionary<Guid, object>();
        private readonly ConcurrentDictionary<Guid, Game> runningGames = new ConcurrentDictionary<Guid, Game>();
        private readonly System.Threading.Timer liveCheckTimer;
        private int liveCheckActive;

        public SaveLocationResolver Resolver { get; }

        private GameroomSettings settings => getSettings();

        public event EventHandler<SaveBackupResult> BackupCompleted;

        public string BackupDirectory => settings.SaveBackupDirectory.IsNullOrWhiteSpace()
            ? Path.Combine(PlaynitePaths.ConfigRootPath, "SaveBackups")
            : settings.SaveBackupDirectory;

        public SaveBackupService(IGameDatabaseMain database, GameroomDatabase gameroomDb, Func<GameroomSettings> getSettings)
        {
            this.database = database;
            this.gameroomDb = gameroomDb;
            this.getSettings = getSettings;
            Resolver = new SaveLocationResolver(gameroomDb);
            liveCheckTimer = new System.Threading.Timer(_ => CheckRunningGames(), null, liveCheckSeconds * 1000, liveCheckSeconds * 1000);
        }

        public void Dispose()
        {
            liveCheckTimer.Dispose();
            writeWatcher.Dispose();
        }

        #region Session hooks

        public void OnSessionStarted(Game game)
        {
            if (!settings.SaveBackupsEnabled)
            {
                return;
            }

            runningGames[game.Id] = game;
            if (!settings.LearnSaveLocations)
            {
                return;
            }

            try
            {
                writeWatcher.StartSession(game.Id, SaveLocationResolver.GetInstallDir(game));
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to start save write monitoring for {game.Name}");
            }
        }

        /// <summary>
        /// Stops watching file writes of a session that didn't really happen (game failed to start).
        /// </summary>
        public void OnSessionCancelled(Game game)
        {
            runningGames.TryRemove(game.Id, out _);
            writeWatcher.EndSession(game.Id);
        }

        /// <summary>
        /// Backs up saves of running games that changed since the last backup.
        /// </summary>
        internal void CheckRunningGames()
        {
            if (System.Threading.Interlocked.Exchange(ref liveCheckActive, 1) == 1)
            {
                return;
            }

            try
            {
                foreach (var game in runningGames.Values.ToList())
                {
                    try
                    {
                        if (!settings.SaveBackupsEnabled || gameroomDb.GetGameData(game.Id)?.SaveBackupsDisabled == true)
                        {
                            continue;
                        }

                        var writtenFiles = writeWatcher.PeekSession(game.Id);
                        if (writtenFiles.Count > 0)
                        {
                            LearnLocations(game, writtenFiles);
                        }

                        BackupGame(game, SaveBackupTrigger.WhilePlaying);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, $"Failed to check saves of running game {game.Name}");
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref liveCheckActive, 0);
            }
        }

        public void OnSessionEnded(Game game)
        {
            runningGames.TryRemove(game.Id, out _);
            var writtenFiles = writeWatcher.EndSession(game.Id);
            if (!settings.SaveBackupsEnabled)
            {
                return;
            }

            Task.Run(async () =>
            {
                // Give the game and store clients a moment to finish writing saves.
                await Task.Delay(5000);
                try
                {
                    if (writtenFiles.Count > 0)
                    {
                        LearnLocations(game, writtenFiles);
                    }

                    var data = gameroomDb.GetGameData(game.Id);
                    if (data?.SaveBackupsDisabled != true)
                    {
                        BackupGame(game, SaveBackupTrigger.SessionEnded);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to back up saves of {game.Name} after session.");
                }
            });
        }

        #endregion Session hooks

        #region Locations

        public List<SaveWriteCandidate> LearnLocations(Game game, List<string> writtenFiles)
        {
            var data = gameroomDb.GetOrCreateGameData(game.Id);
            var installDir = SaveLocationResolver.GetInstallDir(game);
            var steamAppId = SaveLocationResolver.GetSteamAppId(game, data);
            var existing = Resolver.Resolve(game);
            var learned = new List<SaveWriteCandidate>();
            foreach (var candidate in SaveWriteWatcher.FindCandidates(writtenFiles, game.Name, installDir, steamAppId))
            {
                if (existing.Any(a => SavePaths.IsSubPathOrEqual(candidate.Path, a.Path) || SavePaths.IsSubPathOrEqual(a.Path, candidate.Path)))
                {
                    continue;
                }

                var template = SavePaths.Contract(candidate.Path, installDir);
                if (data.SaveLocations.Any(a => string.Equals(a.Path, template, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                data.SaveLocations.Add(new SaveLocation { Path = template, Source = SaveLocationSource.Learned });
                learned.Add(candidate);
                logger.Info($"Learned save location of {game.Name}: {candidate}");
            }

            if (learned.Count > 0)
            {
                gameroomDb.SaveGameData(data);
            }

            return learned;
        }

        public void SetLocations(Game game, List<SaveLocation> locations)
        {
            var data = gameroomDb.GetOrCreateGameData(game.Id);
            data.SaveLocations = locations;
            gameroomDb.SaveGameData(data);
        }

        #endregion Locations

        #region Backup

        /// <summary>
        /// Backups of a game, including backups of an earlier library entry of the same game
        /// (game removed and added again, library recreated after reinstalling Windows...).
        /// </summary>
        public List<SaveBackup> GetBackups(Game game)
        {
            var name = SavePaths.NormalizeName(game.Name);
            var steamAppId = gameroomDb.GetGameData(game.Id)?.SteamAppId;
            return gameroomDb.GetAllBackups()
                .Where(a => a.GameId == game.Id ||
                    (database.Games.Get(a.GameId) == null &&
                     ((name.Length > 0 && SavePaths.NormalizeName(a.GameName) == name) ||
                      (!steamAppId.IsNullOrWhiteSpace() && a.SteamAppId == steamAppId))))
                .OrderByDescending(a => a.Created)
                .ToList();
        }

        /// <summary>
        /// Number of save files currently present in the game's save locations.
        /// </summary>
        public int GetCurrentSaveFileCount(Game game)
        {
            return CollectFiles(Resolver.Resolve(game)).Count;
        }

        /// <summary>
        /// Newest backup worth offering for restore: the game has backups but no saves on this PC,
        /// e.g. after reinstalling the game or Windows. Right after a reinstall the game may already
        /// have created new files, then the backup is offered if it has saves that are missing now.
        /// </summary>
        public SaveBackup GetRestoreCandidate(Game game, bool reinstalled = false)
        {
            if (!settings.SaveBackupsEnabled || gameroomDb.GetGameData(game.Id)?.RestoreOfferDeclined == true)
            {
                return null;
            }

            var latest = GetBackups(game).FirstOrDefault(a => File.Exists(GetArchivePath(a)));
            if (latest == null)
            {
                return null;
            }

            if (GetCurrentSaveFileCount(game) == 0)
            {
                return latest;
            }

            return reinstalled && GetMissingFileCount(game, latest) > 0 ? latest : null;
        }

        /// <summary>
        /// Number of files in a backup that don't exist in their save location now.
        /// </summary>
        public int GetMissingFileCount(Game game, SaveBackup backup)
        {
            var data = gameroomDb.GetGameData(game.Id);
            var installDir = SaveLocationResolver.GetInstallDir(game);
            var steamAppId = SaveLocationResolver.GetSteamAppId(game, data);
            using (var stream = File.OpenRead(GetArchivePath(backup)))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                SaveBackupManifest manifest;
                using (var reader = new StreamReader(archive.GetEntry(ManifestFileName).Open(), Encoding.UTF8))
                {
                    manifest = Serialization.FromJson<SaveBackupManifest>(reader.ReadToEnd());
                }

                var targets = manifest.Locations.ToDictionary(a => a.Index, a => SavePaths.Expand(a.Template, installDir, steamAppId) ?? a.Path);
                var missing = 0;
                foreach (var entry in archive.Entries)
                {
                    var separator = entry.FullName.IndexOf('/');
                    if (entry.FullName == ManifestFileName || entry.FullName.EndsWith("/") || separator <= 0 ||
                        !int.TryParse(entry.FullName.Substring(0, separator), out var index) ||
                        !targets.TryGetValue(index, out var root) || root == null)
                    {
                        continue;
                    }

                    if (!File.Exists(Path.Combine(root, entry.FullName.Substring(separator + 1).Replace('/', '\\'))))
                    {
                        missing++;
                    }
                }

                return missing;
            }
        }

        /// <summary>
        /// Registers backup archives found in the backup folder that aren't in the database,
        /// for example when the backup folder is synced from another PC or Gameroom was reinstalled.
        /// </summary>
        public int ImportExistingBackups()
        {
            var root = BackupDirectory;
            if (!Directory.Exists(root))
            {
                return 0;
            }

            var known = new HashSet<string>(gameroomDb.GetAllBackups().Select(a => a.ArchivePath), StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
            {
                var relative = file.Substring(root.TrimEnd('\\').Length + 1);
                if (known.Contains(relative))
                {
                    continue;
                }

                try
                {
                    if (RegisterArchive(file) != null)
                    {
                        imported++;
                    }
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Failed to import save backup {file}");
                }
            }

            if (imported > 0)
            {
                logger.Info($"Imported {imported} save backups found in {root}");
            }

            return imported;
        }

        /// <summary>
        /// Adds a backup archive inside the backup folder to the database, using its manifest.
        /// </summary>
        public SaveBackup RegisterArchive(string file, string cloudObjectPath = null)
        {
            using (var stream = File.OpenRead(file))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var manifestEntry = archive.GetEntry(ManifestFileName);
                if (manifestEntry == null)
                {
                    return null;
                }

                SaveBackupManifest manifest;
                using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                {
                    manifest = Serialization.FromJson<SaveBackupManifest>(reader.ReadToEnd());
                }

                var name = Path.GetFileNameWithoutExtension(file);
                var backup = new SaveBackup
                {
                    GameId = manifest.GameId,
                    GameName = manifest.GameName,
                    Created = manifest.Created,
                    ArchivePath = file.Substring(BackupDirectory.TrimEnd('\\').Length + 1),
                    Size = new FileInfo(file).Length,
                    FileCount = archive.Entries.Count(a => a.FullName != ManifestFileName && !a.FullName.EndsWith("/")),
                    SteamAppId = manifest.SteamAppId,
                    ContentHash = manifest.ContentHash,
                    CloudObjectPath = cloudObjectPath,
                    Trigger = name.Contains("_manual") ? SaveBackupTrigger.Manual :
                        name.Contains("_before-restore") ? SaveBackupTrigger.BeforeRestore :
                        name.Contains("_playing") ? SaveBackupTrigger.WhilePlaying :
                        SaveBackupTrigger.SessionEnded
                };

                gameroomDb.AddBackup(backup);
                return backup;
            }
        }

        public string GetArchivePath(SaveBackup backup)
        {
            return Path.Combine(BackupDirectory, backup.ArchivePath);
        }

        public string GetGameBackupDirectory(Game game)
        {
            return Path.Combine(BackupDirectory, $"{SavePaths.ToFolderName(game.Name)} [{game.Id.ToString("N").Substring(0, 8)}]");
        }

        private class BackupFile
        {
            public int LocationIndex;
            public string FullPath;
            public string RelativePath;
            public long Size;
            public DateTime Modified;
        }

        private List<BackupFile> CollectFiles(List<ResolvedSaveLocation> locations)
        {
            var files = new List<BackupFile>();
            for (int i = 0; i < locations.Count; i++)
            {
                var root = locations[i].Path;
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0 && files.Count < maxBackupFiles)
                {
                    var dir = pending.Pop();
                    try
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(dir))
                        {
                            if (!excludedDirectory.IsMatch(Path.GetFileName(subDir)) &&
                                !new DirectoryInfo(subDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                pending.Push(subDir);
                            }
                        }

                        foreach (var file in Directory.EnumerateFiles(dir))
                        {
                            if (excludedFile.IsMatch(file))
                            {
                                continue;
                            }

                            var info = new FileInfo(file);
                            files.Add(new BackupFile
                            {
                                LocationIndex = i,
                                FullPath = file,
                                RelativePath = file.Substring(root.TrimEnd('\\').Length + 1),
                                Size = info.Length,
                                Modified = info.LastWriteTimeUtc
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, $"Failed to read save folder {dir}");
                    }
                }
            }

            return files;
        }

        private static string GetContentHash(List<BackupFile> files)
        {
            var builder = new StringBuilder();
            foreach (var file in files.OrderBy(a => a.LocationIndex).ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(file.LocationIndex).Append('|').Append(file.RelativePath.ToLowerInvariant()).Append('|')
                    .Append(file.Size).Append('|').Append(file.Modified.Ticks).Append('\n');
            }

            using (var sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", string.Empty);
            }
        }

        /// <summary>
        /// Backs up all save locations of a game. Automatic backups are skipped when nothing changed since the last one.
        /// </summary>
        public SaveBackupResult BackupGame(Game game, SaveBackupTrigger trigger, string note = null)
        {
            var result = new SaveBackupResult { Game = game, Trigger = trigger };
            lock (gameLocks.GetOrAdd(game.Id, _ => new object()))
            {
                try
                {
                    var locations = Resolver.Resolve(game);
                    if (locations.Count == 0)
                    {
                        result.Skipped = true;
                        result.Message = ResourceProvider.GetString("LOCGameroomSavesNoLocations");
                        return result;
                    }

                    var files = CollectFiles(locations);
                    if (files.Count == 0)
                    {
                        result.Skipped = true;
                        result.Message = ResourceProvider.GetString("LOCGameroomSavesNoFiles");
                        return result;
                    }

                    var totalSize = files.Sum(a => a.Size);
                    if (totalSize > maxBackupSize)
                    {
                        result.Message = string.Format(ResourceProvider.GetString("LOCGameroomSavesTooLarge"), Units.BytesToMegaBytes(totalSize));
                        logger.Warn($"Save backup of {game.Name} skipped, {totalSize} bytes is too large.");
                        return result;
                    }

                    var hash = GetContentHash(files);
                    var latest = gameroomDb.GetBackups(game.Id).FirstOrDefault();
                    if (trigger != SaveBackupTrigger.Manual && latest != null && latest.ContentHash == hash && File.Exists(GetArchivePath(latest)))
                    {
                        if (trigger == SaveBackupTrigger.SessionEnded && latest.Trigger == SaveBackupTrigger.WhilePlaying)
                        {
                            // Saves were already backed up while playing, keep that backup as the one of this session.
                            latest.Trigger = SaveBackupTrigger.SessionEnded;
                            gameroomDb.UpdateBackup(latest);
                            ApplyRetention(game);
                            result.Success = true;
                            result.Backup = latest;
                            result.Message = string.Format(ResourceProvider.GetString("LOCGameroomSavesBackedUp"), latest.FileCount, game.Name);
                            return result;
                        }

                        result.Skipped = true;
                        result.Message = ResourceProvider.GetString("LOCGameroomSavesUnchanged");
                        return result;
                    }

                    var gameDir = GetGameBackupDirectory(game);
                    Directory.CreateDirectory(gameDir);
                    var suffix = trigger == SaveBackupTrigger.Manual ? "_manual" :
                        trigger == SaveBackupTrigger.BeforeRestore ? "_before-restore" :
                        trigger == SaveBackupTrigger.WhilePlaying ? "_playing" :
                        string.Empty;
                    var steamAppId = SaveLocationResolver.GetSteamAppId(game, gameroomDb.GetGameData(game.Id));
                    var archiveName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{suffix}";
                    var archivePath = Path.Combine(gameDir, archiveName + ".zip");
                    for (int i = 2; File.Exists(archivePath); i++)
                    {
                        archivePath = Path.Combine(gameDir, $"{archiveName}_{i}.zip");
                    }

                    var manifest = new SaveBackupManifest
                    {
                        GameId = game.Id,
                        GameName = game.Name,
                        Created = DateTime.Now,
                        Machine = Environment.MachineName,
                        SteamAppId = steamAppId,
                        ContentHash = hash,
                        Locations = locations.Select((a, i) => new SaveBackupManifest.Location { Index = i, Template = a.Template, Path = a.Path }).ToList()
                    };

                    var tempPath = archivePath + ".tmp";
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        var manifestEntry = archive.CreateEntry(ManifestFileName);
                        using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
                        {
                            writer.Write(Serialization.ToJson(manifest, true));
                        }

                        foreach (var file in files)
                        {
                            try
                            {
                                var entry = archive.CreateEntry($"{file.LocationIndex}/{file.RelativePath.Replace('\\', '/')}", CompressionLevel.Optimal);
                                entry.LastWriteTime = file.Modified.ToLocalTime();
                                // Games can keep save files open, so share access with writers.
                                using (var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                using (var target = entry.Open())
                                {
                                    source.CopyTo(target);
                                }
                            }
                            catch (Exception e)
                            {
                                logger.Warn(e, $"Failed to back up save file {file.FullPath}");
                            }
                        }
                    }

                    File.Move(tempPath, archivePath);
                    var backup = new SaveBackup
                    {
                        GameId = game.Id,
                        GameName = game.Name,
                        ArchivePath = archivePath.Substring(BackupDirectory.TrimEnd('\\').Length + 1),
                        Size = new FileInfo(archivePath).Length,
                        FileCount = files.Count,
                        ContentHash = hash,
                        Trigger = trigger,
                        Note = note,
                        SteamAppId = steamAppId
                    };

                    gameroomDb.AddBackup(backup);
                    ApplyRetention(game);
                    logger.Info($"Backed up {files.Count} save files of {game.Name} to {archivePath}");
                    result.Success = true;
                    result.Backup = backup;
                    result.Message = string.Format(ResourceProvider.GetString("LOCGameroomSavesBackedUp"), files.Count, game.Name);
                    return result;
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to back up saves of {game.Name}");
                    result.Message = e.Message;
                    return result;
                }
                finally
                {
                    BackupCompleted?.Invoke(this, result);
                }
            }
        }

        private void ApplyRetention(Game game)
        {
            var backups = gameroomDb.GetBackups(game.Id);
            var automatic = backups.Where(a => a.Trigger == SaveBackupTrigger.SessionEnded || a.Trigger == SaveBackupTrigger.BeforeRestore);
            var whilePlaying = backups.Where(a => a.Trigger == SaveBackupTrigger.WhilePlaying);
            foreach (var old in automatic.Skip(settings.MaxBackupsPerGame).Concat(whilePlaying.Skip(maxWhilePlayingBackups)).ToList())
            {
                DeleteBackup(old);
            }
        }

        public void DeleteBackup(SaveBackup backup)
        {
            try
            {
                FileSystem.DeleteFile(GetArchivePath(backup));
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to delete save backup {backup.ArchivePath}");
            }

            gameroomDb.RemoveBackup(backup.Id);
        }

        #endregion Backup

        #region Restore

        /// <summary>
        /// Restores files from a backup to their original locations. Current saves are backed up first.
        /// Files that aren't part of the backup are left untouched.
        /// </summary>
        public SaveBackupResult RestoreBackup(Game game, SaveBackup backup)
        {
            var result = new SaveBackupResult { Game = game, Backup = backup, Trigger = backup.Trigger };
            if (game.IsRunning || game.IsLaunching)
            {
                result.Message = ResourceProvider.GetString("LOCGameroomSavesCloseGameFirst");
                return result;
            }

            var archivePath = GetArchivePath(backup);
            if (!File.Exists(archivePath))
            {
                result.Message = ResourceProvider.GetString("LOCGameroomSavesArchiveMissing");
                return result;
            }

            var safety = BackupGame(game, SaveBackupTrigger.BeforeRestore);
            if (!safety.Success && !safety.Skipped)
            {
                result.Message = safety.Message;
                return result;
            }

            lock (gameLocks.GetOrAdd(game.Id, _ => new object()))
            {
                try
                {
                    var data = gameroomDb.GetGameData(game.Id);
                    var installDir = SaveLocationResolver.GetInstallDir(game);
                    var steamAppId = SaveLocationResolver.GetSteamAppId(game, data);
                    var restored = 0;
                    using (var stream = File.OpenRead(archivePath))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        var manifestEntry = archive.GetEntry(ManifestFileName);
                        SaveBackupManifest manifest;
                        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                        {
                            manifest = Serialization.FromJson<SaveBackupManifest>(reader.ReadToEnd());
                        }

                        var targets = manifest.Locations.ToDictionary(
                            a => a.Index,
                            a => SavePaths.Expand(a.Template, installDir, steamAppId) ?? a.Path);

                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName == ManifestFileName || entry.FullName.EndsWith("/"))
                            {
                                continue;
                            }

                            var separator = entry.FullName.IndexOf('/');
                            if (separator <= 0 || !int.TryParse(entry.FullName.Substring(0, separator), out var index) ||
                                !targets.TryGetValue(index, out var targetRoot) || targetRoot == null)
                            {
                                continue;
                            }

                            var relative = entry.FullName.Substring(separator + 1).Replace('/', '\\');
                            var targetPath = Path.GetFullPath(Path.Combine(targetRoot, relative));
                            // Never write outside of the save folder, even with a tampered archive.
                            if (!SavePaths.IsSubPathOrEqual(targetPath, targetRoot))
                            {
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                            using (var source = entry.Open())
                            using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                source.CopyTo(target);
                            }

                            File.SetLastWriteTime(targetPath, entry.LastWriteTime.LocalDateTime);
                            restored++;
                        }
                    }

                    logger.Info($"Restored {restored} save files of {game.Name} from {archivePath}");
                    result.Success = true;
                    result.Message = string.Format(ResourceProvider.GetString("LOCGameroomSavesRestored"), restored, game.Name);
                    return result;
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to restore saves of {game.Name} from {archivePath}");
                    result.Message = e.Message;
                    return result;
                }
            }
        }

        #endregion Restore
    }
}
