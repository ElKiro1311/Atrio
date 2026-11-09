using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class OutsidePlayTests
    {
        private class FakeLedger : IOutsidePlayLedger
        {
            public HashSet<Guid> Tracked = new HashSet<Guid>();
            public List<Guid> Played = new List<Guid>();
            public Dictionary<Guid, ulong> Playtime = new Dictionary<Guid, ulong>();
            public List<PlaySession> Sessions = new List<PlaySession>();
            public List<OpenSitting> Saved;
            public int Saves;

            /// <summary>Programs keep the start time they had when they opened.</summary>
            public Dictionary<int, string> Keys = new Dictionary<int, string>();

            public bool IsTrackedByAtrio(Guid gameId) => Tracked.Contains(gameId);
            public Guid ChooseOne(List<Guid> gameIds) => gameIds.Max();
            public string GetProcessKey(int processId) => Keys.TryGetValue(processId, out var key) ? key : processId + ":1";
            public void MarkPlayed(Guid gameId) => Played.Add(gameId);

            public void AddPlaytime(Guid gameId, ulong seconds)
            {
                Playtime.TryGetValue(gameId, out var current);
                Playtime[gameId] = current + seconds;
            }

            public void Record(Guid gameId, DateTime startUtc, DateTime endUtc, double seconds)
            {
                Sessions.Add(new PlaySession { GameId = gameId, Start = startUtc, End = endUtc, Seconds = seconds, Outside = true });
            }

            public void SaveOpen(List<OpenSitting> open)
            {
                Saved = open;
                Saves++;
            }
        }

        private static readonly Guid game = new Guid("00000000-0000-0000-0000-000000000001");
        private static readonly Guid other = new Guid("00000000-0000-0000-0000-000000000002");
        private static readonly DateTime start = new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc);

        private static Dictionary<Guid, List<int>> Open()
        {
            return new Dictionary<Guid, List<int>>();
        }

        private static Dictionary<Guid, List<int>> Open(Guid id, params int[] processes)
        {
            return new Dictionary<Guid, List<int>> { { id, processes.ToList() } };
        }

        private static Dictionary<Guid, List<int>> Open(Guid id, int[] processes, Guid second, int[] secondProcesses)
        {
            return new Dictionary<Guid, List<int>> { { id, processes.ToList() }, { second, secondProcesses.ToList() } };
        }

        /// <summary>Rounds of three seconds, like the watch of the open games.</summary>
        private static DateTime Play(OutsidePlayTracker tracker, IReadOnlyDictionary<Guid, List<int>> open, int foreground, DateTime from, int seconds)
        {
            var now = from;
            for (var i = 0; i < seconds / 3; i++)
            {
                now = now.AddSeconds(3);
                tracker.Tick(open, foreground, 3, now);
            }

            return now;
        }

        [Test]
        public void GameInFrontIsCountedTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            tracker.Tick(open, 0, 0, start);
            var now = Play(tracker, open, 100, start, 90);
            Assert.AreEqual(1, ledger.Played.Count, "One play, once a minute has passed.");
            Assert.AreEqual(0, ledger.Playtime.Count, "The time goes in when the game closes.");

            tracker.Tick(Open(), 0, 3, now.AddSeconds(3));
            Assert.AreEqual(90, ledger.Playtime[game]);
            Assert.AreEqual(1, ledger.Sessions.Count);
            Assert.AreEqual(90, ledger.Sessions[0].Seconds, 0.01);
            Assert.AreEqual(start, ledger.Sessions[0].Start, "From the start of the first round in front.");
            Assert.AreEqual(now, ledger.Sessions[0].End);
        }

        [Test]
        public void GameBehindIsNotCountedTest()
        {
            // A wallpaper or a desktop pet: open all day, never in front.
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 555, start, 3600);
            tracker.Tick(Open(), 0, 3, now);
            Assert.IsEmpty(ledger.Played);
            Assert.IsEmpty(ledger.Playtime);
            Assert.IsEmpty(ledger.Sessions);
        }

        [Test]
        public void ShortLookIsNotAPlayTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 45);
            tracker.Tick(Open(), 0, 3, now);
            Assert.IsEmpty(ledger.Played);
            Assert.IsEmpty(ledger.Playtime);
        }

        [Test]
        public void OnlyTimeInFrontCountsTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100, 101);
            var now = Play(tracker, open, 101, start, 120);
            now = Play(tracker, open, 999, now, 300);
            now = Play(tracker, open, 100, now, 60);
            tracker.Tick(Open(), 0, 3, now.AddSeconds(3));
            Assert.AreEqual(180, ledger.Playtime[game]);
            Assert.AreEqual(180, ledger.Sessions.Sum(a => a.Seconds), 0.01);
        }

        [Test]
        public void GameStartedByAtrioIsNotCountedTwiceTest()
        {
            var ledger = new FakeLedger();
            ledger.Tracked.Add(game);
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 300);
            Assert.IsEmpty(ledger.Played);
            Assert.IsEmpty(ledger.Playtime);

            // Atrio stopped counting it but the game stays open: the rest counts, without another play.
            ledger.Tracked.Clear();
            now = Play(tracker, open, 100, now, 120);
            tracker.Tick(Open(), 0, 3, now.AddSeconds(3));
            Assert.IsEmpty(ledger.Played);
            Assert.AreEqual(120, ledger.Playtime[game]);
        }

        [Test]
        public void AtrioTakesOverTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 90);
            ledger.Tracked.Add(game);
            Play(tracker, open, 100, now, 60);
            Assert.AreEqual(90, ledger.Playtime[game], "What was played before Atrio counted it goes in, and nothing after.");
        }

        [Test]
        public void SharedProgramsCountOnceTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, new[] { 100 }, other, new[] { 100 });
            var now = Play(tracker, open, 100, start, 90);
            tracker.Tick(Open(), 0, 3, now);
            Assert.AreEqual(1, ledger.Playtime.Count);
            Assert.AreEqual(90, ledger.Playtime[other]);
            Assert.AreEqual(new List<Guid> { other }, ledger.Played);
        }

        [Test]
        public void SharedProgramsWithATrackedGameDontCountTest()
        {
            var ledger = new FakeLedger();
            ledger.Tracked.Add(game);
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, new[] { 100 }, other, new[] { 100 });
            var now = Play(tracker, open, 100, start, 120);
            tracker.Tick(Open(), 0, 3, now);
            Assert.IsEmpty(ledger.Playtime);
        }

        [Test]
        public void RestartKeepsTheSittingTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 90);
            tracker.FinishAll(true);
            Assert.AreEqual(90, ledger.Playtime[game], "Closing Atrio puts the time in the library.");
            Assert.AreEqual(1, ledger.Saved.Count);
            Assert.IsTrue(ledger.Saved[0].Counted);

            // Atrio opens again with the same game open: same sitting, no new play.
            var again = new OutsidePlayTracker(ledger, ledger.Saved, now);
            now = Play(again, open, 100, now, 60);
            again.Tick(Open(), 0, 3, now);
            Assert.AreEqual(1, ledger.Played.Count);
            Assert.AreEqual(150, ledger.Playtime[game]);
        }

        [Test]
        public void SameNumberOtherProgramIsANewPlayTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 90);
            tracker.FinishAll(true);

            // Windows gave the same number to a program that started later.
            ledger.Keys[100] = "100:2";
            var again = new OutsidePlayTracker(ledger, ledger.Saved, now);
            Play(again, open, 100, now, 90);
            Assert.AreEqual(2, ledger.Played.Count);
        }

        [Test]
        public void ShortSittingAddsUpAfterRestartTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            var now = Play(tracker, open, 100, start, 30);
            tracker.FinishAll(true);
            Assert.IsEmpty(ledger.Playtime);

            var again = new OutsidePlayTracker(ledger, ledger.Saved, now);
            now = Play(again, open, 100, now, 30);
            Assert.AreEqual(1, ledger.Played.Count, "30 + 30 seconds make a play.");
            again.Tick(Open(), 0, 3, now);
            Assert.AreEqual(60, ledger.Playtime[game]);
        }

        [Test]
        public void TimeLeftByACrashGoesInTest()
        {
            var ledger = new FakeLedger();
            var previous = new List<OpenSitting>
            {
                new OpenSitting
                {
                    GameId = game,
                    Processes = new List<string> { "100:1" },
                    Start = start,
                    LastPlayed = start.AddMinutes(10),
                    PendingSeconds = 600,
                    TotalSeconds = 600,
                    Counted = true
                }
            };

            var tracker = new OutsidePlayTracker(ledger, previous, start.AddHours(1));
            Assert.AreEqual(600, ledger.Playtime[game]);
            Assert.AreEqual(1, ledger.Saves, "The file forgets the time at once, so another crash can't add it twice.");
            Assert.AreEqual(0, ledger.Saved[0].PendingSeconds);

            // The game was closed meanwhile: nothing else happens.
            tracker.Tick(Open(), 0, 3, start.AddHours(1));
            tracker.FinishAll(true);
            Assert.AreEqual(600, ledger.Playtime[game]);
        }

        [Test]
        public void PendingTimeIsSavedTest()
        {
            var ledger = new FakeLedger();
            var tracker = new OutsidePlayTracker(ledger, null, start);
            var open = Open(game, 100);
            Play(tracker, open, 100, start, 6 * 60);
            Assert.AreEqual(1, ledger.Saves);
            Assert.Greater(ledger.Saved[0].PendingSeconds, 290);
            Assert.IsEmpty(ledger.Playtime);
        }
    }
}
