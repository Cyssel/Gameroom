using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Copies the library of an existing Playnite installation into Gameroom on first start.
    /// The Playnite installation itself is never modified.
    /// </summary>
    public static class PlayniteImporter
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static string PlayniteDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");

        /// <summary>
        /// Library folder of the installed Playnite, or null if there's none.
        /// </summary>
        public static string FindPlayniteLibrary()
        {
            try
            {
                var libraryDir = Path.Combine(PlayniteDataDir, "library");
                var configPath = Path.Combine(PlayniteDataDir, PlaynitePaths.ConfigFileName);
                if (File.Exists(configPath))
                {
                    var config = Serialization.FromJsonFile<PlayniteConfigStub>(configPath);
                    if (!config?.DatabasePath.IsNullOrWhiteSpace() == true)
                    {
                        // Playnite's default installation folder is used for {PlayniteDir}.
                        var playniteProgramDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnite");
                        libraryDir = Environment.ExpandEnvironmentVariables(
                            config.DatabasePath.Replace(ExpandableVariables.PlayniteDirectory, playniteProgramDir));
                    }
                }

                return File.Exists(Path.Combine(libraryDir, "games.db")) ? libraryDir : null;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to look for Playnite library.");
                return null;
            }
        }

        private class PlayniteConfigStub
        {
            public string DatabasePath { get; set; }
        }

        private static bool IsPlayniteRunning()
        {
            return Process.GetProcessesByName("Playnite.DesktopApp").Concat(Process.GetProcessesByName("Playnite.FullscreenApp")).Any();
        }

        /// <summary>
        /// Offers to import Playnite's library to <paramref name="targetLibraryDir"/>. Returns true if it was imported.
        /// </summary>
        public static bool OfferImport(IDialogsFactory dialogs, string targetLibraryDir)
        {
            var sourceLibrary = FindPlayniteLibrary();
            if (sourceLibrary == null)
            {
                return false;
            }

            var target = GameDatabase.GetFullDbPath(targetLibraryDir);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return false;
            }

            if (dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCGameroomImportPlayniteMessage"), sourceLibrary),
                ResourceProvider.GetString("LOCGameroomImportPlayniteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return false;
            }

            // Library files are opened exclusively while Playnite runs.
            while (IsPlayniteRunning())
            {
                if (dialogs.ShowMessage(
                    ResourceProvider.GetString("LOCGameroomImportPlayniteRunning"),
                    ResourceProvider.GetString("LOCGameroomImportPlayniteTitle"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    return false;
                }
            }

            Exception error = null;
            dialogs.ActivateGlobalProgress(_ =>
            {
                try
                {
                    CopyDirectory(sourceLibrary, target);
                    CopyIfExists(Path.Combine(PlayniteDataDir, PlaynitePaths.ExtensionsDirName), PlaynitePaths.ExtensionsUserDataPath);
                    CopyIfExists(Path.Combine(PlayniteDataDir, PlaynitePaths.ExtensionsDataDirName), PlaynitePaths.ExtensionsDataPath);
                    CopyIfExists(Path.Combine(PlayniteDataDir, PlaynitePaths.ThemesDirName), PlaynitePaths.ThemesUserDataPath);
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, new GlobalProgressOptions("LOCGameroomImportPlayniteProgress"));

            if (error != null)
            {
                logger.Error(error, $"Failed to import Playnite library from {sourceLibrary}");
                FileSystem.DeleteDirectory(target, false);
                dialogs.ShowErrorMessage(string.Format(ResourceProvider.GetString("LOCGameroomImportPlayniteFailed"), error.Message), "");
                return false;
            }

            logger.Info($"Imported Playnite library from {sourceLibrary} to {target}");
            return true;
        }

        private static void CopyIfExists(string source, string target)
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, target);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }
    }
}
