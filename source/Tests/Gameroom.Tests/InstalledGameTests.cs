using NUnit.Framework;
using Playnite.Database;
using Playnite.Gameroom;
using Playnite.Gameroom.InstalledGames;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gameroom.Tests
{
    [TestFixture]
    public class InstalledGameScannerTests
    {
        private string root;
        private InstalledGameScanner scanner;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            scanner = new InstalledGameScanner(new UnknownGameDetector());
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(root, true);
        }

        public static string CreateUnityGame(string root, string folder, string exeName)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(Path.Combine(dir, exeName + "_Data"));
            File.WriteAllText(Path.Combine(dir, exeName + ".exe"), "game");
            File.WriteAllText(Path.Combine(dir, "UnityPlayer.dll"), "engine");
            File.WriteAllText(Path.Combine(dir, "UnityCrashHandler64.exe"), "crash");
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "uninstaller");
            return dir;
        }

        public static string CreateLauncher(string root)
        {
            var dir = Path.Combine(root, "GRYPHLINK");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Launcher.exe"), "launcher");
            File.WriteAllText(Path.Combine(dir, "Uninstall.exe"), "uninstaller");
            var game = CreateUnityGame(Path.Combine(dir, "games"), "EndField Game", "Endfield");
            File.WriteAllText(Path.Combine(game, "Endfield_Data", "app.info"), "Gryphline\nArknights: Endfield");
            // Leftover of a removed game.
            Directory.CreateDirectory(Path.Combine(dir, "games", "Old Game"));
            return dir;
        }

        private DetectedInstalledGame ClassifyOne(InstalledProgram program)
        {
            return scanner.Classify(program).SingleOrDefault();
        }

        [Test]
        public void CleansNamesTest()
        {
            Assert.AreEqual("Pragmata", InstalledGameScanner.CleanName("Pragmata MULTi14 - ElAmigos versión 15.04.2026"));
            Assert.AreEqual("Castlevania Belmonts Curse", InstalledGameScanner.CleanName("Castlevania Belmonts Curse MULTi11 - ElAmigos version 1.0"));
            Assert.AreEqual("Lost In Random", InstalledGameScanner.CleanName("Lost In Random™"));
            Assert.AreEqual("Roblox", InstalledGameScanner.CleanName("Roblox Player for player1"));
            Assert.AreEqual("Dragon Raja", InstalledGameScanner.CleanName("Dragon Raja Launcher"));
            Assert.AreEqual("Some Game", InstalledGameScanner.CleanName("Some Game [FitGirl Repack]"));
            Assert.AreEqual("VALORANT", InstalledGameScanner.CleanName("VALORANT"));
            Assert.AreEqual("Launcher", InstalledGameScanner.CleanName("Launcher"), "Never cleaned to nothing.");
        }

        [Test]
        public void ParsesCommandLinesTest()
        {
            Assert.AreEqual(@"D:\Game\unins000.exe", InstalledGameScanner.GetCommandPath("\"D:\\Game\\unins000.exe\" /SILENT"));
            Assert.AreEqual(@"C:\Roblox\RobloxPlayerInstaller.exe", InstalledGameScanner.GetCommandPath(@"C:\Roblox\RobloxPlayerInstaller.exe,0"));
            Assert.AreEqual(@"D:\Riot Games\VALORANT\live", InstalledGameScanner.GetCommandPath("D:/Riot Games/VALORANT/live"));
            Assert.IsNull(InstalledGameScanner.GetCommandPath(" "));

            Assert.AreEqual("1056640", InstalledGameScanner.GetSteamAppId("\"D:\\Steam\\steam.exe\" steam://uninstall/1056640"));
            Assert.IsNull(InstalledGameScanner.GetSteamAppId("MsiExec.exe /X{1234}"));

            var riot = InstalledGameScanner.GetRiotProduct("\"D:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live");
            Assert.AreEqual(@"D:\Riot Games\Riot Client\RiotClientServices.exe", riot.Value.Client);
            Assert.AreEqual("valorant", riot.Value.Product);
            Assert.AreEqual("live", riot.Value.Patchline);

            var hoyo = InstalledGameScanner.GetHoYoPlayGame(@"C:\Program Files\HoYoPlay\launcher.exe --uninstall_game=hk4e_global");
            Assert.AreEqual(@"C:\Program Files\HoYoPlay\launcher.exe", hoyo.Value.Launcher);
            Assert.AreEqual("hk4e_global", hoyo.Value.Game);
        }

        [Test]
        public void FindsGameByEngineTest()
        {
            var dir = CreateUnityGame(root, "Cozy Game", "CozyGame");
            var game = ClassifyOne(new InstalledProgram
            {
                Key = "uninstall:cozy",
                Name = "Cozy Game",
                InstallLocation = dir + "\\",
                UninstallString = $"\"{dir}\\unins000.exe\""
            });

            Assert.IsNotNull(game);
            Assert.AreEqual("Cozy Game", game.Name);
            Assert.AreEqual(Path.Combine(dir, "CozyGame.exe"), game.Executable, "Crash handlers and uninstallers are never started.");
            Assert.IsNull(game.Source);
        }

        [Test]
        public void UsesRegisteredLauncherTest()
        {
            var dir = Path.Combine(root, "Online Game");
            CreateUnityGame(dir, "game", "Client");
            File.WriteAllText(Path.Combine(dir, "OnlineLauncher.exe"), "launcher");
            File.WriteAllText(Path.Combine(dir, "uninstall.exe"), "uninstaller");

            // No install location: the folder comes from the icon and the uninstaller.
            var game = ClassifyOne(new InstalledProgram
            {
                Key = "uninstall:online",
                Name = "Online Game Launcher",
                DisplayIcon = Path.Combine(dir, "OnlineLauncher.exe"),
                UninstallString = Path.Combine(dir, "uninstall.exe")
            });

            Assert.IsNotNull(game);
            Assert.AreEqual("Online Game", game.Name);
            Assert.AreEqual(Path.Combine(dir, "OnlineLauncher.exe"), game.Executable, "Online games start from their launcher.");
        }

        [Test]
        public void IgnoresApplicationsTest()
        {
            var tool = Path.Combine(root, "Photo Tool");
            Directory.CreateDirectory(tool);
            File.WriteAllText(Path.Combine(tool, "PhotoTool.exe"), "app");
            File.WriteAllText(Path.Combine(tool, "imaging.dll"), "library");
            Assert.IsNull(ClassifyOne(new InstalledProgram { Key = "uninstall:tool", Name = "Photo Tool", InstallLocation = tool }));

            var unity = CreateUnityGame(root, "Unity Editor", "Editor");
            Assert.IsNull(ClassifyOne(new InstalledProgram { Key = "uninstall:unity", Name = "Unity 6000.5.1f1", InstallLocation = unity }), "Developer tools.");
            Assert.IsNull(ClassifyOne(new InstalledProgram { Key = "uninstall:steam", Name = "Steam", InstallLocation = unity }), "Launchers.");

            Assert.IsNull(ClassifyOne(new InstalledProgram
            {
                Key = "uninstall:gone",
                Name = "Removed Game MULTi5 - ElAmigos",
                InstallLocation = Path.Combine(root, "Removed Game"),
                UninstallString = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Some Launcher", "uninstall.exe")
            }), "Its folder doesn't exist anymore.");
        }

        [Test]
        public void FindsGamesInsideLaunchersTest()
        {
            var dir = CreateLauncher(root);
            var games = scanner.Classify(new InstalledProgram
            {
                Key = "uninstall:GRYPHLINK",
                Name = "GRYPHLINK",
                InstallLocation = dir,
                DisplayIcon = Path.Combine(dir, "Launcher.exe"),
                UninstallString = Path.Combine(dir, "Uninstall.exe")
            });

            var game = games.Single();
            Assert.AreEqual("Arknights: Endfield", game.Name, "Unity player settings name the game.");
            Assert.AreEqual("uninstall:GRYPHLINK|EndField Game", game.Key);
            Assert.AreEqual(Path.Combine(dir, "games", "EndField Game") + "\\", game.InstallDirectory);
            Assert.AreEqual(Path.Combine(dir, "Launcher.exe"), game.Executable, "Started with the launcher, which signs in.");
            Assert.AreEqual(Path.Combine(dir, "games", "EndField Game", "Endfield.exe"), game.GameExecutable);
            Assert.AreEqual("GRYPHLINK", game.Source);
        }

        public static string CreateRagnarokClient(string dir, string launcher, string client)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, launcher + ".exe"), "patcher");
            File.WriteAllText(Path.Combine(dir, client + ".exe"), "client client client");
            File.WriteAllText(Path.Combine(dir, "data.grf"), "resources");
            File.WriteAllText(Path.Combine(dir, "Setup.exe"), "settings");
            return dir;
        }

        [Test]
        public void FindsGamesFromShortcutsTest()
        {
            var dir = CreateRagnarokClient(Path.Combine(root, "Xatiya"), "XATIYARO", "Xatiya");
            var game = ClassifyOne(new InstalledProgram
            {
                Key = "shortcut:xatiyaro",
                Kind = InstalledProgramKind.Shortcut,
                Name = "XATIYARO",
                DisplayIcon = Path.Combine(dir, "XATIYARO.exe"),
                Arguments = "-server latam"
            });

            Assert.IsNotNull(game, "Ragnarok Online clients are recognized by their resource files.");
            Assert.AreEqual("XATIYARO", game.Name);
            Assert.AreEqual(Path.Combine(dir, "XATIYARO.exe"), game.Executable);
            Assert.AreEqual("-server latam", game.Arguments, "Started like its shortcut.");

            File.WriteAllText(Path.Combine(dir, "XATIYARO_Uninstall.exe"), "uninstaller");
            Assert.IsNull(ClassifyOne(new InstalledProgram
            {
                Key = "shortcut:uninstall",
                Kind = InstalledProgramKind.Shortcut,
                Name = "Desinstalar",
                DisplayIcon = Path.Combine(dir, "XATIYARO_Uninstall.exe")
            }), "Uninstaller shortcuts sit next to games but aren't them.");
            Assert.IsNull(ClassifyOne(new InstalledProgram
            {
                Key = "shortcut:uninstall2",
                Kind = InstalledProgramKind.Shortcut,
                Name = "Uninstall XATIYARO",
                DisplayIcon = Path.Combine(dir, "XATIYARO.exe")
            }));
        }

        [Test]
        public void FindsGamesInDriveRootTest()
        {
            var drive = CreateRagnarokClient(Path.Combine(root, "Drive"), "Ragnarok", "Ragexe");
            var other = CreateUnityGame(drive, "Other Game", "Other");
            File.WriteAllText(Path.Combine(other, "steam_appid.txt"), "761890");
            var game = ClassifyOne(new InstalledProgram
            {
                Key = "drive:d",
                Kind = InstalledProgramKind.DriveRoot,
                Name = drive,
                InstallLocation = drive
            });

            Assert.AreEqual("Ragnarok Online", game.Name, "Only the programs right in the root count, not its folders.");
            Assert.AreEqual(Path.Combine(drive, "Ragnarok.exe"), game.Executable, "The launcher patches and signs in.");
            Assert.AreEqual(Path.Combine(drive, "Ragexe.exe"), game.GameExecutable);
            Assert.IsNull(game.InstallDirectory, "A whole drive is never a game folder.");
            Assert.IsNull(game.SteamAppId, "The Steam app of a game in a folder of the drive isn't this game's.");
            Assert.IsNull(UnknownGameDetector.FindSteamAppIdInGameFolder(Path.GetPathRoot(root)), "Drives are never searched whole.");
        }

        [Test]
        public void FindsGameFoldersTest()
        {
            CreateUnityGame(root, "Cozy.Game.v1.2-CODEX", "CozyGame");
            CreateUnityGame(Path.Combine(root, "Games"), "Puzzle Game", "Puzzle");
            CreateUnityGame(Path.Combine(root, "Riot Games"), "VALORANT", "Valorant");
            Directory.CreateDirectory(Path.Combine(root, "SteamLibrary", "steamapps"));
            Directory.CreateDirectory(Path.Combine(root, "Windows"));
            Directory.CreateDirectory(Path.Combine(root, "Notes"));

            var folders = InstalledGameScanner.FindGameFolders(new[] { root }, new[] { Path.Combine(root, "Riot Games", "VALORANT") });
            var names = folders.Select(a => a.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { GameMatcher.NormalizeDirectory(root), "Cozy.Game.v1.2-CODEX", "Puzzle Game", "Notes" }, names,
                "Folders holding known games, Steam libraries and system folders are skipped; Games folders hold one game per folder.");
            Assert.AreEqual(InstalledProgramKind.DriveRoot, folders.Single(a => a.Name == GameMatcher.NormalizeDirectory(root)).Kind);

            var cozy = ClassifyOne(folders.Single(a => a.Name == "Cozy.Game.v1.2-CODEX"));
            Assert.AreEqual("Cozy Game", cozy.Name, "Release tags aren't part of the name.");
            Assert.IsNull(ClassifyOne(folders.Single(a => a.Name == "Notes")));
        }

        [Test]
        public void IgnoresStoreClientsTest()
        {
            var dir = Path.Combine(root, "EA Desktop");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "EADesktop.exe"), "client");
            File.WriteAllText(Path.Combine(dir, "EAUpdater.exe"), "updater");
            File.WriteAllText(Path.Combine(dir, "EAConnect.exe"), "connect");
            File.WriteAllText(Path.Combine(dir, "steam_api64.dll"), "steam");

            Assert.IsNull(ClassifyOne(new InstalledProgram
            {
                Key = "shortcut:eaupdater",
                Kind = InstalledProgramKind.Shortcut,
                Name = "EA app update",
                DisplayIcon = Path.Combine(dir, "EAUpdater.exe")
            }), "Store clients ship Steam API files but aren't games.");
        }

        [Test]
        public void RecognizesRepacksWithoutEngineFilesTest()
        {
            var dir = Path.Combine(root, "Overcooked");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Overcooked.exe"), "game");
            File.WriteAllText(Path.Combine(dir, "unins000.exe"), "uninstaller");
            var game = ClassifyOne(new InstalledProgram
            {
                Key = "uninstall:overcooked",
                Name = "Overcooked Gourmet Edition MULTi6 - ElAmigos version 2",
                InstallLocation = dir
            });

            Assert.AreEqual("Overcooked Gourmet Edition", game.Name);
            Assert.AreEqual(Path.Combine(dir, "Overcooked.exe"), game.Executable);
        }
    }

    [TestFixture]
    public class InstalledGameImporterTests
    {
        private string root;
        private InMemoryGameDatabase database;
        private GameroomDatabase gameroomDb;
        private InstalledGameImporter importer;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            database = new InMemoryGameDatabase();
            gameroomDb = new GameroomDatabase(root);
            importer = new InstalledGameImporter(database, gameroomDb, new UnknownGameDetector());
        }

        [TearDown]
        public void TearDown()
        {
            gameroomDb.Dispose();
            Directory.Delete(root, true);
        }

        private List<InstalledProgram> Programs(string dir)
        {
            var notes = Path.Combine(root, "Notes App");
            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, "Notes.exe"), "app");
            return new List<InstalledProgram>
            {
                new InstalledProgram { Key = "uninstall:cozy", Name = "Cozy Game MULTi2 - ElAmigos version 1.0", InstallLocation = dir },
                new InstalledProgram { Key = "uninstall:notes", Name = "Notes App", InstallLocation = notes }
            };
        }

        [Test]
        public void AddsGamesOnceTest()
        {
            var dir = InstalledGameScannerTests.CreateUnityGame(root, "Cozy Game", "CozyGame");
            var epicGame = new DetectedInstalledGame
            {
                Key = "epic:fortnite",
                Name = "Fortnite",
                InstallDirectory = Path.Combine(root, "Fortnite"),
                Url = "com.epicgames.launcher://apps/fn?action=launch&silent=true",
                Source = "Epic"
            };

            var result = importer.Import(Programs(dir), new[] { epicGame }, false);
            CollectionAssert.AreEquivalent(new[] { "Cozy Game", "Fortnite" }, result.Added.Select(a => a.Name));

            var cozy = database.Games.Single(a => a.Name == "Cozy Game");
            Assert.IsTrue(cozy.IsInstalled);
            Assert.AreEqual(dir, cozy.InstallDirectory);
            var action = cozy.GameActions.Single();
            Assert.AreEqual(GameActionType.File, action.Type);
            Assert.AreEqual(@"{InstallDir}\CozyGame.exe", action.Path);
            Assert.AreEqual(1, cozy.PlatformIds?.Count, "Metadata providers need a platform.");

            var fortnite = database.Games.Single(a => a.Name == "Fortnite");
            Assert.AreEqual(GameActionType.URL, fortnite.GameActions.Single().Type);
            Assert.AreEqual("Epic", database.Sources[fortnite.SourceId].Name);

            Assert.AreEqual(0, importer.Import(Programs(dir), new[] { epicGame }, true).Added.Count, "Already in the library.");
        }

        [Test]
        public void AddsFolderGamesOnceTest()
        {
            var drive = Path.Combine(root, "Drive");
            InstalledGameScannerTests.CreateUnityGame(drive, "Cozy Game", "CozyGame");
            var added = importer.Import(new InstalledProgram[0], new DetectedInstalledGame[0], new[] { drive }, false).Added.Single();
            Assert.AreEqual("Cozy Game", added.Name);
            Assert.AreEqual(Path.Combine(drive, "Cozy Game"), added.InstallDirectory);

            Assert.AreEqual(0, importer.Import(new InstalledProgram[0], new DetectedInstalledGame[0], new[] { drive }, true).Added.Count,
                "Its folder is known now.");
        }

        [Test]
        public void AddsGamesInsideLaunchersTest()
        {
            var dir = InstalledGameScannerTests.CreateLauncher(root);
            var launcher = new[]
            {
                new InstalledProgram { Key = "uninstall:GRYPHLINK", Name = "GRYPHLINK", InstallLocation = dir, DisplayIcon = Path.Combine(dir, "Launcher.exe") }
            };

            var endfield = importer.Import(launcher, new DetectedInstalledGame[0], false).Added.Single();
            Assert.AreEqual("Arknights: Endfield", endfield.Name);
            Assert.AreEqual(Path.Combine(dir, "Launcher.exe"), endfield.GameActions.Single().Path);
            CollectionAssert.Contains(gameroomDb.GetGameData(endfield.Id).KnownExecutables, Path.Combine(dir, "games", "EndField Game", "Endfield.exe"),
                "Found running by its own program, not by the launcher.");

            Assert.AreEqual(0, importer.Import(launcher, new DetectedInstalledGame[0], true).Added.Count, "Already in the library.");
        }

        [Test]
        public void LeavesStoreGamesToTheirExtensionTest()
        {
            var steamPlugin = Playnite.SDK.BuiltinExtensions.GetIdFromExtension(Playnite.SDK.BuiltinExtension.SteamLibrary);
            var withExtension = new InstalledGameImporter(database, gameroomDb, new UnknownGameDetector(), () => new List<Guid> { steamPlugin });
            var stardew = new DetectedInstalledGame { Key = "uninstall:Steam App 413150", Name = "Stardew Valley", Url = "steam://rungameid/413150", SteamAppId = "413150", Source = "Steam" };

            Assert.AreEqual(0, withExtension.Import(new InstalledProgram[0], new[] { stardew }, false).Added.Count, "The Steam extension imports it.");
            Assert.AreEqual(1, importer.Import(new InstalledProgram[0], new[] { stardew }, false).Added.Count, "Without the extension Gameroom adds it.");
        }

        [Test]
        public void RemembersGamesDetectedWhileRunningTest()
        {
            var dir = InstalledGameScannerTests.CreateUnityGame(root, "Hell Maiden", "Hell Maiden");
            var exe = Path.Combine(dir, "Hell Maiden.exe");
            var game = new Game("Hell Maiden") { InstallDirectory = dir, IsInstalled = true };
            database.Games.Add(game);

            importer.CompleteDetectedGame(game, exe);
            Assert.AreEqual(1, game.PlatformIds?.Count, "Metadata providers need a platform.");
            Assert.IsFalse(importer.WasRemoved(exe, dir));

            database.Games.Remove(game);
            importer.OnGamesRemoved(new[] { game });
            Assert.IsTrue(importer.WasRemoved(exe, dir), "Removed on purpose: live detection doesn't add it again.");
            Assert.IsTrue(importer.WasRemoved(exe.ToUpperInvariant(), dir), "Paths are compared without case.");
        }

        [Test]
        public void RespectsRemovedGamesTest()
        {
            var dir = InstalledGameScannerTests.CreateUnityGame(root, "Cozy Game", "CozyGame");
            var added = importer.Import(Programs(dir), new DetectedInstalledGame[0], false).Added.Single();
            database.Games.Remove(added);
            importer.OnGamesRemoved(new[] { added });

            Assert.AreEqual(0, importer.Import(Programs(dir), new DetectedInstalledGame[0], true).Added.Count, "Removed on purpose.");
            Assert.AreEqual(0, database.Games.Count);
        }

        [Test]
        public void MatchesGamesAlreadyInLibraryTest()
        {
            var dir = InstalledGameScannerTests.CreateUnityGame(root, "Cozy Game", "CozyGame");
            // Same game, added by hand before and then uninstalled.
            var existing = new Game("Cozy Game") { IsInstalled = false };
            database.Games.Add(existing);

            var result = importer.Import(Programs(dir), new DetectedInstalledGame[0], false);
            Assert.AreEqual(0, result.Added.Count);
            Assert.AreEqual(existing.Id, result.Reinstalled.Single().Id);
            Assert.IsTrue(existing.IsInstalled);
            Assert.AreEqual(dir, existing.InstallDirectory);
            Assert.AreEqual(@"{InstallDir}\CozyGame.exe", existing.GameActions.Single().Path);
        }
    }
}
