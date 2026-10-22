using NUnit.Framework;
using Playnite.Database;
using Playnite.Gameroom;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gameroom.Tests
{
    [TestFixture]
    public class SavePathsTests
    {
        [Test]
        public void ContractAndExpandTest()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var template = SavePaths.Contract(Path.Combine(appData, "Studio", "Game"), null);
            Assert.AreEqual(@"%APPDATA%\Studio\Game", template);
            Assert.AreEqual(Path.Combine(appData, "Studio", "Game"), SavePaths.Expand(template, null, null));

            var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Team Cherry");
            Assert.AreEqual(@"%USERPROFILE%\AppData\LocalLow\Team Cherry", SavePaths.Contract(localLow, null));

            Assert.AreEqual(@"{InstallDir}\saves", SavePaths.Contract(@"D:\Library\Game\saves", @"D:\Library\Game"));
            Assert.AreEqual(@"E:\Moved\Game\saves", SavePaths.Expand(@"{InstallDir}\saves", @"E:\Moved\Game\", null));
        }

        [Test]
        public void ExpandRequiresPlaceholdersTest()
        {
            Assert.IsNull(SavePaths.Expand(@"%APPDATA%\Goldberg SteamEmu Saves\{SteamAppId}", null, null));
            Assert.IsNull(SavePaths.Expand(@"{InstallDir}\saves", null, "10"));
            StringAssert.EndsWith(@"Goldberg SteamEmu Saves\10", SavePaths.Expand(@"%APPDATA%\Goldberg SteamEmu Saves\{SteamAppId}", null, "10"));
        }

        [Test]
        public void SubPathTest()
        {
            Assert.IsTrue(SavePaths.IsSubPathOrEqual(@"C:\Saves\Game", @"C:\Saves"));
            Assert.IsTrue(SavePaths.IsSubPathOrEqual(@"C:\Saves\", @"c:\saves"));
            Assert.IsFalse(SavePaths.IsSubPathOrEqual(@"C:\Saves2\Game", @"C:\Saves"));
        }
    }

    [TestFixture]
    public class SaveWriteWatcherTests
    {
        private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        [Test]
        public void FindsFolderNamedAfterGameTest()
        {
            var files = new List<string>
            {
                Path.Combine(SavePaths.LocalLow, "Team Cherry", "Hollow Knight", "user1.dat"),
                Path.Combine(SavePaths.LocalLow, "Team Cherry", "Hollow Knight", "user1.dat.bak"),
                Path.Combine(Roaming, "SomeChatApp", "settings.json")
            };

            var candidates = SaveWriteWatcher.FindCandidates(files, "Hollow Knight", null, null);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(Path.Combine(SavePaths.LocalLow, "Team Cherry", "Hollow Knight"), candidates[0].Path);
            Assert.AreEqual(2, candidates[0].FileCount);
        }

        [Test]
        public void FindsFolderByMainTitleTest()
        {
            var files = new List<string> { Path.Combine(SavePaths.Documents, "My Games", "Fallout4", "Saves", "Save1.fos") };
            var candidates = SaveWriteWatcher.FindCandidates(files, "Fallout 4: Game of the Year Edition", null, null);
            Assert.AreEqual(Path.Combine(SavePaths.Documents, "My Games", "Fallout4"), candidates.Single().Path);
        }

        [Test]
        public void FindsSteamEmulatorFolderByAppIdTest()
        {
            var files = new List<string> { Path.Combine(SavePaths.PublicDocuments, "Steam", "CODEX", "1245620", "remote", "slot0.sl2") };
            var candidates = SaveWriteWatcher.FindCandidates(files, "Elden Ring", null, "1245620");
            Assert.AreEqual(Path.Combine(SavePaths.PublicDocuments, "Steam", "CODEX", "1245620"), candidates.Single().Path);
        }

        [Test]
        public void FindsSaveFolderInsideGameFolderTest()
        {
            var install = @"D:\Library\Old Game";
            var files = new List<string>
            {
                install + @"\Data\Saves\slot1.dat",
                install + @"\Logs\run.txt",
                install + @"\config.ini",
                install + @"\quick.sav"
            };

            var candidates = SaveWriteWatcher.FindCandidates(files, "Old Game", install, null).Select(a => a.Path).ToList();
            CollectionAssert.AreEquivalent(new[] { install + @"\Data\Saves", install }, candidates);
        }

        [Test]
        public void IgnoresUnrelatedWritesTest()
        {
            var files = new List<string>
            {
                Path.Combine(Local, "Vendor", "App", "state.json"),
                Path.Combine(Roaming, "Game Launcher", "cache.bin")
            };

            Assert.IsEmpty(SaveWriteWatcher.FindCandidates(files, "Celeste", null, null));
        }
    }

    [TestFixture]
    public class SaveBackupServiceTests
    {
        private string root;
        private InMemoryGameDatabase database;
        private GameroomDatabase gameroomDb;
        private SaveBackupService service;
        private GameroomSettings settings;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            database = new InMemoryGameDatabase();
            gameroomDb = new GameroomDatabase(root);
            settings = new GameroomSettings { SaveBackupDirectory = Path.Combine(root, "Backups"), MaxBackupsPerGame = 2 };
            service = new SaveBackupService(database, gameroomDb, () => settings);
        }

        [TearDown]
        public void TearDown()
        {
            service.Dispose();
            gameroomDb.Dispose();
            Directory.Delete(root, true);
        }

        private Game CreateGameWithSaves(out string saveDir)
        {
            var installDir = Path.Combine(root, "Install");
            saveDir = Path.Combine(installDir, "saves");
            Directory.CreateDirectory(Path.Combine(saveDir, "slot1"));
            File.WriteAllText(Path.Combine(saveDir, "slot1", "save.dat"), "level 1");
            File.WriteAllText(Path.Combine(saveDir, "settings.cfg"), "volume=5");
            Directory.CreateDirectory(Path.Combine(saveDir, "logs"));
            File.WriteAllText(Path.Combine(saveDir, "logs", "game.txt"), "log");

            var game = new Game("Test Game") { InstallDirectory = installDir, IsInstalled = true };
            database.Games.Add(game);
            return game;
        }

        [Test]
        public void BackupAndRestoreTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var locations = service.Resolver.Resolve(game);
            Assert.AreEqual(1, locations.Count);
            Assert.AreEqual(@"{InstallDir}\saves", locations[0].Template);

            var first = service.BackupGame(game, SaveBackupTrigger.SessionEnded);
            Assert.IsTrue(first.Success, first.Message);
            // Log folders are skipped.
            Assert.AreEqual(2, first.Backup.FileCount);
            Assert.IsTrue(File.Exists(service.GetArchivePath(first.Backup)));

            // Nothing changed, automatic backup is skipped.
            var unchanged = service.BackupGame(game, SaveBackupTrigger.SessionEnded);
            Assert.IsTrue(unchanged.Skipped);

            File.WriteAllText(Path.Combine(saveDir, "slot1", "save.dat"), "level 2 with progress");
            File.Delete(Path.Combine(saveDir, "settings.cfg"));
            var restore = service.RestoreBackup(game, first.Backup);
            Assert.IsTrue(restore.Success, restore.Message);
            Assert.AreEqual("level 1", File.ReadAllText(Path.Combine(saveDir, "slot1", "save.dat")));
            Assert.AreEqual("volume=5", File.ReadAllText(Path.Combine(saveDir, "settings.cfg")));

            // Saves present before restoring were backed up first.
            var backups = service.GetBackups(game);
            Assert.AreEqual(2, backups.Count);
            Assert.AreEqual(SaveBackupTrigger.BeforeRestore, backups[0].Trigger);
        }

        [Test]
        public void RestoreFollowsMovedInstallationTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var backup = service.BackupGame(game, SaveBackupTrigger.Manual).Backup;

            var newInstall = Path.Combine(root, "Moved");
            Directory.CreateDirectory(newInstall);
            game.InstallDirectory = newInstall;
            var restore = service.RestoreBackup(game, backup);
            Assert.IsTrue(restore.Success, restore.Message);
            Assert.AreEqual("level 1", File.ReadAllText(Path.Combine(newInstall, "saves", "slot1", "save.dat")));
        }

        [Test]
        public void RetentionKeepsManualBackupsTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var manual = service.BackupGame(game, SaveBackupTrigger.Manual).Backup;
            for (int i = 0; i < 4; i++)
            {
                File.WriteAllText(Path.Combine(saveDir, "slot1", "save.dat"), "progress " + i);
                File.SetLastWriteTime(Path.Combine(saveDir, "slot1", "save.dat"), DateTime.Now.AddMinutes(i + 1));
                Assert.IsTrue(service.BackupGame(game, SaveBackupTrigger.SessionEnded).Success);
            }

            var backups = service.GetBackups(game);
            Assert.AreEqual(1, backups.Count(a => a.Trigger == SaveBackupTrigger.Manual));
            Assert.AreEqual(2, backups.Count(a => a.Trigger == SaveBackupTrigger.SessionEnded));
            Assert.IsTrue(File.Exists(service.GetArchivePath(manual)));
        }

        [Test]
        public void UserLocationsAndDisabledLocationsTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var custom = Path.Combine(root, "Custom");
            Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom, "profile.bin"), "x");
            service.SetLocations(game, new List<SaveLocation>
            {
                new SaveLocation { Path = custom, Source = SaveLocationSource.User },
                new SaveLocation { Path = @"{InstallDir}\saves", Source = SaveLocationSource.Known, Enabled = false }
            });

            var locations = service.Resolver.Resolve(game);
            Assert.AreEqual(1, locations.Count);
            Assert.AreEqual(custom, locations[0].Path);
        }

        [Test]
        public void BacksUpWhilePlayingTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            service.OnSessionStarted(game);
            service.CheckRunningGames();
            var backups = service.GetBackups(game);
            Assert.AreEqual(1, backups.Count);
            Assert.AreEqual(SaveBackupTrigger.WhilePlaying, backups[0].Trigger);

            // Nothing changed, nothing new.
            service.CheckRunningGames();
            Assert.AreEqual(1, service.GetBackups(game).Count);

            for (int i = 0; i < 7; i++)
            {
                File.WriteAllText(Path.Combine(saveDir, "slot1", "save.dat"), "checkpoint " + i);
                File.SetLastWriteTime(Path.Combine(saveDir, "slot1", "save.dat"), DateTime.Now.AddMinutes(i + 1));
                service.CheckRunningGames();
            }

            Assert.AreEqual(5, service.GetBackups(game).Count, "Only the newest backups made while playing are kept.");

            // Session end keeps the last one as the backup of the session instead of making a copy.
            var newest = service.GetBackups(game)[0];
            Assert.IsTrue(service.BackupGame(game, SaveBackupTrigger.SessionEnded).Success);
            var afterEnd = service.GetBackups(game);
            Assert.AreEqual(5, afterEnd.Count);
            Assert.AreEqual(newest.Id, afterEnd[0].Id);
            Assert.AreEqual(SaveBackupTrigger.SessionEnded, afterEnd[0].Trigger);
        }

        [Test]
        public void FindsBackupsOfRemovedLibraryEntryTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            Assert.IsTrue(service.BackupGame(game, SaveBackupTrigger.Manual).Success);
            database.Games.Remove(game);

            var readded = new Game("test game") { InstallDirectory = game.InstallDirectory, IsInstalled = true };
            database.Games.Add(readded);
            Assert.AreEqual(1, service.GetBackups(readded).Count);

            var other = new Game("Other Game");
            database.Games.Add(other);
            Assert.IsEmpty(service.GetBackups(other));
        }

        [Test]
        public void OffersRestoreWhenSavesAreMissingTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            Assert.IsNull(service.GetRestoreCandidate(game), "No backups yet.");
            var backup = service.BackupGame(game, SaveBackupTrigger.SessionEnded).Backup;
            Assert.IsNull(service.GetRestoreCandidate(game), "Saves are present.");

            // Game was uninstalled together with its saves and installed again.
            Directory.Delete(saveDir, true);
            Assert.AreEqual(backup.Id, service.GetRestoreCandidate(game).Id);
            Assert.IsTrue(service.RestoreBackup(game, backup).Success);
            Assert.AreEqual("level 1", File.ReadAllText(Path.Combine(saveDir, "slot1", "save.dat")));
            Assert.IsNull(service.GetRestoreCandidate(game));
        }

        [Test]
        public void OffersRestoreAfterReinstallWithNewFilesTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var backup = service.BackupGame(game, SaveBackupTrigger.SessionEnded).Backup;

            // Reinstalled game created its settings file on first launch, the save itself is gone.
            File.Delete(Path.Combine(saveDir, "slot1", "save.dat"));
            Assert.IsNull(service.GetRestoreCandidate(game), "Some saves are present.");
            Assert.AreEqual(1, service.GetMissingFileCount(game, backup));
            Assert.AreEqual(backup.Id, service.GetRestoreCandidate(game, true).Id);

            File.WriteAllText(Path.Combine(saveDir, "slot1", "save.dat"), "new game");
            Assert.IsNull(service.GetRestoreCandidate(game, true), "Nothing is missing.");
        }

        [Test]
        public void ImportsBackupsFromBackupFolderTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            Assert.IsTrue(service.BackupGame(game, SaveBackupTrigger.Manual).Success);

            // New Gameroom library pointing to the same backup folder.
            var otherRoot = Path.Combine(root, "OtherLibrary");
            Directory.CreateDirectory(otherRoot);
            using (var otherDb = new GameroomDatabase(otherRoot))
            {
                var otherDatabase = new InMemoryGameDatabase();
                var otherService = new SaveBackupService(otherDatabase, otherDb, () => settings);
                Assert.AreEqual(1, otherService.ImportExistingBackups());
                Assert.AreEqual(0, otherService.ImportExistingBackups(), "Already imported.");

                var readded = new Game("Test Game") { InstallDirectory = game.InstallDirectory, IsInstalled = true };
                otherDatabase.Games.Add(readded);
                var found = otherService.GetBackups(readded).Single();
                Assert.AreEqual(SaveBackupTrigger.Manual, found.Trigger);
                Assert.AreEqual(2, found.FileCount);
                otherService.Dispose();
            }
        }

        [Test]
        public void RegistersBackupDownloadedFromCloudTest()
        {
            var game = CreateGameWithSaves(out var saveDir);
            var result = service.BackupGame(game, SaveBackupTrigger.Manual);
            Assert.IsTrue(result.Success);

            // Another PC downloads the archive into its own backup folder.
            var otherRoot = Path.Combine(root, "OtherPc");
            Directory.CreateDirectory(otherRoot);
            using (var otherDb = new GameroomDatabase(otherRoot))
            {
                var otherDatabase = new InMemoryGameDatabase();
                var otherService = new SaveBackupService(otherDatabase, otherDb, () => settings);
                var target = Path.Combine(otherService.BackupDirectory, "Test Game [cloud]", "cloud-copy.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(service.GetArchivePath(result.Backup), target);

                var registered = otherService.RegisterArchive(target, "name-testgame/cloud-copy.zip");
                Assert.AreEqual("name-testgame/cloud-copy.zip", registered.CloudObjectPath);
                Assert.AreEqual(result.Backup.ContentHash, registered.ContentHash);
                Assert.AreEqual(@"Test Game [cloud]\cloud-copy.zip", registered.ArchivePath);

                var reinstalled = new Game("Test Game") { InstallDirectory = game.InstallDirectory, IsInstalled = true };
                otherDatabase.Games.Add(reinstalled);
                Assert.AreEqual(registered.Id, otherService.GetBackups(reinstalled).Single().Id);
                otherService.Dispose();
            }
        }

        [Test]
        public void CloudGameKeyTest()
        {
            Assert.AreEqual("steam-2369900", Playnite.Gameroom.Cloud.CloudSaveSync.GetGameKey(" 2369900 ", "Castlevania"));
            Assert.AreEqual(
                Playnite.Gameroom.Cloud.CloudSaveSync.GetGameKey(null, "Castlevania: Dominus Collection"),
                Playnite.Gameroom.Cloud.CloudSaveSync.GetGameKey("", "castlevania dominus collection"));
            Assert.AreNotEqual(
                Playnite.Gameroom.Cloud.CloudSaveSync.GetGameKey(null, "Hollow Knight"),
                Playnite.Gameroom.Cloud.CloudSaveSync.GetGameKey(null, "Hollow Knight Silksong"));
        }

        [Test]
        public void LearnsLocationsFromWrittenFilesTest()
        {
            var game = new Game("Learned Game") { InstallDirectory = Path.Combine(root, "LearnedInstall"), IsInstalled = true };
            database.Games.Add(game);
            // Not one of the folder names checked by default, so it can only be found by learning.
            var saveDir = Path.Combine(game.InstallDirectory, "UserData");
            Directory.CreateDirectory(saveDir);
            File.WriteAllText(Path.Combine(saveDir, "a.bin"), "x");

            var learned = service.LearnLocations(game, new List<string> { Path.Combine(saveDir, "a.bin") });
            Assert.AreEqual(1, learned.Count);
            var data = gameroomDb.GetGameData(game.Id);
            Assert.AreEqual(@"{InstallDir}\UserData", data.SaveLocations.Single().Path);
            Assert.AreEqual(SaveLocationSource.Learned, data.SaveLocations.Single().Source);
        }
    }
}
