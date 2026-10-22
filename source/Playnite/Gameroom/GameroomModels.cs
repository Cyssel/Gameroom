using System;
using System.Collections.Generic;

namespace Playnite.Gameroom
{
    public enum SessionSource
    {
        /// <summary>Game was started from Gameroom.</summary>
        Launched,
        /// <summary>Game was started outside of Gameroom and recognized by the live detector.</summary>
        Detected
    }

    /// <summary>
    /// One finished play session.
    /// </summary>
    public class GameSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        /// <summary>Length in seconds.</summary>
        public ulong Duration { get; set; }
        public SessionSource Source { get; set; }
        public string Executable { get; set; }
    }

    /// <summary>
    /// Detected session that is still running. Persisted periodically so playtime survives
    /// a crash, a forced shutdown or Gameroom being closed while a game runs.
    /// </summary>
    public class ActiveSession
    {
        public Guid Id { get; set; }
        public Guid GameId { get; set; }
        public DateTime Start { get; set; }
        public DateTime LastSeen { get; set; }
        public string Executable { get; set; }
        public int ProcessId { get; set; }
        public DateTime ProcessStart { get; set; }
        /// <summary>Seconds of the session already added to the game's playtime.</summary>
        public ulong CreditedSeconds { get; set; }
    }

    public enum SaveLocationSource
    {
        /// <summary>Added by the user.</summary>
        User,
        /// <summary>Known location from the built-in database (store, emulator or crack specific paths).</summary>
        Known,
        /// <summary>Discovered by watching files the game wrote while running.</summary>
        Learned
    }

    /// <summary>
    /// Folder containing save files of a game.
    /// </summary>
    public class SaveLocation
    {
        /// <summary>Folder path. Can contain environment variables like %APPDATA%.</summary>
        public string Path { get; set; }
        public SaveLocationSource Source { get; set; }
        /// <summary>Disabled locations are kept so they are not learned again.</summary>
        public bool Enabled { get; set; } = true;
        public DateTime Added { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return Path;
        }
    }

    /// <summary>
    /// Per game data Gameroom keeps outside of the main library database.
    /// </summary>
    public class GameroomGameData
    {
        /// <summary>Same as Game.Id.</summary>
        public Guid Id { get; set; }
        public List<SaveLocation> SaveLocations { get; set; } = new List<SaveLocation>();
        /// <summary>Steam app id, also for games not imported by the Steam library plugin.</summary>
        public string SteamAppId { get; set; }
        /// <summary>Executables the detector has seen this game running from.</summary>
        public List<string> KnownExecutables { get; set; } = new List<string>();
        public bool SaveBackupsDisabled { get; set; }
        /// <summary>User doesn't want to be offered restoring saves of this game.</summary>
        public bool RestoreOfferDeclined { get; set; }
        /// <summary>Metadata download from Steam was attempted.</summary>
        public bool SteamMetadataFetched { get; set; }
        /// <summary>When the Steam store was last searched by name for the app with this game's achievements.</summary>
        public DateTime? AchievementAppSearched { get; set; }
    }

    public enum InstalledAppDecision
    {
        /// <summary>Added to the library, or already there.</summary>
        Added,
        NotGame,
        /// <summary>The user removed the game from the library, it isn't added again.</summary>
        Removed
    }

    /// <summary>
    /// Application installed on Windows that was already checked for being a game.
    /// </summary>
    public class InstalledAppRecord
    {
        /// <summary>Registration of the application, e.g. "uninstall:" and its registry key.</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public InstalledAppDecision Decision { get; set; }
        public Guid GameId { get; set; }
        public DateTime Checked { get; set; }
    }

    public enum SaveBackupTrigger
    {
        SessionEnded,
        Manual,
        BeforeRestore,
        /// <summary>Saves changed while the game was running.</summary>
        WhilePlaying
    }

    /// <summary>
    /// One save backup archive.
    /// </summary>
    public class SaveBackup
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        /// <summary>Archive path relative to the backup directory.</summary>
        public string ArchivePath { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        /// <summary>Fingerprint of backed up files, used to skip backups when nothing changed.</summary>
        public string ContentHash { get; set; }
        public SaveBackupTrigger Trigger { get; set; }
        public string Note { get; set; }
        /// <summary>Used to find backups of a game that was removed and added to the library again.</summary>
        public string SteamAppId { get; set; }
        /// <summary>Path of the uploaded copy in the cloud, null if it's only on this PC.</summary>
        public string CloudObjectPath { get; set; }
    }
}
