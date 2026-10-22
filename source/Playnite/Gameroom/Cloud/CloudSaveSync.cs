using Playnite.Database;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Gameroom.Cloud
{
    /// <summary>
    /// Keeps save backups in the user's Gameroom account: new backups are uploaded and backups made on other PCs
    /// are downloaded, so the regular restore offers work everywhere.
    /// </summary>
    public class CloudSaveSync : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        // Cloud storage of the free plan is shared by all users, only recent backups are kept.
        private const int maxCloudSavesPerGame = 5;
        private const int downloadedSavesPerGame = 3;
        private const long maxUploadSize = 50L * 1024 * 1024;
        // Backups made while playing happen often, big ones wait for the end of the session.
        private const long maxWhilePlayingUploadSize = 10L * 1024 * 1024;

        private readonly GameroomCloud cloud;
        private readonly SaveBackupService saves;
        private readonly GameroomDatabase gameroomDb;
        private readonly IGameDatabaseMain database;
        private readonly Func<GameroomSettings> getSettings;
        private readonly SemaphoreSlim syncLock = new SemaphoreSlim(1, 1);

        public DateTime? LastSync { get; private set; }
        public string LastError { get; private set; }
        public bool IsSyncing { get; private set; }

        /// <summary>Raised after a sync, with the number of downloaded backups.</summary>
        public event EventHandler<int> SyncCompleted;
        public event EventHandler StatusChanged;

        private GameroomSettings settings => getSettings();

        public CloudSaveSync(GameroomCloud cloud, SaveBackupService saves, GameroomDatabase gameroomDb, IGameDatabaseMain database, Func<GameroomSettings> getSettings)
        {
            this.cloud = cloud;
            this.saves = saves;
            this.gameroomDb = gameroomDb;
            this.database = database;
            this.getSettings = getSettings;
            saves.BackupCompleted += Saves_BackupCompleted;
            cloud.SessionChanged += Cloud_SessionChanged;
        }

        public void Dispose()
        {
            saves.BackupCompleted -= Saves_BackupCompleted;
            cloud.SessionChanged -= Cloud_SessionChanged;
        }

        private void Cloud_SessionChanged(object sender, EventArgs e)
        {
            if (cloud.IsSignedIn)
            {
                Task.Run(SyncAsync);
            }
            else
            {
                LastSync = null;
                LastError = null;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Saves_BackupCompleted(object sender, SaveBackupResult result)
        {
            if (!result.Success || result.Backup == null || !cloud.IsSignedIn || !settings.CloudSavesEnabled)
            {
                return;
            }

            if (result.Trigger == SaveBackupTrigger.WhilePlaying && result.Backup.Size > maxWhilePlayingUploadSize)
            {
                return;
            }

            var backup = result.Backup;
            Task.Run(async () =>
            {
                await syncLock.WaitAsync();
                try
                {
                    await UploadAsync(backup);
                    await ApplyCloudRetentionAsync(GetGameKey(backup));
                    LastError = null;
                }
                catch (Exception e)
                {
                    LastError = GameroomCloud.DescribeError(e);
                    logger.Error(e, $"Failed to upload save backup {backup.ArchivePath}");
                }
                finally
                {
                    syncLock.Release();
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// Identifies a game across PCs, by Steam app id when known.
        /// </summary>
        public static string GetGameKey(string steamAppId, string gameName)
        {
            return steamAppId.IsNullOrWhiteSpace() ? "name-" + SavePaths.NormalizeName(gameName) : "steam-" + steamAppId.Trim();
        }

        private static string GetGameKey(SaveBackup backup)
        {
            return GetGameKey(backup.SteamAppId, backup.GameName);
        }

        private async Task UploadAsync(SaveBackup backup)
        {
            // Another thread may have uploaded it meanwhile.
            backup = gameroomDb.GetAllBackups().FirstOrDefault(a => a.Id == backup.Id) ?? backup;
            if (!backup.CloudObjectPath.IsNullOrEmpty())
            {
                return;
            }

            var archivePath = saves.GetArchivePath(backup);
            if (!File.Exists(archivePath))
            {
                return;
            }

            if (new FileInfo(archivePath).Length > maxUploadSize)
            {
                logger.Warn($"Save backup {backup.ArchivePath} is too large to upload.");
                return;
            }

            var gameKey = GetGameKey(backup);
            var uploaded = await cloud.UploadSaveAsync(archivePath, new CloudSave
            {
                GameKey = gameKey,
                GameName = backup.GameName,
                SteamAppId = backup.SteamAppId,
                ObjectPath = $"{gameKey}/{backup.Id:N}.zip",
                Size = backup.Size,
                FileCount = backup.FileCount,
                ContentHash = backup.ContentHash,
                Trigger = backup.Trigger.ToString(),
                Machine = Environment.MachineName,
                CreatedAt = backup.Created.ToUniversalTime()
            });

            backup.CloudObjectPath = uploaded.ObjectPath;
            gameroomDb.UpdateBackup(backup);
            logger.Info($"Uploaded save backup of {backup.GameName} to {uploaded.ObjectPath}");
        }

        private async Task ApplyCloudRetentionAsync(string gameKey)
        {
            var cloudSaves = await cloud.GetSavesAsync();
            foreach (var old in cloudSaves.Where(a => a.GameKey == gameKey).OrderByDescending(a => a.CreatedAt).Skip(maxCloudSavesPerGame).ToList())
            {
                await cloud.DeleteSaveAsync(old);
                foreach (var local in gameroomDb.GetAllBackups().Where(a => a.CloudObjectPath == old.ObjectPath))
                {
                    local.CloudObjectPath = null;
                    gameroomDb.UpdateBackup(local);
                }
            }
        }

        /// <summary>
        /// Uploads the newest backup of every game that isn't in the cloud yet and downloads
        /// recent backups made on other PCs. Returns number of downloaded backups.
        /// </summary>
        public async Task<int> SyncAsync()
        {
            if (!cloud.IsSignedIn)
            {
                return 0;
            }

            await syncLock.WaitAsync();
            var downloaded = 0;
            IsSyncing = true;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                if (settings.CloudSavesEnabled)
                {
                    var cloudSaves = await cloud.GetSavesAsync();
                    var local = gameroomDb.GetAllBackups();

                    foreach (var group in local.GroupBy(GetGameKey))
                    {
                        var newest = group.OrderByDescending(a => a.Created).First();
                        var inCloud = !newest.CloudObjectPath.IsNullOrEmpty() ||
                            cloudSaves.Any(a => a.GameKey == group.Key && !newest.ContentHash.IsNullOrEmpty() && a.ContentHash == newest.ContentHash);
                        if (!inCloud)
                        {
                            await UploadAsync(newest);
                            await ApplyCloudRetentionAsync(group.Key);
                        }
                    }

                    local = gameroomDb.GetAllBackups();
                    foreach (var group in cloudSaves.GroupBy(a => a.GameKey))
                    {
                        foreach (var save in group.OrderByDescending(a => a.CreatedAt).Take(downloadedSavesPerGame))
                        {
                            var present = local.Any(a => a.CloudObjectPath == save.ObjectPath ||
                                (!save.ContentHash.IsNullOrEmpty() && a.ContentHash == save.ContentHash && GetGameKey(a) == save.GameKey));
                            if (present)
                            {
                                continue;
                            }

                            var target = Path.Combine(saves.BackupDirectory, $"{SavePaths.ToFolderName(save.GameName)} [cloud]", Path.GetFileName(save.ObjectPath));
                            await cloud.DownloadSaveAsync(save, target);
                            if (saves.RegisterArchive(target, save.ObjectPath) != null)
                            {
                                downloaded++;
                            }
                        }
                    }
                }

                await cloud.UpdateProfileStatsAsync(
                    database.Games.Sum(a => (long)a.Playtime),
                    database.Games.Count(a => a.Playtime > 0));

                LastSync = DateTime.Now;
                LastError = null;
                if (downloaded > 0)
                {
                    logger.Info($"Downloaded {downloaded} save backups from the cloud.");
                }
            }
            catch (Exception e)
            {
                LastError = GameroomCloud.DescribeError(e);
                logger.Error(e, "Cloud save sync failed.");
            }
            finally
            {
                IsSyncing = false;
                syncLock.Release();
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            SyncCompleted?.Invoke(this, downloaded);
            return downloaded;
        }

        /// <summary>
        /// Cloud backups grouped by game, for the account page.
        /// </summary>
        public async Task<List<CloudSave>> GetCloudSavesAsync()
        {
            return cloud.IsSignedIn ? await cloud.GetSavesAsync() : new List<CloudSave>();
        }
    }
}
