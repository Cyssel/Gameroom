using NUnit.Framework;
using Playnite.Gameroom;
using System;
using System.IO;

namespace Gameroom.Tests
{
    [TestFixture]
    public class UnknownGameDetectorTests
    {
        private string root;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(root, true);
        }

        private string CreateFile(string relativePath, string content = "")
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private static ProcessSnapshot Process(string path, bool fullscreen = false)
        {
            return new ProcessSnapshot { Id = 1, Path = path, HasWindow = true, IsForeground = fullscreen, IsFullscreen = fullscreen };
        }

        [Test]
        public void DetectsUnityGameTest()
        {
            var exe = CreateFile(@"Hollow.Knight.v1.5.78-CODEX\hollow_knight.exe");
            CreateFile(@"Hollow.Knight.v1.5.78-CODEX\UnityPlayer.dll");
            CreateFile(@"Hollow.Knight.v1.5.78-CODEX\steam_appid.txt", "367520\n");
            Directory.CreateDirectory(Path.Combine(root, @"Hollow.Knight.v1.5.78-CODEX\hollow_knight_Data"));

            var info = new UnknownGameDetector().Inspect(Process(exe));
            Assert.IsNotNull(info);
            Assert.AreEqual("Unity", info.Engine);
            Assert.AreEqual("Hollow Knight", info.SuggestedName);
            Assert.AreEqual("367520", info.SteamAppId);
            Assert.AreEqual(Path.Combine(root, "Hollow.Knight.v1.5.78-CODEX"), info.RootDirectory);
        }

        [Test]
        public void DetectsUnrealGameRootTest()
        {
            var exe = CreateFile(@"Some Game\SomeGame\Binaries\Win64\SomeGame-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.Combine(root, @"Some Game\Engine"));

            var info = new UnknownGameDetector().Inspect(Process(exe));
            Assert.IsNotNull(info);
            Assert.AreEqual("Unreal Engine", info.Engine);
            Assert.AreEqual(Path.Combine(root, "Some Game"), info.RootDirectory);
            Assert.AreEqual("Some Game", info.SuggestedName);
        }

        [Test]
        public void DetectsSteamEmulatorAppIdTest()
        {
            var exe = CreateFile(@"Cracked Game\bin\game.exe");
            CreateFile(@"Cracked Game\bin\steam_api64.dll");
            CreateFile(@"Cracked Game\bin\steam_emu.ini", "[Settings]\nAppId=480\n");

            var info = new UnknownGameDetector().Inspect(Process(exe));
            Assert.IsNotNull(info);
            Assert.AreEqual("480", info.SteamAppId);
            // "bin" is a wrapper folder, the game root is its parent.
            Assert.AreEqual(Path.Combine(root, "Cracked Game"), info.RootDirectory);
            Assert.AreEqual("Cracked Game", info.SuggestedName);
        }

        [Test]
        public void FindsAppIdInUnityPluginsFolderTest()
        {
            var exe = CreateFile(@"Castlevania\Castlevania Belmont's Curse.exe");
            CreateFile(@"Castlevania\UnityPlayer.dll");
            CreateFile(@"Castlevania\Castlevania Belmont's Curse_Data\Plugins\x86_64\steam_api64.dll");
            CreateFile(@"Castlevania\Castlevania Belmont's Curse_Data\Plugins\x86_64\steam_emu.ini", "[Settings]\r\nAppId=4231820\r\n[Interfaces]\r\n");

            var info = new UnknownGameDetector().Inspect(Process(exe));
            Assert.AreEqual("4231820", info.SteamAppId);
            Assert.AreEqual("4231820", UnknownGameDetector.FindSteamAppIdInGameFolder(Path.Combine(root, "Castlevania")));
        }

        [Test]
        public void IgnoresRegularApplicationsTest()
        {
            var exe = CreateFile(@"Tools\tool.exe");
            Assert.IsNull(new UnknownGameDetector().Inspect(Process(exe)));
            Assert.IsNull(new UnknownGameDetector().Inspect(Process(exe, true)));
        }

        [Test]
        public void WeakEvidenceNeedsFullscreenTest()
        {
            var exe = CreateFile(@"Old Game\oldgame.exe");
            CreateFile(@"Old Game\binkw32.dll");

            Assert.IsNull(new UnknownGameDetector().Inspect(Process(exe)));
            var info = new UnknownGameDetector().Inspect(Process(exe, true));
            Assert.IsNotNull(info);
            Assert.IsNull(info.Engine);
        }

        [Test]
        public void IgnoresWindowlessProcessesTest()
        {
            var exe = CreateFile(@"Unity Game\game.exe");
            CreateFile(@"Unity Game\UnityPlayer.dll");
            var process = Process(exe);
            process.HasWindow = false;
            Assert.IsNull(new UnknownGameDetector().Inspect(process));
        }
    }
}
