using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class WeekSummaryTests
    {
        private static readonly Guid game = new Guid("00000000-0000-0000-0000-000000000001");
        private static readonly Guid copy = new Guid("00000000-0000-0000-0000-000000000002");
        private static readonly Guid other = new Guid("00000000-0000-0000-0000-000000000003");

        // Sunday, 8 November 2026, in the evening.
        private static readonly DateTime now = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Local);

        private static PlaySession Session(Guid id, DateTime localStart, double minutes, double? playedMinutes = null)
        {
            return new PlaySession
            {
                GameId = id,
                Start = localStart.ToUniversalTime(),
                End = localStart.AddMinutes(minutes).ToUniversalTime(),
                Seconds = (playedMinutes ?? minutes) * 60
            };
        }

        [Test]
        public void WeekStartTest()
        {
            Assert.AreEqual(new DateTime(2026, 11, 2), WeekSummary.GetWeekStart(now, DayOfWeek.Monday));
            Assert.AreEqual(new DateTime(2026, 11, 8), WeekSummary.GetWeekStart(now, DayOfWeek.Sunday));
            Assert.AreEqual(new DateTime(2026, 11, 2), WeekSummary.GetWeekStart(new DateTime(2026, 11, 2, 0, 0, 1), DayOfWeek.Monday));
        }

        [Test]
        public void DaysAndGamesTest()
        {
            var sessions = new List<PlaySession>
            {
                Session(game, new DateTime(2026, 11, 2, 18, 0, 0), 120),
                Session(other, new DateTime(2026, 11, 4, 20, 0, 0), 30),
                Session(game, new DateTime(2026, 11, 8, 10, 0, 0), 60),

                // Before the week: only for the comparison.
                Session(other, new DateTime(2026, 10, 30, 10, 0, 0), 600)
            };

            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 1, 1), null);
            Assert.AreEqual(7, summary.Days.Count);
            Assert.AreEqual(120 * 60, summary.Days[0].Seconds, 0.1);
            Assert.AreEqual(30 * 60, summary.Days[2].Seconds, 0.1);
            Assert.AreEqual(60 * 60, summary.Days[6].Seconds, 0.1);
            Assert.IsTrue(summary.Days[6].IsToday);
            Assert.IsFalse(summary.Days.Any(a => a.IsFuture));
            Assert.AreEqual(210 * 60, summary.TotalSeconds, 0.1);
            Assert.AreEqual(new[] { game, other }, summary.Games.Select(a => a.GameId).ToArray());
            Assert.AreEqual(180 * 60, summary.Games[0].Seconds, 0.1);
            Assert.AreEqual(600 * 60, summary.PreviousWeekSeconds.Value, 0.1);
        }

        [Test]
        public void MidnightIsSharedTest()
        {
            // From Wednesday 23:00 to Thursday 01:00, played the whole time.
            var sessions = new List<PlaySession> { Session(game, new DateTime(2026, 11, 4, 23, 0, 0), 120) };
            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 1, 1), null);
            Assert.AreEqual(3600, summary.Days[2].Seconds, 0.1);
            Assert.AreEqual(3600, summary.Days[3].Seconds, 0.1);
        }

        [Test]
        public void TimeInFrontIsSharedByLengthTest()
        {
            // Two hours open on each side of midnight, one hour in front: half an hour for each day.
            var sessions = new List<PlaySession> { Session(game, new DateTime(2026, 11, 4, 23, 0, 0), 120, 60) };
            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 1, 1), null);
            Assert.AreEqual(1800, summary.Days[2].Seconds, 0.1);
            Assert.AreEqual(1800, summary.Days[3].Seconds, 0.1);
            Assert.AreEqual(3600, summary.TotalSeconds, 0.1);
        }

        [Test]
        public void SessionBeforeTheWeekIsCutTest()
        {
            // Sunday 23:00 of last week to Monday 01:00.
            var sessions = new List<PlaySession> { Session(game, new DateTime(2026, 11, 1, 23, 0, 0), 120) };
            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 1, 1), null);
            Assert.AreEqual(3600, summary.TotalSeconds, 0.1);
            Assert.AreEqual(3600, summary.PreviousWeekSeconds.Value, 0.1);
        }

        [Test]
        public void CopiesAddUpTest()
        {
            var sessions = new List<PlaySession>
            {
                Session(game, new DateTime(2026, 11, 3, 18, 0, 0), 30),
                Session(copy, new DateTime(2026, 11, 5, 18, 0, 0), 40),
                Session(other, new DateTime(2026, 11, 6, 18, 0, 0), 60)
            };

            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 1, 1), a => a == copy ? game : a);
            Assert.AreEqual(2, summary.Games.Count);
            Assert.AreEqual(game, summary.Games[0].GameId);
            Assert.AreEqual(70 * 60, summary.Games[0].Seconds, 0.1);
        }

        [Test]
        public void NoComparisonWithoutHistoryTest()
        {
            var sessions = new List<PlaySession> { Session(game, new DateTime(2026, 11, 3, 18, 0, 0), 30) };
            var summary = WeekSummary.Build(sessions, now, DayOfWeek.Monday, new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc), null);
            Assert.IsNull(summary.PreviousWeekSeconds, "The history started this week: last week is unknown, not zero.");
        }

        [Test]
        public void FutureDaysTest()
        {
            var wednesday = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Local);
            var summary = WeekSummary.Build(new List<PlaySession>(), wednesday, DayOfWeek.Monday, new DateTime(2026, 1, 1), null);
            Assert.AreEqual(new[] { false, false, false, true, true, true, true }, summary.Days.Select(a => a.IsFuture).ToArray());
            Assert.IsTrue(summary.IsEmpty);
        }

        [Test]
        public void HistoryFileTest()
        {
            var folder = Path.Combine(Path.GetTempPath(), "atrio-historial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var previous = PlayHistory.FilePath;
            try
            {
                PlayHistory.FilePath = Path.Combine(folder, PlayHistory.FileName);
                var since = PlayHistory.Since;
                Assert.That(since, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));

                var end = DateTime.UtcNow;
                PlayHistory.Record(game, end.AddMinutes(-30), end, 1800, false);
                PlayHistory.Record(other, end.AddMinutes(-10), end, 300, true);
                PlayHistory.Record(game, end, end.AddMinutes(-1), 60, false);
                PlayHistory.SetOpenSittings(new List<OpenSitting> { new OpenSitting { GameId = other, PendingSeconds = 12, TotalSeconds = 70 } });

                // Read again from the file.
                PlayHistory.FilePath = Path.Combine(folder, PlayHistory.FileName);
                var sessions = PlayHistory.GetSessions(end.AddHours(-1), end.AddHours(1));
                Assert.AreEqual(2, sessions.Count, "A sitting that ends before it starts isn't kept.");
                Assert.AreEqual(game, sessions[0].GameId);
                Assert.IsTrue(sessions[1].Outside);
                Assert.AreEqual(DateTimeKind.Utc, sessions[0].Start.Kind);
                Assert.AreEqual(1, PlayHistory.GetOpenSittings().Count);
                Assert.AreEqual(70, PlayHistory.GetOpenSittings()[0].TotalSeconds);
                Assert.That(PlayHistory.Since, Is.EqualTo(since).Within(TimeSpan.FromSeconds(1)));
                Assert.IsEmpty(PlayHistory.GetSessions(end.AddDays(-3), end.AddDays(-2)));

                var old = new PlayHistoryData();
                old.Sessions.Add(new PlaySession { GameId = game, Start = end.AddDays(-500), End = end.AddDays(-500).AddHours(1), Seconds = 3600 });
                old.Sessions.Add(new PlaySession { GameId = game, Start = end.AddDays(-5), End = end.AddDays(-5).AddHours(1), Seconds = 3600 });
                PlayHistory.Prune(old, end);
                Assert.AreEqual(1, old.Sessions.Count);
            }
            finally
            {
                PlayHistory.FilePath = previous;
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void LightWhilePlayingTest()
        {
            DateTime? hiddenFrom = null;
            DateTime? last = null;
            var t = new DateTime(2026, 11, 8, 20, 0, 0);
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, true, true, t, ref hiddenFrom, ref last), "It waits a bit first.");
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, true, true, t.AddSeconds(20), ref hiddenFrom, ref last));
            Assert.IsTrue(LightWhilePlaying.ShouldLighten(true, true, true, t.AddSeconds(30), ref hiddenFrom, ref last));
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, true, true, t.AddMinutes(5), ref hiddenFrom, ref last), "Once, then every ten minutes.");
            Assert.IsTrue(LightWhilePlaying.ShouldLighten(true, true, true, t.AddSeconds(30).AddMinutes(10), ref hiddenFrom, ref last));

            // Shown again: it starts over.
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, true, false, t.AddMinutes(11), ref hiddenFrom, ref last));
            Assert.IsNull(hiddenFrom);
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, true, true, t.AddMinutes(12), ref hiddenFrom, ref last));
            Assert.IsTrue(LightWhilePlaying.ShouldLighten(true, true, true, t.AddMinutes(12).AddSeconds(30), ref hiddenFrom, ref last));

            // Without a game, or turned off, never.
            hiddenFrom = null;
            last = null;
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, false, true, t, ref hiddenFrom, ref last));
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(true, false, true, t.AddMinutes(1), ref hiddenFrom, ref last));
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(false, true, true, t.AddMinutes(2), ref hiddenFrom, ref last));
            Assert.IsFalse(LightWhilePlaying.ShouldLighten(false, true, true, t.AddMinutes(3), ref hiddenFrom, ref last));
        }
    }
}
