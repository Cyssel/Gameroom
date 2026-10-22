using NUnit.Framework;
using Playnite.Gameroom;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Gameroom.Tests
{
    [TestFixture]
    public class GameMatcherTests
    {
        private static GameMatchInfo Info(Guid id, string dir = null, params string[] exes)
        {
            var info = new GameMatchInfo { Id = id };
            if (dir != null)
            {
                info.InstallDirectories.Add(dir);
            }

            info.Executables.AddRange(exes);
            return info;
        }

        [Test]
        public void IgnoresStoreLaunchersTest()
        {
            var valorant = new Game("VALORANT")
            {
                IsInstalled = true,
                InstallDirectory = @"D:\Riot Games\VALORANT\live",
                GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction
                    {
                        Type = GameActionType.File,
                        Path = @"D:\Riot Games\Riot Client\RiotClientServices.exe",
                        Arguments = "--launch-product=valorant --launch-patchline=live",
                        IsPlayAction = true
                    }
                }
            };
            var albion = new Game("Albion Online")
            {
                IsInstalled = true,
                InstallDirectory = @"D:\Albion",
                GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction { Type = GameActionType.File, Path = @"{InstallDir}\launcher\AlbionLauncher.exe", IsPlayAction = true }
                }
            };

            var endfield = new Game("Arknights: Endfield")
            {
                IsInstalled = true,
                InstallDirectory = @"D:\GRYPHLINK\games\EndField Game",
                GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction { Type = GameActionType.File, Path = @"D:\GRYPHLINK\Launcher.exe", IsPlayAction = true }
                }
            };

            var matcher = GameMatcher.FromLibrary(new[] { valorant, albion, endfield }, null, null);
            Assert.IsNull(matcher.Match(@"D:\Riot Games\Riot Client\RiotClientServices.exe"), "The Riot client running isn't VALORANT running.");
            Assert.IsNull(matcher.Match(@"D:\GRYPHLINK\Launcher.exe"), "A launcher holding the game folder isn't the game.");
            Assert.AreEqual(endfield.Id, matcher.Match(@"D:\GRYPHLINK\games\EndField Game\Endfield.exe"));
            Assert.AreEqual(valorant.Id, matcher.Match(@"D:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"));
            Assert.AreEqual(albion.Id, matcher.Match(@"D:\Albion\launcher\AlbionLauncher.exe"));
        }

        [Test]
        public void MatchesByInstallDirectoryTest()
        {
            var fallout = Guid.NewGuid();
            var fallout2 = Guid.NewGuid();
            var matcher = new GameMatcher(new[]
            {
                Info(fallout, @"D:\Library\Fallout"),
                Info(fallout2, @"D:\Library\Fallout 2\")
            }, null);

            Assert.AreEqual(fallout, matcher.Match(@"D:\Library\Fallout\fallout.exe"));
            Assert.AreEqual(fallout, matcher.Match(@"d:\library\fallout\bin\FALLOUT.EXE"));
            Assert.AreEqual(fallout2, matcher.Match(@"D:\Library\Fallout 2\fallout2.exe"));
            Assert.IsNull(matcher.Match(@"D:\Library\Fallout Tactics\bos.exe"));
        }

        [Test]
        public void NestedInstallationWinsTest()
        {
            var parent = Guid.NewGuid();
            var child = Guid.NewGuid();
            var matcher = new GameMatcher(new[]
            {
                Info(parent, @"D:\Library\Collection"),
                Info(child, @"D:\Library\Collection\Episode 2")
            }, null);

            Assert.AreEqual(child, matcher.Match(@"D:\Library\Collection\Episode 2\game.exe"));
            Assert.AreEqual(parent, matcher.Match(@"D:\Library\Collection\Episode 1\game.exe"));
        }

        [Test]
        public void IgnoresGenericFoldersTest()
        {
            var matcher = new GameMatcher(new[]
            {
                Info(Guid.NewGuid(), @"C:\"),
                Info(Guid.NewGuid(), @"D:\SteamLibrary\steamapps\common"),
                Info(Guid.NewGuid(), @"E:\Games"),
                Info(Guid.NewGuid(), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            }, null);

            Assert.IsNull(matcher.Match(@"C:\Tools\tool.exe"));
            Assert.IsNull(matcher.Match(@"D:\SteamLibrary\steamapps\common\Portal\hl2.exe"));
            Assert.IsNull(matcher.Match(@"E:\Games\Something\game.exe"));
            Assert.IsNull(matcher.Match(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\App\app.exe"));
        }

        [Test]
        public void SharedFolderOnlyMatchesExecutablesTest()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var matcher = new GameMatcher(new[]
            {
                Info(first, @"D:\Library\Pack", @"D:\Library\Pack\first.exe"),
                Info(second, @"D:\Library\Pack", @"D:\Library\Pack\second.exe")
            }, null);

            Assert.AreEqual(first, matcher.Match(@"D:\Library\Pack\first.exe"));
            Assert.AreEqual(second, matcher.Match(@"D:\Library\Pack\second.exe"));
            Assert.IsNull(matcher.Match(@"D:\Library\Pack\third.exe"));
        }

        [Test]
        public void SkipsHelperExecutablesTest()
        {
            var game = Guid.NewGuid();
            var matcher = new GameMatcher(new[]
            {
                Info(game, @"D:\Library\Unity Game", @"D:\Library\Unity Game\Tools\CrashReporter.exe")
            }, null);

            Assert.IsNull(matcher.Match(@"D:\Library\Unity Game\UnityCrashHandler64.exe"));
            Assert.IsNull(matcher.Match(@"D:\Library\Unity Game\unins000.exe"));
            Assert.IsNull(matcher.Match(@"D:\Library\Unity Game\_CommonRedist\vcredist_x64.exe"));
            Assert.IsNull(matcher.Match(@"D:\Library\Unity Game\EasyAntiCheat\EasyAntiCheat_Setup.exe"));
            // Explicitly listed executables are always trusted.
            Assert.AreEqual(game, matcher.Match(@"D:\Library\Unity Game\Tools\CrashReporter.exe"));
            Assert.AreEqual(game, matcher.Match(@"D:\Library\Unity Game\Game.exe"));
        }

        [Test]
        public void IgnoredExecutablesTest()
        {
            var game = Guid.NewGuid();
            var matcher = new GameMatcher(new[] { Info(game, @"D:\Library\Game") }, new List<string> { @"D:\Library\Game\editor.exe" });
            Assert.IsNull(matcher.Match(@"D:\Library\Game\editor.exe"));
            Assert.IsTrue(matcher.IsIgnored(@"d:\library\game\EDITOR.exe"));
            Assert.AreEqual(game, matcher.Match(@"D:\Library\Game\game.exe"));
        }
    }
}
