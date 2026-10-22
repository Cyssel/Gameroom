using System;
using System.Collections.Generic;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Settings for features specific to Gameroom: live game detection and save backups.
    /// Stored as part of the main application config.
    /// </summary>
    public class GameroomSettings : ObservableObject
    {
        private bool liveDetectionEnabled = true;
        /// <summary>
        /// Detects library games started outside of Gameroom and records their playtime.
        /// </summary>
        public bool LiveDetectionEnabled
        {
            get => liveDetectionEnabled;
            set
            {
                liveDetectionEnabled = value;
                OnPropertyChanged();
            }
        }

        private int detectionInterval = 5;
        /// <summary>
        /// How often running processes are scanned, in seconds.
        /// </summary>
        public int DetectionInterval
        {
            get => detectionInterval;
            set
            {
                detectionInterval = Math.Max(2, value);
                OnPropertyChanged();
            }
        }

        private int minimumSessionLength = 60;
        /// <summary>
        /// Detected sessions shorter than this (in seconds) are not recorded.
        /// </summary>
        public int MinimumSessionLength
        {
            get => minimumSessionLength;
            set
            {
                minimumSessionLength = Math.Max(0, value);
                OnPropertyChanged();
            }
        }

        private bool detectUnknownGames = true;
        /// <summary>
        /// Recognizes running games that are not in the library yet and offers to add them.
        /// </summary>
        public bool DetectUnknownGames
        {
            get => detectUnknownGames;
            set
            {
                detectUnknownGames = value;
                OnPropertyChanged();
            }
        }

        private bool autoAddUnknownGames = false;
        /// <summary>
        /// Adds recognized unknown games to the library without asking.
        /// </summary>
        public bool AutoAddUnknownGames
        {
            get => autoAddUnknownGames;
            set
            {
                autoAddUnknownGames = value;
                OnPropertyChanged();
            }
        }

        private bool notifyDetectedSessions = true;
        /// <summary>
        /// Shows a notification when a game launched outside of Gameroom is being tracked.
        /// </summary>
        public bool NotifyDetectedSessions
        {
            get => notifyDetectedSessions;
            set
            {
                notifyDetectedSessions = value;
                OnPropertyChanged();
            }
        }

        private bool saveBackupsEnabled = true;
        /// <summary>
        /// Backs up game saves after each play session.
        /// </summary>
        public bool SaveBackupsEnabled
        {
            get => saveBackupsEnabled;
            set
            {
                saveBackupsEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool learnSaveLocations = true;
        /// <summary>
        /// Watches which files a game writes while it runs to discover save folders automatically.
        /// </summary>
        public bool LearnSaveLocations
        {
            get => learnSaveLocations;
            set
            {
                learnSaveLocations = value;
                OnPropertyChanged();
            }
        }

        private string saveBackupDirectory;
        /// <summary>
        /// Where save backups are stored. Empty means the default folder in Gameroom's data directory.
        /// Pointing this to a cloud synced folder (OneDrive, Google Drive...) keeps saves in the cloud.
        /// </summary>
        public string SaveBackupDirectory
        {
            get => saveBackupDirectory;
            set
            {
                saveBackupDirectory = value;
                OnPropertyChanged();
            }
        }

        private int maxBackupsPerGame = 15;
        /// <summary>
        /// Oldest automatic backups are removed above this count. Manual backups are never removed.
        /// </summary>
        public int MaxBackupsPerGame
        {
            get => maxBackupsPerGame;
            set
            {
                maxBackupsPerGame = Math.Max(1, value);
                OnPropertyChanged();
            }
        }

        private bool cloudSavesEnabled = true;
        /// <summary>
        /// Uploads save backups to the user's Gameroom account and downloads them on other PCs.
        /// </summary>
        public bool CloudSavesEnabled
        {
            get => cloudSavesEnabled;
            set
            {
                cloudSavesEnabled = value;
                OnPropertyChanged();
            }
        }

        private string cloudServerUrl;
        /// <summary>
        /// Address of the user's own Supabase project holding accounts and cloud saves, e.g. https://abcd.supabase.co.
        /// </summary>
        public string CloudServerUrl
        {
            get => cloudServerUrl;
            set
            {
                cloudServerUrl = value;
                OnPropertyChanged();
            }
        }

        private string cloudServerKey;
        /// <summary>
        /// Publishable (anon) key of that project. Public by design: row level security protects the data.
        /// </summary>
        public string CloudServerKey
        {
            get => cloudServerKey;
            set
            {
                cloudServerKey = value;
                OnPropertyChanged();
            }
        }

        private bool detectInstalledGames = true;
        /// <summary>
        /// Adds games installed on this PC (Windows applications, Steam, Riot, Battle.net, EA, HoYoPlay and Epic) to the library.
        /// </summary>
        public bool DetectInstalledGames
        {
            get => detectInstalledGames;
            set
            {
                detectInstalledGames = value;
                OnPropertyChanged();
            }
        }

        private bool achievementOverlayEnabled = true;
        /// <summary>
        /// Shows a popup over the game when an achievement is unlocked.
        /// </summary>
        public bool AchievementOverlayEnabled
        {
            get => achievementOverlayEnabled;
            set
            {
                achievementOverlayEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool achievementOverlayForSteam = false;
        /// <summary>
        /// Also shows the popup for games running through the Steam client, which has its own overlay.
        /// </summary>
        public bool AchievementOverlayForSteam
        {
            get => achievementOverlayForSteam;
            set
            {
                achievementOverlayForSteam = value;
                OnPropertyChanged();
            }
        }

        private bool achievementSoundEnabled = true;
        public bool AchievementSoundEnabled
        {
            get => achievementSoundEnabled;
            set
            {
                achievementSoundEnabled = value;
                OnPropertyChanged();
            }
        }

        private int visualDefaultsVersion = 0;
        /// <summary>
        /// Interface defaults already applied to settings carried over from older versions or from Playnite.
        /// </summary>
        public int VisualDefaultsVersion
        {
            get => visualDefaultsVersion;
            set
            {
                visualDefaultsVersion = value;
                OnPropertyChanged();
            }
        }

        private List<string> ignoredExecutables = new List<string>();
        /// <summary>
        /// Executables that are never treated as games by the detector.
        /// </summary>
        public List<string> IgnoredExecutables
        {
            get => ignoredExecutables;
            set
            {
                ignoredExecutables = value ?? new List<string>();
                OnPropertyChanged();
            }
        }
    }
}
