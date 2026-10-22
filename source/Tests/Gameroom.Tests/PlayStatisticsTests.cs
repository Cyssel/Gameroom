using NUnit.Framework;
using Playnite.Gameroom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Gameroom.Tests
{
    [TestFixture]
    public class PlayStatisticsTests
    {
        private static readonly Guid hollowKnight = Guid.NewGuid();
        private static readonly Guid celeste = Guid.NewGuid();
        private static readonly Guid hades = Guid.NewGuid();

        private static GameSession Session(Guid gameId, string name, DateTime start, TimeSpan length)
        {
            return new GameSession
            {
                GameId = gameId,
                GameName = name,
                Start = start,
                End = start + length,
                Duration = (ulong)length.TotalSeconds,
                Source = SessionSource.Detected
            };
        }

        [Test]
        public void GroupsSessionsByDayTest()
        {
            var from = new DateTime(2026, 3, 1);
            var to = from.AddDays(7);
            var sessions = new List<GameSession>
            {
                Session(hollowKnight, "Hollow Knight", new DateTime(2026, 3, 1, 10, 0, 0), TimeSpan.FromHours(1)),
                Session(celeste, "Celeste", new DateTime(2026, 3, 1, 18, 0, 0), TimeSpan.FromMinutes(30)),
                Session(hollowKnight, "Hollow Knight", new DateTime(2026, 3, 3, 20, 0, 0), TimeSpan.FromHours(2)),
                // Outside of the range.
                Session(hollowKnight, "Hollow Knight", new DateTime(2026, 2, 27, 20, 0, 0), TimeSpan.FromHours(2)),
                Session(hollowKnight, "Hollow Knight", new DateTime(2026, 3, 8, 12, 0, 0), TimeSpan.FromHours(2))
            };

            var buckets = PlayStatistics.GroupPlayTime(sessions, from, to, PlayStatisticsGrouping.Day);
            Assert.AreEqual(7, buckets.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 7).Select(a => from.AddDays(a)), buckets.Select(a => a.Start));
            CollectionAssert.AreEqual(Enumerable.Range(1, 7).Select(a => from.AddDays(a)), buckets.Select(a => a.End));
            CollectionAssert.AreEqual(new double[] { 5400, 0, 7200, 0, 0, 0, 0 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void SplitsSessionCrossingMidnightTest()
        {
            var from = new DateTime(2026, 3, 1);
            var to = from.AddDays(3);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2026, 3, 1, 23, 0, 0), TimeSpan.FromHours(2))
            };

            var buckets = PlayStatistics.GroupPlayTime(sessions, from, to, PlayStatisticsGrouping.Day);
            CollectionAssert.AreEqual(new double[] { 3600, 3600, 0 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void SplitsSessionProportionallyWhenDurationIsShorterThanSpanTest()
        {
            // Session window 22:00 - 02:00 but only 2 hours of play recorded (e.g. game paused while PC slept).
            var session = Session(hades, "Hades", new DateTime(2026, 3, 1, 22, 0, 0), TimeSpan.FromHours(4));
            session.Duration = 7200;

            var buckets = PlayStatistics.GroupPlayTime(new[] { session }, new DateTime(2026, 3, 1), new DateTime(2026, 3, 3), PlayStatisticsGrouping.Day);
            CollectionAssert.AreEqual(new double[] { 3600, 3600 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void ClipsSessionsAtRangeBoundsTest()
        {
            var from = new DateTime(2026, 3, 2);
            var to = new DateTime(2026, 3, 3);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2026, 3, 1, 23, 30, 0), TimeSpan.FromHours(1)),
                Session(celeste, "Celeste", new DateTime(2026, 3, 2, 23, 0, 0), TimeSpan.FromHours(3))
            };

            Assert.AreEqual(1800, PlayStatistics.SecondsWithin(sessions[0], from, to), 0.001);
            Assert.AreEqual(3600, PlayStatistics.SecondsWithin(sessions[1], from, to), 0.001);

            var result = PlayStatistics.Calculate(sessions, from, to, PlayStatisticsGrouping.Day);
            Assert.AreEqual(5400, result.TotalSeconds, 0.001);
            Assert.AreEqual(2, result.SessionCount);
            Assert.AreEqual(1, result.Buckets.Count);
            Assert.AreEqual(5400, result.Buckets[0].Seconds, 0.001);
        }

        [Test]
        public void ZeroLengthIntervalCountsOnStartDayTest()
        {
            var session = new GameSession
            {
                GameId = hades,
                GameName = "Hades",
                Start = new DateTime(2026, 3, 2, 12, 0, 0),
                End = new DateTime(2026, 3, 2, 12, 0, 0),
                Duration = 600
            };

            var buckets = PlayStatistics.GroupPlayTime(new[] { session }, new DateTime(2026, 3, 1), new DateTime(2026, 3, 4), PlayStatisticsGrouping.Day);
            CollectionAssert.AreEqual(new double[] { 0, 600, 0 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void GroupsByWeekTest()
        {
            // 2026-01-01 is a Thursday, weeks start on Monday.
            var from = new DateTime(2026, 1, 1);
            var to = new DateTime(2026, 1, 20);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2026, 1, 2, 10, 0, 0), TimeSpan.FromHours(1)),
                // Sunday night into Monday, split between two weeks.
                Session(hades, "Hades", new DateTime(2026, 1, 11, 23, 0, 0), TimeSpan.FromHours(2)),
                Session(celeste, "Celeste", new DateTime(2026, 1, 19, 10, 0, 0), TimeSpan.FromHours(3))
            };

            var buckets = PlayStatistics.GroupPlayTime(sessions, from, to, PlayStatisticsGrouping.Week, DayOfWeek.Monday);
            CollectionAssert.AreEqual(
                new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 5), new DateTime(2026, 1, 12), new DateTime(2026, 1, 19) },
                buckets.Select(a => a.Start));
            Assert.AreEqual(new DateTime(2026, 1, 5), buckets[0].End);
            Assert.AreEqual(to, buckets.Last().End);
            CollectionAssert.AreEqual(new double[] { 3600, 3600, 3600, 10800 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void GroupsByMonthTest()
        {
            var from = new DateTime(2025, 11, 15);
            var to = new DateTime(2026, 2, 1);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2025, 11, 20, 10, 0, 0), TimeSpan.FromHours(1)),
                Session(hades, "Hades", new DateTime(2025, 12, 31, 23, 30, 0), TimeSpan.FromHours(1)),
                Session(celeste, "Celeste", new DateTime(2026, 1, 10, 10, 0, 0), TimeSpan.FromHours(2))
            };

            var buckets = PlayStatistics.GroupPlayTime(sessions, from, to, PlayStatisticsGrouping.Month);
            CollectionAssert.AreEqual(
                new[] { new DateTime(2025, 11, 15), new DateTime(2025, 12, 1), new DateTime(2026, 1, 1) },
                buckets.Select(a => a.Start));
            CollectionAssert.AreEqual(new double[] { 3600, 1800, 1800 + 7200 }, buckets.Select(a => a.Seconds));
        }

        [Test]
        public void TopGamesTest()
        {
            var from = new DateTime(2026, 3, 1);
            var to = from.AddDays(7);
            var sessions = new List<GameSession>
            {
                Session(celeste, "Celeste", new DateTime(2026, 3, 1, 10, 0, 0), TimeSpan.FromHours(1)),
                Session(hollowKnight, "Hollow Knight", new DateTime(2026, 3, 2, 10, 0, 0), TimeSpan.FromHours(2)),
                Session(hollowKnight, "Hollow Knight (renamed)", new DateTime(2026, 3, 4, 10, 0, 0), TimeSpan.FromHours(4)),
                Session(hades, "Hades", new DateTime(2026, 3, 5, 10, 0, 0), TimeSpan.FromHours(3)),
                // Outside of the range, must not count.
                Session(celeste, "Celeste", new DateTime(2026, 2, 1, 10, 0, 0), TimeSpan.FromHours(50))
            };

            var games = PlayStatistics.GetGamePlayTime(sessions, from, to);
            CollectionAssert.AreEqual(new[] { hollowKnight, hades, celeste }, games.Select(a => a.GameId));
            Assert.AreEqual("Hollow Knight (renamed)", games[0].GameName);
            Assert.AreEqual(6 * 3600, games[0].Seconds, 0.001);
            Assert.AreEqual(2, games[0].SessionCount);
            Assert.AreEqual(60, games[0].Percentage, 0.001);
            Assert.AreEqual(30, games[1].Percentage, 0.001);
            Assert.AreEqual(10, games[2].Percentage, 0.001);
            Assert.AreEqual(1, games[2].SessionCount);

            var result = PlayStatistics.Calculate(sessions, from, to, PlayStatisticsGrouping.Day, topCount: 2);
            CollectionAssert.AreEqual(new[] { hollowKnight, hades }, result.TopGames.Select(a => a.GameId));
            Assert.AreEqual(3, result.GameCount);
            Assert.AreEqual(4, result.SessionCount);
            Assert.AreEqual(4, result.DaysPlayed);
            Assert.AreEqual(10 * 3600, result.TotalSeconds, 0.001);
        }

        [Test]
        public void TopGamesAreLimitedToTenTest()
        {
            var day = new DateTime(2026, 3, 1);
            var sessions = Enumerable.Range(1, 12)
                .Select(a => Session(Guid.NewGuid(), "Game " + a, day.AddHours(a), TimeSpan.FromMinutes(a)))
                .ToList();

            var result = PlayStatistics.Calculate(sessions, day, day.AddDays(1), PlayStatisticsGrouping.Day);
            Assert.AreEqual(10, result.TopGames.Count);
            Assert.AreEqual("Game 12", result.TopGames[0].GameName);
            Assert.AreEqual(12, result.GameCount);
        }

        [Test]
        public void PeriodRangesTest()
        {
            var now = new DateTime(2026, 10, 20, 15, 30, 0);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2024, 5, 6, 22, 0, 0), TimeSpan.FromHours(1)),
                Session(hades, "Hades", new DateTime(2026, 10, 19, 22, 0, 0), TimeSpan.FromHours(1))
            };

            PlayStatistics.GetRange(PlayStatisticsPeriod.Last7Days, now, sessions, out var from, out var to);
            Assert.AreEqual(new DateTime(2026, 10, 14), from);
            Assert.AreEqual(new DateTime(2026, 10, 21), to);

            PlayStatistics.GetRange(PlayStatisticsPeriod.Last30Days, now, sessions, out from, out to);
            Assert.AreEqual(new DateTime(2026, 9, 21), from);
            Assert.AreEqual(30, (to - from).TotalDays);

            PlayStatistics.GetRange(PlayStatisticsPeriod.ThisYear, now, sessions, out from, out to);
            Assert.AreEqual(new DateTime(2026, 1, 1), from);
            Assert.AreEqual(new DateTime(2026, 10, 21), to);

            PlayStatistics.GetRange(PlayStatisticsPeriod.All, now, sessions, out from, out to);
            Assert.AreEqual(new DateTime(2024, 5, 6), from);
            Assert.AreEqual(new DateTime(2026, 10, 21), to);

            PlayStatistics.GetRange(PlayStatisticsPeriod.All, now, new List<GameSession>(), out from, out to);
            Assert.AreEqual(now.Date, from);
        }

        [Test]
        public void GroupingByPeriodTest()
        {
            var now = new DateTime(2026, 10, 20, 15, 30, 0);
            var sessions = new List<GameSession>
            {
                Session(hades, "Hades", new DateTime(2024, 5, 6, 22, 0, 0), TimeSpan.FromHours(1))
            };

            Assert.AreEqual(PlayStatisticsGrouping.Day, PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.Last7Days, now).Grouping);
            Assert.AreEqual(PlayStatisticsGrouping.Day, PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.Last30Days, now).Grouping);
            Assert.AreEqual(PlayStatisticsGrouping.Week, PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.ThisYear, now).Grouping);
            Assert.AreEqual(PlayStatisticsGrouping.Month, PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.All, now).Grouping);

            var last30 = PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.Last30Days, now);
            Assert.AreEqual(30, last30.Buckets.Count);
            Assert.AreEqual(0, last30.TotalSeconds);
            Assert.AreEqual(0, last30.SessionCount);
            Assert.AreEqual(0, last30.TopGames.Count);

            // Early in the year "this year" is short enough to be shown by day.
            Assert.AreEqual(PlayStatisticsGrouping.Day, PlayStatistics.Calculate(sessions, PlayStatisticsPeriod.ThisYear, new DateTime(2026, 1, 15)).Grouping);
        }
    }
}
