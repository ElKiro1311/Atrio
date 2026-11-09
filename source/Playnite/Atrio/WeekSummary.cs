using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Atrio
{
    public class WeekDayTotal
    {
        /// <summary>Midnight of that day, local time.</summary>
        public DateTime Date { get; set; }
        public double Seconds { get; set; }
        public bool IsToday { get; set; }
        public bool IsFuture { get; set; }
    }

    public class WeekGameTotal
    {
        /// <summary>The game as the library shows it: the main copy when there are several.</summary>
        public Guid GameId { get; set; }
        public double Seconds { get; set; }
    }

    public class WeekSummaryData
    {
        public DateTime WeekStart { get; set; }
        public List<WeekDayTotal> Days { get; set; } = new List<WeekDayTotal>();
        public List<WeekGameTotal> Games { get; set; } = new List<WeekGameTotal>();
        public double TotalSeconds { get; set; }

        /// <summary>Null when the history doesn't reach the whole week before.</summary>
        public double? PreviousWeekSeconds { get; set; }

        public bool IsEmpty => TotalSeconds < 1;
    }

    /// <summary>What was played this week, by day and by game, from the history of sittings.</summary>
    public static class WeekSummary
    {
        public static DateTime GetWeekStart(DateTime nowLocal, DayOfWeek firstDay)
        {
            var days = ((int)nowLocal.DayOfWeek - (int)firstDay + 7) % 7;
            return nowLocal.Date.AddDays(-days);
        }

        /// <summary>
        /// The played time of a sitting that falls between two moments. A sitting that crosses midnight shares
        /// its time by how long it lasted on each side.
        /// </summary>
        public static double GetSecondsBetween(PlaySession session, DateTime fromLocal, DateTime toLocal)
        {
            // The days start at local midnight, but the time is counted in universal time: the night the clock
            // goes back an hour, a sitting doesn't end before it started.
            var start = PlayHistory.ToUtc(session.Start);
            var end = PlayHistory.ToUtc(session.End);
            var from = fromLocal.Kind == DateTimeKind.Utc ? fromLocal : fromLocal.ToUniversalTime();
            var to = toLocal.Kind == DateTimeKind.Utc ? toLocal : toLocal.ToUniversalTime();
            if (end < start)
            {
                return 0;
            }

            var wall = (end - start).TotalSeconds;
            if (wall <= 0)
            {
                return start >= from && start < to ? session.Seconds : 0;
            }

            var overlapStart = start > from ? start : from;
            var overlapEnd = end < to ? end : to;
            if (overlapEnd <= overlapStart)
            {
                return 0;
            }

            return session.Seconds * (overlapEnd - overlapStart).TotalSeconds / wall;
        }

        /// <param name="groupOf">The game the library shows for a copy; copies of one game add up together.</param>
        public static WeekSummaryData Build(
            IEnumerable<PlaySession> sessions,
            DateTime nowLocal,
            DayOfWeek firstDay,
            DateTime historySinceUtc,
            Func<Guid, Guid> groupOf)
        {
            var list = sessions?.Where(a => a != null && a.Seconds > 0).ToList() ?? new List<PlaySession>();
            var weekStart = GetWeekStart(nowLocal, firstDay);
            var weekEnd = weekStart.AddDays(7);
            var result = new WeekSummaryData { WeekStart = weekStart };
            for (var i = 0; i < 7; i++)
            {
                var day = weekStart.AddDays(i);
                result.Days.Add(new WeekDayTotal
                {
                    Date = day,
                    Seconds = list.Sum(a => GetSecondsBetween(a, day, day.AddDays(1))),
                    IsToday = day == nowLocal.Date,
                    IsFuture = day > nowLocal.Date
                });
            }

            var games = new Dictionary<Guid, double>();
            foreach (var session in list)
            {
                var seconds = GetSecondsBetween(session, weekStart, weekEnd);
                if (seconds <= 0)
                {
                    continue;
                }

                var id = groupOf?.Invoke(session.GameId) ?? session.GameId;
                if (id == Guid.Empty)
                {
                    id = session.GameId;
                }

                games.TryGetValue(id, out var current);
                games[id] = current + seconds;
            }

            result.Games = games.
                Where(a => a.Value >= 1).
                OrderByDescending(a => a.Value).
                ThenBy(a => a.Key).
                Select(a => new WeekGameTotal { GameId = a.Key, Seconds = a.Value }).
                ToList();
            result.TotalSeconds = result.Games.Sum(a => a.Seconds);

            var previousStart = weekStart.AddDays(-7);
            if (historySinceUtc <= previousStart.ToUniversalTime())
            {
                result.PreviousWeekSeconds = list.Sum(a => GetSecondsBetween(a, previousStart, weekStart));
            }

            return result;
        }
    }
}
