using Playnite.Common;
using Playnite.DesktopApp.ViewModels;
using Playnite.DesktopApp.Windows;
using Playnite.Gameroom;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Game menu items of Gameroom features.
    /// </summary>
    internal static class GameroomMenu
    {
        public static void AddItems(ItemsControl menu, Game game, DesktopAppViewModel model)
        {
            var service = GameroomService.Instance;
            if (service == null)
            {
                return;
            }

            menu.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuGamePage"),
                Command = new RelayCommand(() => model.OpenGamePage(game))
            });

            menu.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuAchievements"),
                Command = new RelayCommand(() => new GameAchievementsViewModel(new GameAchievementsWindowFactory(), service, game).OpenView())
            });

            menu.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuSessions"),
                Command = new RelayCommand(() => new GameSessionsViewModel(new GameSessionsWindowFactory(), service, game).OpenView())
            });

            var savesItem = new MenuItem { Header = ResourceProvider.GetString("LOCGameroomMenuSaves") };
            savesItem.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuBackupNow"),
                Command = new RelayCommand(() => BackupNow(service, game, model.Dialogs))
            });
            var latestBackup = service.Saves.GetBackups(game).FirstOrDefault();
            savesItem.Items.Add(new MenuItem
            {
                Header = latestBackup == null
                    ? ResourceProvider.GetString("LOCGameroomMenuRestoreLatest")
                    : string.Format(ResourceProvider.GetString("LOCGameroomMenuRestoreLatestDate"), latestBackup.Created.ToString("g")),
                IsEnabled = latestBackup != null,
                Command = new RelayCommand(() => RestoreLatest(service, game, latestBackup, model.Dialogs))
            });
            savesItem.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuManageBackups"),
                Command = new RelayCommand(() => new GameSavesViewModel(new GameSavesWindowFactory(), model.Dialogs, service, game).OpenView())
            });
            savesItem.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuOpenBackupFolder"),
                Command = new RelayCommand(() => OpenBackupFolder(service, game))
            });

            menu.Items.Add(savesItem);

            menu.Items.Add(new MenuItem
            {
                Header = ResourceProvider.GetString("LOCGameroomMenuSteamMetadata"),
                Command = new RelayCommand(() => DownloadSteamMetadata(service, game, model.Dialogs))
            });
        }

        /// <summary>
        /// Looks for installed games right away and tells which ones were added.
        /// </summary>
        internal static void ScanInstalledGames(IDialogsFactory dialogs)
        {
            var service = GameroomService.Instance;
            if (service == null)
            {
                return;
            }

            Gameroom.InstalledGames.InstalledGamesResult result = null;
            dialogs.ActivateGlobalProgress(_ => result = service.ScanInstalledGames(true), new GlobalProgressOptions("LOCGameroomInstalledGamesScanning"));
            var added = result?.Added ?? new System.Collections.Generic.List<Game>();
            var message = added.Count == 0
                ? ResourceProvider.GetString("LOCGameroomInstalledGamesNone")
                : string.Format(ResourceProvider.GetString(added.Count == 1 ? "LOCGameroomInstalledGamesResultOne" : "LOCGameroomInstalledGamesResult"),
                    added.Count, string.Join(Environment.NewLine, added.Select(a => "• " + a.Name)));
            dialogs.ShowMessage(message, ResourceProvider.GetString("LOCGameroomMenuScanInstalled"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void DownloadSteamMetadata(GameroomService service, Game game, IDialogsFactory dialogs)
        {
            var found = false;
            dialogs.ActivateGlobalProgress(_ =>
            {
                found = service.DownloadSteamMetadata(game);
            }, new GlobalProgressOptions("LOCGameroomSteamMetadataProgress"));

            dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString(found ? "LOCGameroomSteamMetadataDone" : "LOCGameroomSteamMetadataNotFound"), game.Name),
                game.Name,
                MessageBoxButton.OK,
                found ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        internal static void BackupNow(GameroomService service, Game game, IDialogsFactory dialogs)
        {
            SaveBackupResult result = null;
            dialogs.ActivateGlobalProgress(_ =>
            {
                result = service.Saves.BackupGame(game, SaveBackupTrigger.Manual);
            }, new GlobalProgressOptions("LOCGameroomSavesInProgress"));

            if (result.Success || result.Skipped)
            {
                dialogs.ShowMessage(result.Message, game.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                dialogs.ShowErrorMessage(string.Format(ResourceProvider.GetString("LOCGameroomSavesBackupFailed"), game.Name, result.Message), game.Name);
            }
        }

        private static void RestoreLatest(GameroomService service, Game game, SaveBackup backup, IDialogsFactory dialogs)
        {
            if (dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCGameroomSavesRestoreConfirm"), game.Name, backup.Created.ToString("g")),
                ResourceProvider.GetString("LOCGameroomSavesRestore"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            SaveBackupResult result = null;
            dialogs.ActivateGlobalProgress(_ =>
            {
                result = service.Saves.RestoreBackup(game, backup);
            }, new GlobalProgressOptions("LOCGameroomSavesRestoreInProgress"));

            if (result?.Success == true)
            {
                dialogs.ShowMessage(result.Message, game.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                dialogs.ShowErrorMessage(result?.Message, game.Name);
            }
        }

        private static void OpenBackupFolder(GameroomService service, Game game)
        {
            var dir = service.Saves.GetGameBackupDirectory(game);
            if (!Directory.Exists(dir))
            {
                dir = service.Saves.BackupDirectory;
                Directory.CreateDirectory(dir);
            }

            Explorer.OpenDirectory(dir);
        }
    }
}
