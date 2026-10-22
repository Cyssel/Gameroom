using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Gameroom
{
    public enum PlayStatisticsPeriod
    {
        Last7Days,
        Last30Days,
        ThisYear,
        All
    }

    public enum PlayStatisticsGrouping
    {
        Day,
        Week,
        Month
    }

    /// <summary>
    /// Play time inside one bar of the statistics chart.
    /// </summary>
    public class PlayTimeBucket
    {
        public DateTime Start { get; set; }
        /// <summary>Exclusive.</summary>
        public DateTime End { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>
    /// Play time of one game inside a period.
    /// </summary>
    public class GamePlayTime
    {
        public Guid GameId { get; set; }
        /// <summary>Name from the most recent session of the game.</summary>
        public string GameName { get; set; }
        public double Seconds { get; set; }
        public int SessionCount { get; set; }
        /// <summary>Share of the period's play time, 0 to 100.</summary>
        public double Percentage { get; set; }
    }

    public class PlayStatisticsResult
    {
        public DateTime From { get; set; }
        /// <summary>Exclusive.</summary>
        public DateTime To { get; set; }
        public PlayStatisticsGrouping Grouping { get; set; }
        public List<PlayTimeBucket> Buckets { get; set; }
        public List<GamePlayTime> TopGames { get; set; }
        public double TotalSeconds { get; set; }
        public int SessionCount { get; set; }
        public int GameCount { get; set; }
        public int DaysPlayed { get; set; }
    }

    /// <summary>
    /// Aggregates recorded play sessions for the play statistics view.
    /// Sessions spanning several days (or weeks, months) are split between them
    /// proportionally to the time spent in each one.
    /// </summary>
    public static class PlayStatistics
    {
        public static PlayStatisticsResult Calculate(
            IEnumerable<GameSession> sessions,
            PlayStatisticsPeriod period,
            DateTime now,
            DayOfWeek firstDayOfWeek = DayOfWeek.Monday,
            int topCount = 10)
        {
            var list = sessions.ToList();
            GetRange(period, now, list, out var from, out var to);
            return Calculate(list, from, to, GetGrouping(from, to), firstDayOfWeek, topCount);
        }

        public static PlayStatisticsResult Calculate(
            IEnumerable<GameSession> sessions,
            DateTime from,
            DateTime to,
            PlayStatisticsGrouping grouping,
            DayOfWeek firstDayOfWeek = DayOfWeek.Monday,
            int topCount = 10)
        {
            var inRange = sessions.Where(a => SecondsWithin(a, from, to) > 0).ToList();
            var games = GetGamePlayTime(inRange, from, to);
            return new PlayStatisticsResult
            {
                From = from,
                To = to,
                Grouping = grouping,
                Buckets = GroupPlayTime(inRange, from, to, grouping, firstDayOfWeek),
                TopGames = games.Take(topCount).ToList(),
                TotalSeconds = games.Sum(a => a.Seconds),
                SessionCount = inRange.Count,
                GameCount = games.Count,
                DaysPlayed = GroupPlayTime(inRange, from, to, PlayStatisticsGrouping.Day, firstDayOfWeek).Count(a => a.Seconds > 0)
            };
        }

        /// <summary>
        /// Range of whole days covered by a period, ending with today.
        /// </summary>
        public static void GetRange(PlayStatisticsPeriod period, DateTime now, IEnumerable<GameSession> sessions, out DateTime from, out DateTime to)
        {
            var today = now.Date;
            to = today.AddDays(1);
            switch (period)
            {
                case PlayStatisticsPeriod.Last7Days:
                    from = today.AddDays(-6);
                    break;
                case PlayStatisticsPeriod.Last30Days:
                    from = today.AddDays(-29);
                    break;
                case PlayStatisticsPeriod.ThisYear:
                    from = new DateTime(today.Year, 1, 1);
                    break;
                default:
                    var played = sessions.Where(a => a.Duration > 0).ToList();
                    from = played.Count > 0 ? played.Min(a => a.Start).Date : today;
                    if (from > today)
                    {
                        from = today;
                    }
                    break;
            }
        }

        /// <summary>
        /// Bars by day for up to a month, by week for up to a year and by month for longer ranges.
        /// </summary>
        public static PlayStatisticsGrouping GetGrouping(DateTime from, DateTime to)
        {
            var days = (to - from).TotalDays;
            if (days <= 31)
            {
                return PlayStatisticsGrouping.Day;
            }

            if (days <= 366)
            {
                return PlayStatisticsGrouping.Week;
            }

            return PlayStatisticsGrouping.Month;
        }

        /// <summary>
        /// Play time per day, week or month. Returns a bucket for every unit of the range, including empty ones.
        /// First and last buckets are clipped to the range.
        /// </summary>
        public static List<PlayTimeBucket> GroupPlayTime(
            IEnumerable<GameSession> sessions,
            DateTime from,
            DateTime to,
            PlayStatisticsGrouping grouping,
            DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
        {
            var buckets = new List<PlayTimeBucket>();
            if (to <= from)
            {
                return buckets;
            }

            var index = new Dictionary<DateTime, PlayTimeBucket>();
            for (var unit = GetUnitStart(from, grouping, firstDayOfWeek); unit < to; unit = GetNextUnit(unit, grouping))
            {
                var bucket = new PlayTimeBucket
                {
                    Start = unit < from ? from : unit,
                    End = GetNextUnit(unit, grouping) > to ? to : GetNextUnit(unit, grouping)
                };

                buckets.Add(bucket);
                index[unit] = bucket;
            }

            foreach (var session in sessions)
            {
                if (session.Duration == 0)
                {
                    continue;
                }

                if (session.End <= session.Start)
                {
                    if (session.Start >= from && session.Start < to)
                    {
                        index[GetUnitStart(session.Start, grouping, firstDayOfWeek)].Seconds += session.Duration;
                    }

                    continue;
                }

                var start = session.Start < from ? from : session.Start;
                var end = session.End > to ? to : session.End;
                for (var unit = GetUnitStart(start, grouping, firstDayOfWeek); unit < end; unit = GetNextUnit(unit, grouping))
                {
                    var seconds = SecondsWithin(session, unit, GetNextUnit(unit, grouping));
                    if (seconds > 0)
                    {
                        index[unit].Seconds += seconds;
                    }
                }
            }

            return buckets;
        }

        /// <summary>
        /// Play time per game inside the range, most played first.
        /// </summary>
        public static List<GamePlayTime> GetGamePlayTime(IEnumerable<GameSession> sessions, DateTime from, DateTime to)
        {
            var games = new List<GamePlayTime>();
            foreach (var group in sessions.GroupBy(a => a.GameId))
            {
                var game = new GamePlayTime
                {
                    GameId = group.Key,
                    GameName = group.OrderByDescending(a => a.Start).First().GameName
                };

                foreach (var session in group)
                {
                    var seconds = SecondsWithin(session, from, to);
                    if (seconds > 0)
                    {
                        game.Seconds += seconds;
                        game.SessionCount++;
                    }
                }

                if (game.Seconds > 0)
                {
                    games.Add(game);
                }
            }

            var total = games.Sum(a => a.Seconds);
            foreach (var game in games)
            {
                game.Percentage = game.Seconds / total * 100;
            }

            return games.
                OrderByDescending(a => a.Seconds).
                ThenBy(a => a.GameName, StringComparer.CurrentCultureIgnoreCase).
                ToList();
        }

        /// <summary>
        /// Part of the session's play time that falls between from and to.
        /// Duration is spread evenly over the session's start and end time.
        /// </summary>
        public static double SecondsWithin(GameSession session, DateTime from, DateTime to)
        {
            if (session.Duration == 0)
            {
                return 0;
            }

            if (session.End <= session.Start)
            {
                return session.Start >= from && session.Start < to ? session.Duration : 0;
            }

            var start = session.Start < from ? from : session.Start;
            var end = session.End > to ? to : session.End;
            if (end <= start)
            {
                return 0;
            }

            return session.Duration * ((double)(end - start).Ticks / (session.End - session.Start).Ticks);
        }

        public static DateTime GetUnitStart(DateTime date, PlayStatisticsGrouping grouping, DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
        {
            switch (grouping)
            {
                case PlayStatisticsGrouping.Week:
                    return date.Date.AddDays(-((7 + (date.DayOfWeek - firstDayOfWeek)) % 7));
                case PlayStatisticsGrouping.Month:
                    return new DateTime(date.Year, date.Month, 1);
                default:
                    return date.Date;
            }
        }

        private static DateTime GetNextUnit(DateTime unitStart, PlayStatisticsGrouping grouping)
        {
            switch (grouping)
            {
                case PlayStatisticsGrouping.Week:
                    return unitStart.AddDays(7);
                case PlayStatisticsGrouping.Month:
                    return unitStart.AddMonths(1);
                default:
                    return unitStart.AddDays(1);
            }
        }
    }
}
