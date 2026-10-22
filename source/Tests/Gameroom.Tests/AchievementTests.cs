using NUnit.Framework;
using Playnite.Common;
using Playnite.Database;
using Playnite.Gameroom;
using Playnite.Gameroom.Achievements;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Gameroom.Tests
{
    [TestFixture]
    public class AchievementReaderTests
    {
        [Test]
        public void ParsesRuneIniTest()
        {
            var ini = "[1]\r\nAchieved=1\r\nCurProgress=0\r\nMaxProgress=0\r\nUnlockTime=1792384953\r\n\r\n" +
                "[]\r\nAchieved=1\r\nUnlockTime=1773991374\r\n\r\n" +
                "[trophy_022]\r\nAchieved=0\r\nCurProgress=1\r\nMaxProgress=18\r\nUnlockTime=0\r\n\r\n" +
                "[SteamAchievements]\r\n00000=1\r\nCount=1\r\n";
            var result = AchievementReaders.ParseIni(ini);
            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result["1"].Unlocked);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1792384953).LocalDateTime, result["1"].UnlockTime);
            Assert.IsFalse(result["trophy_022"].Unlocked);
            Assert.IsNull(result["trophy_022"].UnlockTime);
            Assert.AreEqual(18, result["trophy_022"].MaxProgress);
        }

        [Test]
        public void ParsesOnlineFixIniTest()
        {
            var result = AchievementReaders.ParseIni("[ACH_WIN]\nachieved=true\ntimestamp=1700000000\n[ACH_LOSE]\nachieved=false\n");
            Assert.IsTrue(result["ACH_WIN"].Unlocked);
            Assert.IsNotNull(result["ACH_WIN"].UnlockTime);
            Assert.IsFalse(result["ACH_LOSE"].Unlocked);
        }

        [Test]
        public void ParsesGoldbergJsonTest()
        {
            var result = AchievementReaders.ParseGoldbergJson(
                "{\"ACH_A\": {\"earned\": true, \"earned_time\": 1700000000}, \"ACH_B\": {\"earned\": false, \"earned_time\": 0}}");
            Assert.IsTrue(result["ACH_A"].Unlocked);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, result["ACH_A"].UnlockTime);
            Assert.IsFalse(result["ACH_B"].Unlocked);

            var list = AchievementReaders.ParseGoldbergJson("[{\"name\": \"ACH_C\", \"earned\": 1, \"earned_time\": 1700000000000}]");
            Assert.IsTrue(list["ACH_C"].Unlocked);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, list["ACH_C"].UnlockTime, "Milliseconds are converted.");
        }

        [Test]
        public void FindsAppIdBelowRootTest()
        {
            var root = @"C:\Users\12345\AppData\Roaming\GSE Saves";
            Assert.AreEqual("4231820", AchievementReaders.GetAppId(root, root + @"\4231820\achievements.json"));
            Assert.AreEqual("1245620", AchievementReaders.GetAppId(@"C:\Users\Public\Documents\Steam", @"C:\Users\Public\Documents\Steam\RUNE\1245620\achievements.ini"));
            Assert.IsNull(AchievementReaders.GetAppId(root, @"D:\Other\1\achievements.json"));
            Assert.AreEqual(AchievementSource.Rune, AchievementReaders.GetSource(@"C:\Users\Public\Documents\Steam\RUNE\1\achievements.ini"));
            Assert.AreEqual("218620", AchievementReaders.GetAppIdFromSteamStatsFile(@"D:\steam\appcache\stats\UserGameStats_234193947_218620.bin"));
            Assert.IsNull(AchievementReaders.GetAppIdFromSteamStatsFile(@"D:\steam\appcache\stats\UserGameStatsSchema_218620.bin"));
        }

        #region Binary KeyValues

        private class KvWriter
        {
            private readonly MemoryStream stream = new MemoryStream();

            private void Name(byte type, string name)
            {
                stream.WriteByte(type);
                var bytes = Encoding.UTF8.GetBytes(name);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte(0);
            }

            public KvWriter Begin(string name)
            {
                Name(0, name);
                return this;
            }

            public KvWriter End()
            {
                stream.WriteByte(8);
                return this;
            }

            public KvWriter String(string name, string value)
            {
                Name(1, name);
                var bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte(0);
                return this;
            }

            public KvWriter Int(string name, int value)
            {
                Name(2, name);
                stream.Write(BitConverter.GetBytes(value), 0, 4);
                return this;
            }

            public byte[] ToArray()
            {
                return stream.ToArray();
            }
        }

        public static byte[] CreateSteamSchema(string appId)
        {
            return new KvWriter()
                .Begin(appId).Begin("stats")
                    .Begin("1").String("type", "ACHIEVEMENTS").Begin("bits")
                        .Begin("0").String("name", "ACH_FIRST").Begin("display")
                            .Begin("name").String("english", "First").String("spanish", "Primero").End()
                            .Begin("desc").String("english", "Do it once").End()
                            .Int("hidden", 0).String("icon", "first.jpg").String("icon_gray", "first_gray.jpg")
                        .End().End()
                        .Begin("1").String("name", "ACH_SECOND").Begin("display")
                            .String("name", "Second").Int("hidden", 1)
                        .End().End()
                        .Begin("31").String("name", "ACH_LAST").Begin("display").String("name", "Last").End().End()
                    .End().End()
                    .Begin("2").String("type", "INT").String("name", "kills").End()
                .End().End()
                .End()
                .ToArray();
        }

        public static byte[] CreateSteamStats(int data, int unlockTime)
        {
            return new KvWriter()
                .Begin("cache").Int("crc", 1).Int("PendingChanges", 0)
                    .Begin("1").Int("data", data).Begin("AchievementTimes").Int("0", unlockTime).Int("31", unlockTime).End().End()
                    .Begin("2").Int("data", 50).End()
                .End()
                .End()
                .ToArray();
        }

        [Test]
        public void ParsesSteamClientStatsTest()
        {
            var schema = BinaryKeyValues.Read(CreateSteamSchema("480"));
            // Bit 31 makes the stat negative.
            var stats = BinaryKeyValues.Read(CreateSteamStats(unchecked((int)0x80000001), 1700000000));
            var progress = AchievementReaders.ParseSteamStats(stats, schema);
            Assert.AreEqual(3, progress.Count);
            Assert.IsTrue(progress["ACH_FIRST"].Unlocked);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, progress["ACH_FIRST"].UnlockTime);
            Assert.IsFalse(progress["ACH_SECOND"].Unlocked);
            Assert.IsTrue(progress["ACH_LAST"].Unlocked);

            var definitions = AchievementReaders.ParseSteamSchema(schema, "spanish");
            Assert.AreEqual(new[] { "ACH_FIRST", "ACH_SECOND", "ACH_LAST" }, definitions.Select(a => a.ApiName).ToArray());
            Assert.AreEqual("Primero", definitions[0].Name);
            Assert.AreEqual("Do it once", definitions[0].Description, "Falls back to English.");
            Assert.AreEqual("first.jpg", definitions[0].Icon);
            Assert.AreEqual("Second", definitions[1].Name);
            Assert.IsTrue(definitions[1].Hidden);
        }

        #endregion Binary KeyValues

        [Test]
        public void CreatesWaveSoundTest()
        {
            var wave = AchievementSound.CreateChime();
            Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
            Assert.AreEqual("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
            Assert.AreEqual(wave.Length - 8, BitConverter.ToInt32(wave, 4));
        }
    }

    [TestFixture]
    public class AchievementServiceTests
    {
        private const string appId = "4231820";
        private string root;
        private string originalPublic;
        private InMemoryGameDatabase database;
        private GameroomDatabase gameroomDb;
        private AchievementService service;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            // Emulator folders under %PUBLIC% point to the test folder.
            originalPublic = Environment.GetEnvironmentVariable("PUBLIC");
            Environment.SetEnvironmentVariable("PUBLIC", Path.Combine(root, "Public"));
            database = new InMemoryGameDatabase();
            gameroomDb = new GameroomDatabase(root);
            service = new AchievementService(database, gameroomDb, root, () => "en_US");

            // Cached schema, so nothing is downloaded.
            var schema = new AchievementSchema
            {
                AppId = appId,
                Language = "english",
                Updated = DateTime.Now,
                Achievements = new List<AchievementDefinition>
                {
                    new AchievementDefinition { ApiName = "1", Name = "Blood tells all", GlobalPercent = 91 },
                    new AchievementDefinition { ApiName = "2", Name = "Is it burning?", GlobalPercent = 73.3 },
                    new AchievementDefinition { ApiName = "3", Name = "Serpents", Hidden = true, GlobalPercent = 4.5 }
                }
            };
            var schemaFile = Path.Combine(root, "achievements", "schemas", $"{appId}_english.json");
            Directory.CreateDirectory(Path.GetDirectoryName(schemaFile));
            File.WriteAllText(schemaFile, Serialization.ToJson(schema));
        }

        [TearDown]
        public void TearDown()
        {
            service.Dispose();
            gameroomDb.Dispose();
            Environment.SetEnvironmentVariable("PUBLIC", originalPublic);
            Directory.Delete(root, true);
        }

        private string RuneFile => Path.Combine(root, "Public", "Documents", "Steam", "RUNE", appId, "achievements.ini");

        private void WriteRune(params (string Name, long Time)[] unlocked)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RuneFile));
            var text = new StringBuilder();
            foreach (var (name, time) in unlocked)
            {
                text.Append($"[{name}]\r\nAchieved=1\r\nCurProgress=0\r\nMaxProgress=0\r\nUnlockTime={time}\r\n\r\n");
            }

            text.Append($"[SteamAchievements]\r\nCount={unlocked.Length}\r\n");
            File.WriteAllText(RuneFile, text.ToString());
        }

        private Game AddGame()
        {
            var game = new Game("Castlevania Belmont's Curse");
            database.Games.Add(game);
            gameroomDb.SaveGameData(new GameroomGameData { Id = game.Id, SteamAppId = appId });
            return game;
        }

        [Test]
        public void MergesSchemaWithEmulatorUnlocksTest()
        {
            var game = AddGame();
            WriteRune(("1", 1792384953));
            var achievements = service.GetGameAchievements(game, false);
            Assert.AreEqual(appId, achievements.AppId);
            Assert.AreEqual(3, achievements.Total);
            Assert.AreEqual(1, achievements.UnlockedCount);
            Assert.IsTrue(achievements.Items.Single(a => a.Definition.ApiName == "1").Unlocked);
            CollectionAssert.AreEqual(new[] { AchievementSource.Rune }, achievements.Sources);

            var recent = service.GetRecentAchievements(5, out var total);
            Assert.AreEqual(1, total);
            Assert.AreEqual("Blood tells all", recent.Single().Achievement.Definition.Name);
            Assert.AreEqual(game.Id, recent.Single().GameId);
        }

        [Test]
        public void ReportsNewUnlocksWhilePlayingTest()
        {
            var game = AddGame();
            // An old unlock from before Gameroom started.
            WriteRune(("1", 1700000000));
            var unlocked = new List<AchievementsUnlockedEventArgs>();
            service.AchievementsUnlocked += (_, e) =>
            {
                lock (unlocked)
                {
                    unlocked.Add(e);
                }
            };

            service.Start();
            service.OnSessionStarted(game);
            Thread.Sleep(1500);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteRune(("1", 1700000000), ("3", now));

            var deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline)
            {
                lock (unlocked)
                {
                    if (unlocked.Count > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(100);
            }

            lock (unlocked)
            {
                Assert.AreEqual(1, unlocked.Count, "Reported once although the watcher and the session poll both see the change.");
                Assert.AreEqual(appId, unlocked[0].AppId);
                Assert.AreEqual(AchievementSource.Rune, unlocked[0].Source);
                Assert.AreEqual("3", unlocked[0].Unlocked.Single().ApiName);
            }

            // Restoring a backup with old unlocks doesn't announce them.
            File.Delete(RuneFile);
            WriteRune(("2", 1700000100));
            Thread.Sleep(2500);
            lock (unlocked)
            {
                Assert.AreEqual(1, unlocked.Count);
            }
        }
    }
}
