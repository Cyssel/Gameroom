using LiteDB;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Storage for Gameroom specific data (sessions, save locations, backups).
    /// Kept in its own file inside the library folder, so it's included in library backups
    /// without changing the format of the main library database.
    /// </summary>
    public class GameroomDatabase : IDisposable
    {
        public const string FileName = "gameroom.db";

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly object dbLock = new object();
        private readonly LiteDatabase db;
        private readonly LiteCollection<GameSession> sessions;
        private readonly LiteCollection<ActiveSession> activeSessions;
        private readonly LiteCollection<GameroomGameData> gameData;
        private readonly LiteCollection<SaveBackup> backups;
        private readonly LiteCollection<InstalledAppRecord> installedApps;

        public string FilePath { get; }

        public GameroomDatabase(string libraryDir)
        {
            FilePath = Path.Combine(libraryDir, FileName);
            db = new LiteDatabase($"Filename={FilePath};Mode=Exclusive");
            sessions = db.GetCollection<GameSession>("sessions");
            sessions.EnsureIndex(a => a.GameId);
            sessions.EnsureIndex(a => a.Start);
            activeSessions = db.GetCollection<ActiveSession>("active_sessions");
            gameData = db.GetCollection<GameroomGameData>("game_data");
            backups = db.GetCollection<SaveBackup>("save_backups");
            backups.EnsureIndex(a => a.GameId);
            installedApps = db.GetCollection<InstalledAppRecord>("installed_apps");
            logger.Info($"Opened Gameroom database {FilePath}");
        }

        public void Dispose()
        {
            lock (dbLock)
            {
                db.Dispose();
            }
        }

        #region Sessions

        public void AddSession(GameSession session)
        {
            lock (dbLock)
            {
                sessions.Insert(session);
            }
        }

        public List<GameSession> GetSessions(Guid gameId)
        {
            lock (dbLock)
            {
                return sessions.Find(a => a.GameId == gameId).OrderByDescending(a => a.Start).ToList();
            }
        }

        public List<GameSession> GetSessions(DateTime from, DateTime to)
        {
            lock (dbLock)
            {
                return sessions.Find(a => a.Start >= from && a.Start < to).OrderBy(a => a.Start).ToList();
            }
        }

        public List<GameSession> GetAllSessions()
        {
            lock (dbLock)
            {
                return sessions.FindAll().ToList();
            }
        }

        public void RemoveSessions(Guid gameId)
        {
            lock (dbLock)
            {
                sessions.Delete(a => a.GameId == gameId);
            }
        }

        public void SaveActiveSession(ActiveSession session)
        {
            lock (dbLock)
            {
                activeSessions.Upsert(session);
            }
        }

        public void RemoveActiveSession(Guid id)
        {
            lock (dbLock)
            {
                activeSessions.Delete(id);
            }
        }

        public List<ActiveSession> GetActiveSessions()
        {
            lock (dbLock)
            {
                return activeSessions.FindAll().ToList();
            }
        }

        #endregion Sessions

        #region Game data

        public GameroomGameData GetGameData(Guid gameId)
        {
            lock (dbLock)
            {
                return gameData.FindById(gameId);
            }
        }

        public GameroomGameData GetOrCreateGameData(Guid gameId)
        {
            return GetGameData(gameId) ?? new GameroomGameData { Id = gameId };
        }

        public List<GameroomGameData> GetAllGameData()
        {
            lock (dbLock)
            {
                return gameData.FindAll().ToList();
            }
        }

        public void SaveGameData(GameroomGameData data)
        {
            lock (dbLock)
            {
                gameData.Upsert(data);
            }
        }

        public void RemoveGameData(Guid gameId)
        {
            lock (dbLock)
            {
                gameData.Delete(gameId);
            }
        }

        #endregion Game data

        #region Save backups

        public void AddBackup(SaveBackup backup)
        {
            lock (dbLock)
            {
                backups.Insert(backup);
            }
        }

        public void UpdateBackup(SaveBackup backup)
        {
            lock (dbLock)
            {
                backups.Update(backup);
            }
        }

        public void RemoveBackup(Guid id)
        {
            lock (dbLock)
            {
                backups.Delete(id);
            }
        }

        public List<SaveBackup> GetBackups(Guid gameId)
        {
            lock (dbLock)
            {
                return backups.Find(a => a.GameId == gameId).OrderByDescending(a => a.Created).ToList();
            }
        }

        public List<SaveBackup> GetAllBackups()
        {
            lock (dbLock)
            {
                return backups.FindAll().ToList();
            }
        }

        #endregion Save backups

        #region Installed applications

        public List<InstalledAppRecord> GetInstalledApps()
        {
            lock (dbLock)
            {
                return installedApps.FindAll().ToList();
            }
        }

        public void SaveInstalledApp(InstalledAppRecord record)
        {
            lock (dbLock)
            {
                installedApps.Upsert(record);
            }
        }

        #endregion Installed applications
    }
}
