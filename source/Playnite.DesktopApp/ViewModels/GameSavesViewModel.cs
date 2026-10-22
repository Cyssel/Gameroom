using Playnite.Common;
using Playnite.Gameroom;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Windows;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace Playnite.DesktopApp.ViewModels
{
    public class SaveLocationItem : ObservableObject
    {
        private readonly Action<SaveLocationItem> enabledChanged;

        public string Template { get; set; }
        public string Path { get; set; }
        public SaveLocationSource Source { get; set; }
        public bool IsStored { get; set; }
        public bool Exists => !Path.IsNullOrWhiteSpace() && Directory.Exists(Path);
        public string SourceText => ResourceProvider.GetString("LOCGameroomSavesSource" + Source);

        private bool enabled;
        public bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled == value)
                {
                    return;
                }

                enabled = value;
                OnPropertyChanged();
                enabledChanged?.Invoke(this);
            }
        }

        public SaveLocationItem(bool enabled, Action<SaveLocationItem> enabledChanged)
        {
            this.enabled = enabled;
            this.enabledChanged = enabledChanged;
        }
    }

    public class SaveBackupItem
    {
        public SaveBackup Backup { get; set; }
        public string DateText => Backup.Created.ToString("g");
        public string TypeText => ResourceProvider.GetString("LOCGameroomSavesTrigger" + Backup.Trigger);
        public string SizeText => Backup.Size >= 1024 * 1024 ? $"{Backup.Size / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, Backup.Size / 1024)} KB";
        public int FileCount => Backup.FileCount;
        public bool InCloud => !Backup.CloudObjectPath.IsNullOrEmpty();
    }

    public class GameSavesViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IWindowFactory window;
        private readonly IDialogsFactory dialogs;
        private readonly GameroomService service;
        private readonly Game game;

        public string Title => $"{ResourceProvider.GetString("LOCGameroomSavesWindowTitle")} - {game.Name}";
        public ObservableCollection<SaveLocationItem> Locations { get; } = new ObservableCollection<SaveLocationItem>();
        public ObservableCollection<SaveBackupItem> Backups { get; } = new ObservableCollection<SaveBackupItem>();
        public bool HasNoLocations => !Locations.Any(a => a.Enabled && a.Exists);

        private SaveLocationItem selectedLocation;
        public SaveLocationItem SelectedLocation
        {
            get => selectedLocation;
            set
            {
                selectedLocation = value;
                OnPropertyChanged();
            }
        }

        private SaveBackupItem selectedBackup;
        public SaveBackupItem SelectedBackup
        {
            get => selectedBackup;
            set
            {
                selectedBackup = value;
                OnPropertyChanged();
            }
        }

        public string SteamAppId
        {
            get => service.Database.GetGameData(game.Id)?.SteamAppId ?? SaveLocationResolver.GetSteamAppId(game, null);
            set
            {
                var data = service.Database.GetOrCreateGameData(game.Id);
                data.SteamAppId = value?.Trim();
                service.Database.SaveGameData(data);
                OnPropertyChanged();
                LoadLocations();
            }
        }

        public bool AutoBackupDisabled
        {
            get => service.Database.GetGameData(game.Id)?.SaveBackupsDisabled == true;
            set
            {
                var data = service.Database.GetOrCreateGameData(game.Id);
                data.SaveBackupsDisabled = value;
                service.Database.SaveGameData(data);
                OnPropertyChanged();
            }
        }

        public RelayCommand AddLocationCommand => new RelayCommand(AddLocation);
        public RelayCommand<SaveLocationItem> RemoveLocationCommand => new RelayCommand<SaveLocationItem>(RemoveLocation, a => a != null);
        public RelayCommand<SaveLocationItem> OpenLocationCommand => new RelayCommand<SaveLocationItem>(a => Explorer.OpenDirectory(a.Path), a => a?.Exists == true);
        public RelayCommand BackupNowCommand => new RelayCommand(BackupNow);
        public RelayCommand<SaveBackupItem> RestoreCommand => new RelayCommand<SaveBackupItem>(Restore, a => a != null);
        public RelayCommand<SaveBackupItem> DeleteCommand => new RelayCommand<SaveBackupItem>(Delete, a => a != null);
        public RelayCommand<SaveBackupItem> ShowInFolderCommand => new RelayCommand<SaveBackupItem>(
            a => Explorer.NavigateToFileSystemEntry(service.Saves.GetArchivePath(a.Backup)),
            a => a != null);
        public RelayCommand CloseCommand => new RelayCommand(() => window.Close());

        public GameSavesViewModel(IWindowFactory window, IDialogsFactory dialogs, GameroomService service, Game game)
        {
            this.window = window;
            this.dialogs = dialogs;
            this.service = service;
            this.game = game;
            LoadLocations();
            LoadBackups();
        }

        public void OpenView()
        {
            window.CreateAndOpenDialog(this);
        }

        private void LoadLocations()
        {
            Locations.Clear();
            var data = service.Database.GetOrCreateGameData(game.Id);
            var installDir = SaveLocationResolver.GetInstallDir(game);
            var steamAppId = SaveLocationResolver.GetSteamAppId(game, data);
            foreach (var stored in data.SaveLocations)
            {
                Locations.Add(new SaveLocationItem(stored.Enabled, LocationEnabledChanged)
                {
                    Template = stored.Path,
                    Path = SavePaths.Expand(stored.Path, installDir, steamAppId),
                    Source = stored.Source,
                    IsStored = true
                });
            }

            foreach (var resolved in service.Saves.Resolver.Resolve(game))
            {
                if (Locations.Any(a => string.Equals(a.Path, resolved.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Locations.Add(new SaveLocationItem(true, LocationEnabledChanged)
                {
                    Template = resolved.Template,
                    Path = resolved.Path,
                    Source = resolved.Source
                });
            }

            OnPropertyChanged(nameof(HasNoLocations));
        }

        private void LoadBackups()
        {
            Backups.Clear();
            foreach (var backup in service.Saves.GetBackups(game))
            {
                Backups.Add(new SaveBackupItem { Backup = backup });
            }
        }

        private void UpdateStoredLocations(Action<List<SaveLocation>> update)
        {
            var data = service.Database.GetOrCreateGameData(game.Id);
            update(data.SaveLocations);
            service.Database.SaveGameData(data);
            LoadLocations();
        }

        private void LocationEnabledChanged(SaveLocationItem item)
        {
            UpdateStoredLocations(locations =>
            {
                var stored = locations.FirstOrDefault(a => string.Equals(a.Path, item.Template, StringComparison.OrdinalIgnoreCase));
                if (stored != null)
                {
                    stored.Enabled = item.Enabled;
                }
                else
                {
                    // Known locations are only stored when the user disables them.
                    locations.Add(new SaveLocation { Path = item.Template, Source = item.Source, Enabled = item.Enabled });
                }
            });
        }

        private void AddLocation()
        {
            var path = dialogs.SelectFolder();
            if (path.IsNullOrWhiteSpace())
            {
                return;
            }

            var template = SavePaths.Contract(path, SaveLocationResolver.GetInstallDir(game));
            UpdateStoredLocations(locations =>
            {
                locations.RemoveAll(a => string.Equals(a.Path, template, StringComparison.OrdinalIgnoreCase));
                locations.Add(new SaveLocation { Path = template, Source = SaveLocationSource.User });
            });
        }

        private void RemoveLocation(SaveLocationItem item)
        {
            UpdateStoredLocations(locations =>
            {
                locations.RemoveAll(a => string.Equals(a.Path, item.Template, StringComparison.OrdinalIgnoreCase));
                // Learned and known locations would come back, so they are kept as disabled.
                if (item.Source != SaveLocationSource.User)
                {
                    locations.Add(new SaveLocation { Path = item.Template, Source = item.Source, Enabled = false });
                }
            });
        }

        private void BackupNow()
        {
            SaveBackupResult result = null;
            dialogs.ActivateGlobalProgress(_ =>
            {
                result = service.Saves.BackupGame(game, SaveBackupTrigger.Manual);
            }, new GlobalProgressOptions("LOCGameroomSavesInProgress"));

            ShowResult(result);
            LoadBackups();
        }

        private void Restore(SaveBackupItem item)
        {
            if (dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCGameroomSavesRestoreConfirm"), game.Name, item.DateText),
                ResourceProvider.GetString("LOCGameroomSavesRestore"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            SaveBackupResult result = null;
            dialogs.ActivateGlobalProgress(_ =>
            {
                result = service.Saves.RestoreBackup(game, item.Backup);
            }, new GlobalProgressOptions("LOCGameroomSavesRestoreInProgress"));

            ShowResult(result);
            LoadBackups();
        }

        private void Delete(SaveBackupItem item)
        {
            if (dialogs.ShowMessage(
                ResourceProvider.GetString("LOCGameroomSavesDeleteConfirm"),
                ResourceProvider.GetString("LOCGameroomSavesDelete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            service.Saves.DeleteBackup(item.Backup);
            LoadBackups();
        }

        private void ShowResult(SaveBackupResult result)
        {
            if (result == null)
            {
                return;
            }

            if (result.Success || result.Skipped)
            {
                dialogs.ShowMessage(result.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                dialogs.ShowErrorMessage(result.Message, Title);
            }
        }
    }
}
