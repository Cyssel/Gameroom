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
    public class GameActivityMonitorTests
    {
        private const string installDir = @"D:\Library\Hollow Knight";
        private const string exePath = installDir + @"\hollow_knight.exe";

        private string root;
        private InMemoryGameDatabase database;
        private GameroomDatabase gameroomDb;
        private GameroomSettings settings;
        private List<ProcessSnapshot> processes;
        private Game game;
        private DateTime t0;

        [SetUp]
        public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "GameroomTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            database = new InMemoryGameDatabase();
            gameroomDb = new GameroomDatabase(root);
            settings = new GameroomSettings { MinimumSessionLength = 60, DetectUnknownGames = false };
            processes = new List<ProcessSnapshot>();
            game = new Game("Hollow Knight") { InstallDirectory = installDir, IsInstalled = true };
            database.Games.Add(game);
            t0 = new DateTime(2026, 1, 10, 20, 0, 0);
        }

        [TearDown]
        public void TearDown()
        {
            gameroomDb.Dispose();
            Directory.Delete(root, true);
        }

        private GameActivityMonitor CreateMonitor(Func<int, DateTime, bool> isRunning = null)
        {
            return new GameActivityMonitor(database, gameroomDb, () => settings, () => processes.ToList(), isRunning ?? ((a, b) => false));
        }

        private void StartGameProcess(DateTime start)
        {
            processes.Add(new ProcessSnapshot { Id = 100, Path = exePath, StartTime = start, HasWindow = true });
        }

        [Test]
        public void RecordsDetectedSessionTest()
        {
            var monitor = CreateMonitor();
            var started = 0;
            var ended = new List<GameSession>();
            monitor.SessionStarted += (_, __) => started++;
            monitor.SessionEnded += (_, s) => ended.Add(s);

            StartGameProcess(t0);
            monitor.Tick(t0.AddSeconds(5));
            Assert.AreEqual(0, started, "Session is confirmed only after running for a while.");
            Assert.IsFalse(database.Games.Get(game.Id).IsRunning);

            monitor.Tick(t0.AddSeconds(15));
            Assert.AreEqual(1, started);
            Assert.IsTrue(database.Games.Get(game.Id).IsRunning);

            monitor.Tick(t0.AddMinutes(30));
            processes.Clear();
            monitor.Tick(t0.AddMinutes(30).AddSeconds(5));
            Assert.IsEmpty(ended, "Short gaps are tolerated, launchers can restart the game.");

            monitor.Tick(t0.AddMinutes(30).AddSeconds(20));
            Assert.AreEqual(1, ended.Count);
            var dbGame = database.Games.Get(game.Id);
            Assert.IsFalse(dbGame.IsRunning);
            Assert.AreEqual(30 * 60, dbGame.Playtime);
            Assert.AreEqual(1, dbGame.PlayCount);
            Assert.AreEqual(t0, dbGame.LastActivity);

            var session = gameroomDb.GetSessions(game.Id).Single();
            Assert.AreEqual(SessionSource.Detected, session.Source);
            Assert.AreEqual((ulong)(30 * 60), session.Duration);
            Assert.AreEqual(exePath, session.Executable);
            Assert.IsEmpty(gameroomDb.GetActiveSessions());
            CollectionAssert.Contains(gameroomDb.GetGameData(game.Id).KnownExecutables, exePath);
        }

        [Test]
        public void PlaytimeGrowsWhilePlayingTest()
        {
            var monitor = CreateMonitor();
            StartGameProcess(t0);
            monitor.Tick(t0.AddSeconds(15));
            Assert.AreEqual(0, database.Games.Get(game.Id).Playtime, "Nothing is added before the minimum session length.");

            monitor.Tick(t0.AddMinutes(2));
            Assert.AreEqual(120, database.Games.Get(game.Id).Playtime);
            Assert.AreEqual(1, database.Games.Get(game.Id).PlayCount);

            monitor.Tick(t0.AddMinutes(2).AddSeconds(30));
            Assert.AreEqual(120, database.Games.Get(game.Id).Playtime, "Playtime is updated once a minute.");

            monitor.Tick(t0.AddMinutes(10));
            Assert.AreEqual(600, database.Games.Get(game.Id).Playtime);
            Assert.AreEqual((ulong)600, gameroomDb.GetActiveSessions().Single().CreditedSeconds);

            monitor.Tick(t0.AddMinutes(10).AddSeconds(40));
            processes.Clear();
            monitor.Tick(t0.AddMinutes(11));

            var dbGame = database.Games.Get(game.Id);
            Assert.AreEqual(640, dbGame.Playtime, "Only the rest is added when the session ends.");
            Assert.AreEqual(1, dbGame.PlayCount);
            Assert.AreEqual((ulong)640, gameroomDb.GetSessions(game.Id).Single().Duration);
        }

        [Test]
        public void RecoveryDoesNotCountCreditedTimeTwiceTest()
        {
            gameroomDb.SaveActiveSession(new ActiveSession
            {
                Id = Guid.NewGuid(),
                GameId = game.Id,
                Start = t0,
                LastSeen = t0.AddMinutes(10),
                Executable = exePath,
                ProcessId = 100,
                ProcessStart = t0,
                CreditedSeconds = 540
            });

            game.Playtime = 540;
            game.PlayCount = 1;
            var monitor = CreateMonitor((pid, start) => false);
            monitor.RecoverActiveSessions();
            Assert.AreEqual(600, database.Games.Get(game.Id).Playtime);
            Assert.AreEqual(1, database.Games.Get(game.Id).PlayCount);
        }

        [Test]
        public void IgnoresShortSessionsTest()
        {
            var monitor = CreateMonitor();
            StartGameProcess(t0);
            monitor.Tick(t0.AddSeconds(15));
            monitor.Tick(t0.AddSeconds(40));
            processes.Clear();
            monitor.Tick(t0.AddSeconds(60));

            var dbGame = database.Games.Get(game.Id);
            Assert.AreEqual(0, dbGame.Playtime);
            Assert.IsFalse(dbGame.IsRunning);
            Assert.IsEmpty(gameroomDb.GetSessions(game.Id));
        }

        [Test]
        public void LeavesLaunchedGamesToPlayControllerTest()
        {
            var monitor = CreateMonitor();
            game.IsRunning = true;
            StartGameProcess(t0);
            monitor.Tick(t0.AddSeconds(100));
            Assert.IsEmpty(monitor.GetTrackedSessions());

            // Play controller stopped tracking while a game process kept running,
            // only the time after that is counted.
            game.IsRunning = false;
            monitor.Tick(t0.AddSeconds(200));
            monitor.Tick(t0.AddSeconds(500));
            processes.Clear();
            monitor.Tick(t0.AddSeconds(520));

            Assert.AreEqual(400, database.Games.Get(game.Id).Playtime);
        }

        [Test]
        public void RecoversSessionsAfterRestartTest()
        {
            gameroomDb.SaveActiveSession(new ActiveSession
            {
                Id = Guid.NewGuid(),
                GameId = game.Id,
                Start = t0,
                LastSeen = t0.AddMinutes(10),
                Executable = exePath,
                ProcessId = 100,
                ProcessStart = t0
            });

            // Game exited while Gameroom wasn't running: last persisted time is used.
            var monitor = CreateMonitor((pid, start) => false);
            monitor.RecoverActiveSessions();
            Assert.AreEqual(600, database.Games.Get(game.Id).Playtime);
            Assert.IsEmpty(gameroomDb.GetActiveSessions());
        }

        [Test]
        public void ContinuesRunningSessionAfterRestartTest()
        {
            var id = Guid.NewGuid();
            gameroomDb.SaveActiveSession(new ActiveSession
            {
                Id = id,
                GameId = game.Id,
                Start = t0,
                LastSeen = t0.AddMinutes(10),
                Executable = exePath,
                ProcessId = 100,
                ProcessStart = t0
            });

            var monitor = CreateMonitor((pid, start) => pid == 100);
            monitor.RecoverActiveSessions();
            Assert.AreEqual(0, database.Games.Get(game.Id).Playtime);
            Assert.IsTrue(database.Games.Get(game.Id).IsRunning);
            Assert.AreEqual(t0, monitor.GetTrackedSessions().Single().Start);
        }

        [Test]
        public void StopKeepsRunningSessionsTest()
        {
            var monitor = CreateMonitor();
            StartGameProcess(t0);
            monitor.Tick(t0.AddSeconds(15));
            monitor.Tick(t0.AddMinutes(5));
            Assert.AreEqual(1, monitor.GetTrackedSessions().Count);

            monitor.Stop();
            Assert.IsEmpty(monitor.GetTrackedSessions());
            Assert.AreEqual(1, gameroomDb.GetActiveSessions().Count, "Sessions are kept for the next start.");
            // Time played so far was already added while running, the stored session remembers it.
            Assert.AreEqual(300, database.Games.Get(game.Id).Playtime);
            Assert.AreEqual((ulong)300, gameroomDb.GetActiveSessions().Single().CreditedSeconds);
        }
    }
}
