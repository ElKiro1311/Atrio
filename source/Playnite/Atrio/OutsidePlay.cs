using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>What the count of the games opened outside Atrio needs from the library, so it can be tested alone.</summary>
    public interface IOutsidePlayLedger
    {
        /// <summary>Atrio started it and counts it itself.</summary>
        bool IsTrackedByAtrio(Guid gameId);

        /// <summary>
        /// Every game Atrio is counting right now, also the ones the watch can't see, like emulated games: their
        /// emulator may live in the folder of another entry of the library.
        /// </summary>
        List<Guid> GetGamesRunByAtrio();

        /// <summary>Several games share the same programs, like two entries for one folder: only one gets the time.</summary>
        Guid ChooseOne(List<Guid> gameIds);

        string GetProcessKey(int processId);

        /// <summary>Last activity, one more play and, if it was unplayed, played: like a game started from Atrio.</summary>
        void MarkPlayed(Guid gameId);

        void AddPlaytime(Guid gameId, ulong seconds);
        void Record(Guid gameId, DateTime startUtc, DateTime endUtc, double seconds);
        void SaveOpen(List<OpenSitting> open);
    }

    /// <summary>
    /// Counts the time played in games opened outside Atrio: from their store, a shortcut or Steam itself. Only the
    /// time the game is in front counts, so programs that stay open all day, like a wallpaper or a desktop pet,
    /// don't fill the history. The time goes to the library like the time of a game started from Atrio.
    /// </summary>
    public class OutsidePlayTracker
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Less time in front than this isn't a play: a launcher that flashes, a misclick.</summary>
        public const double MinimumSeconds = 60;

        /// <summary>The time not yet in the library is saved this often, in case the computer turns off.</summary>
        public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

        /// <summary>
        /// A longer pause in front of the game closes a stretch in the history: the same play, but the hours of the
        /// night it stayed open don't spread the time over the wrong days.
        /// </summary>
        public static readonly TimeSpan PauseForNewStretch = TimeSpan.FromMinutes(15);

        private readonly IOutsidePlayLedger ledger;
        private readonly object sync = new object();
        private readonly Dictionary<Guid, OpenSitting> sittings = new Dictionary<Guid, OpenSitting>();

        /// <summary>Programs of games that Atrio started: when Atrio stops counting them, they were already counted.</summary>
        private readonly HashSet<string> startedByAtrio = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The programs of the games Atrio counts right now, to remember them if Atrio closes before the game.</summary>
        private readonly Dictionary<Guid, List<string>> trackedNow = new Dictionary<Guid, List<string>>();

        /// <summary>Each program is asked for its key once.</summary>
        private Dictionary<int, string> keys = new Dictionary<int, string>();
        private List<OpenSitting> leftovers;
        private DateTime lastSave;
        private bool firstTick = true;
        private bool changed;

        public OutsidePlayTracker(IOutsidePlayLedger ledger, IEnumerable<OpenSitting> previous, DateTime nowUtc)
        {
            this.ledger = ledger;
            lastSave = nowUtc;
            leftovers = previous?.Where(a => a != null).ToList() ?? new List<OpenSitting>();

            // Atrio closed without a goodbye (a crash, the power) with time not in the library: it goes in now,
            // and the file forgets it at once so another crash can't add it twice.
            foreach (var old in leftovers)
            {
                if (old.PendingSeconds > 0 && old.TotalSeconds >= MinimumSeconds)
                {
                    Commit(old);
                }
            }

            SaveIfChanged(nowUtc, leftovers);
        }

        public List<OpenSitting> GetSittings()
        {
            lock (sync)
            {
                return sittings.Values.Select(Copy).ToList();
            }
        }

        public void Tick(IReadOnlyDictionary<Guid, List<int>> open, int foregroundProcessId, double elapsedSeconds, DateTime nowUtc)
        {
            lock (sync)
            {
                open = open ?? new Dictionary<Guid, List<int>>();
                RefreshKeys(open);

                // Games that Atrio started count by themselves, and so do the programs they share with other
                // entries; the programs are remembered for when Atrio stops counting them.
                var runByAtrio = new HashSet<Guid>(ledger.GetGamesRunByAtrio() ?? new List<Guid>());
                var tracked = open.Keys.Where(a => runByAtrio.Contains(a) || ledger.IsTrackedByAtrio(a)).ToList();
                var trackedProcesses = new HashSet<int>(tracked.SelectMany(a => open[a]));
                trackedNow.Clear();
                foreach (var id in tracked)
                {
                    var programs = GetKeys(open[id]);
                    trackedNow[id] = programs;
                    foreach (var key in programs)
                    {
                        startedByAtrio.Add(key);
                    }
                }

                // A game Atrio counts that the watch can't see (an emulated one): its emulator could be the
                // program of another entry, so nothing outside counts meanwhile.
                var atrioPlaysElsewhere = runByAtrio.Any(a => !open.ContainsKey(a));

                foreach (var sitting in sittings.Values.ToList())
                {
                    if (!open.TryGetValue(sitting.GameId, out var processes) || processes.Any(trackedProcesses.Contains))
                    {
                        sittings.Remove(sitting.GameId);
                        Finish(sitting);
                    }
                    else
                    {
                        // A launcher can give way to the game itself: its programs are added as they appear.
                        foreach (var key in GetKeys(processes))
                        {
                            if (!sitting.Processes.Contains(key))
                            {
                                sitting.Processes.Add(key);
                            }
                        }
                    }
                }

                foreach (var id in ChooseGames(open, trackedProcesses))
                {
                    if (!sittings.ContainsKey(id))
                    {
                        sittings[id] = Begin(id, open[id], atrioPlaysElsewhere);
                    }
                }

                // What wasn't open again after a restart is over.
                if (firstTick)
                {
                    firstTick = false;
                    leftovers = new List<OpenSitting>();
                }

                if (foregroundProcessId > 0 && elapsedSeconds > 0 && !atrioPlaysElsewhere)
                {
                    var playing = sittings.Values.FirstOrDefault(a => open[a.GameId].Contains(foregroundProcessId));
                    if (playing != null)
                    {
                        Play(playing, elapsedSeconds, nowUtc);
                    }
                }

                if (nowUtc - lastSave >= SaveEvery && sittings.Values.Any(a => a.PendingSeconds > 0))
                {
                    changed = true;
                }

                SaveIfChanged(nowUtc, sittings.Values);
            }
        }

        /// <summary>
        /// Atrio is closing: the time played goes to the library now, and the open games are remembered so the
        /// same sitting isn't counted as a new play when Atrio opens again. So are the games Atrio started itself.
        /// </summary>
        public void FinishAll(bool keepForLater)
        {
            lock (sync)
            {
                var kept = new List<OpenSitting>();
                foreach (var sitting in sittings.Values)
                {
                    if (sitting.TotalSeconds >= MinimumSeconds)
                    {
                        Commit(sitting);
                    }

                    kept.Add(Copy(sitting));
                }

                foreach (var game in trackedNow.Where(a => !sittings.ContainsKey(a.Key) && a.Value.Count > 0))
                {
                    kept.Add(new OpenSitting { GameId = game.Key, Processes = game.Value.ToList(), Counted = true, TotalSeconds = MinimumSeconds });
                }

                sittings.Clear();
                try
                {
                    ledger.SaveOpen(keepForLater ? kept : new List<OpenSitting>());
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to save the games that are still open.");
                }
            }
        }

        private void SaveIfChanged(DateTime nowUtc, IEnumerable<OpenSitting> open)
        {
            if (!changed)
            {
                return;
            }

            changed = false;
            lastSave = nowUtc;
            try
            {
                ledger.SaveOpen(open.Select(Copy).ToList());
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to save the games that are still open.");
            }
        }

        /// <summary>
        /// One game for each set of programs, the same game every time. If Atrio counts any game of those
        /// programs, none of them counts here.
        /// </summary>
        private IEnumerable<Guid> ChooseGames(IReadOnlyDictionary<Guid, List<int>> open, HashSet<int> trackedProcesses)
        {
            foreach (var group in open.GroupBy(a => string.Join(",", a.Value.OrderBy(b => b))))
            {
                if (group.Any(a => a.Value.Any(trackedProcesses.Contains)))
                {
                    continue;
                }

                var ids = group.Select(a => a.Key).ToList();
                if (ids.Count == 1)
                {
                    yield return ids[0];
                    continue;
                }

                var current = ids.FirstOrDefault(a => sittings.ContainsKey(a));
                yield return current != Guid.Empty ? current : ledger.ChooseOne(ids);
            }
        }

        /// <summary>The keys of the programs still open, asked once for each program.</summary>
        private void RefreshKeys(IReadOnlyDictionary<Guid, List<int>> open)
        {
            var known = new Dictionary<int, string>();
            foreach (var id in open.Values.SelectMany(a => a).Distinct())
            {
                known[id] = keys.TryGetValue(id, out var key) ? key : ledger.GetProcessKey(id);
            }

            keys = known;
        }

        private List<string> GetKeys(IEnumerable<int> processIds)
        {
            return processIds.
                Select(a => keys.TryGetValue(a, out var key) ? key : ledger.GetProcessKey(a)).
                Where(a => a != null).
                Distinct().
                ToList();
        }

        private OpenSitting Begin(Guid gameId, List<int> processIds, bool atrioPlaysElsewhere)
        {
            var programs = GetKeys(processIds);
            var sitting = new OpenSitting { GameId = gameId, Processes = programs };

            // The same programs as before Atrio restarted: the same sitting, already counted as a play.
            var before = leftovers.FirstOrDefault(a => a.GameId == gameId && a.Processes != null && a.Processes.Intersect(programs).Any());
            if (before != null)
            {
                leftovers.Remove(before);
                sitting.Counted = before.Counted;
                sitting.TotalSeconds = before.TotalSeconds;
                if (before.TotalSeconds < MinimumSeconds)
                {
                    // Too short to have gone in: it keeps adding up.
                    sitting.PendingSeconds = before.PendingSeconds;
                    sitting.Start = before.Start;
                    sitting.LastPlayed = before.LastPlayed;
                }
            }

            if (atrioPlaysElsewhere || programs.Any(startedByAtrio.Contains))
            {
                sitting.Counted = true;
            }

            return sitting;
        }

        private void Play(OpenSitting sitting, double seconds, DateTime nowUtc)
        {
            // After a long pause the stretch before goes in by itself: the same play, its own hours.
            if (sitting.PendingSeconds > 0 && sitting.TotalSeconds >= MinimumSeconds &&
                sitting.LastPlayed != default(DateTime) && nowUtc - sitting.LastPlayed > PauseForNewStretch)
            {
                Commit(sitting);
            }

            if (sitting.PendingSeconds <= 0)
            {
                sitting.Start = nowUtc.AddSeconds(-seconds);
            }

            sitting.PendingSeconds += seconds;
            sitting.TotalSeconds += seconds;
            sitting.LastPlayed = nowUtc;
            if (!sitting.Counted && sitting.TotalSeconds >= MinimumSeconds)
            {
                sitting.Counted = true;
                try
                {
                    ledger.MarkPlayed(sitting.GameId);
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to mark {sitting.GameId} as played.");
                }
            }
        }

        private void Finish(OpenSitting sitting)
        {
            if (sitting.TotalSeconds >= MinimumSeconds)
            {
                Commit(sitting);
            }
        }

        /// <summary>
        /// The pending time goes to the library and the history. It's taken off the sitting first: if the library
        /// fails, that time is lost once rather than added again in every round.
        /// </summary>
        private void Commit(OpenSitting sitting)
        {
            var seconds = sitting.PendingSeconds;
            if (seconds <= 0)
            {
                return;
            }

            var end = sitting.LastPlayed == default(DateTime) ? sitting.Start : sitting.LastPlayed;
            var start = sitting.Start == default(DateTime) ? end.AddSeconds(-seconds) : sitting.Start;
            sitting.PendingSeconds = 0;
            sitting.Start = default(DateTime);
            changed = true;
            try
            {
                ledger.AddPlaytime(sitting.GameId, (ulong)Math.Round(seconds));
                ledger.Record(sitting.GameId, start, end, seconds);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to add the time played outside Atrio to {sitting.GameId}.");
            }
        }

        private static OpenSitting Copy(OpenSitting a)
        {
            return new OpenSitting
            {
                GameId = a.GameId,
                Processes = a.Processes?.ToList() ?? new List<string>(),
                Start = a.Start,
                LastPlayed = a.LastPlayed,
                PendingSeconds = a.PendingSeconds,
                TotalSeconds = a.TotalSeconds,
                Counted = a.Counted
            };
        }
    }

    /// <summary>The library of Atrio behind the count.</summary>
    public class LibraryOutsidePlayLedger : IOutsidePlayLedger
    {
        private readonly IGameDatabaseMain database;
        private readonly Func<IEnumerable<Guid>> runByAtrio;

        public LibraryOutsidePlayLedger(IGameDatabaseMain database, Func<IEnumerable<Guid>> runByAtrio = null)
        {
            this.database = database;
            this.runByAtrio = runByAtrio;
        }

        public bool IsTrackedByAtrio(Guid gameId)
        {
            var game = database.Games.Get(gameId);
            return game != null && (game.IsRunning || game.IsLaunching);
        }

        public List<Guid> GetGamesRunByAtrio()
        {
            try
            {
                return runByAtrio?.Invoke()?.ToList() ?? new List<Guid>();
            }
            catch (InvalidOperationException)
            {
                // The list changed while it was read: the next round reads it again.
                return new List<Guid>();
            }
        }

        public Guid ChooseOne(List<Guid> gameIds)
        {
            return gameIds.
                Select(a => database.Games.Get(a)).
                Where(a => a != null).
                OrderByDescending(a => a.Playtime).
                ThenByDescending(a => a.LastActivity ?? DateTime.MinValue).
                ThenBy(a => a.Id).
                Select(a => a.Id).
                DefaultIfEmpty(gameIds.Min()).
                First();
        }

        public string GetProcessKey(int processId)
        {
            return OpenGames.GetProcessKey(processId);
        }

        public void MarkPlayed(Guid gameId)
        {
            var game = database.Games.Get(gameId);
            if (game == null)
            {
                return;
            }

            game.LastActivity = DateTime.Now;
            game.PlayCount += 1;
            var status = database.GetCompletionStatusSettings();
            if (status.PlayedStatus != Constants.MaxGuidVal && status.PlayedStatus != Guid.Empty &&
                (game.CompletionStatusId == Guid.Empty || game.CompletionStatusId == status.DefaultStatus))
            {
                game.CompletionStatusId = status.PlayedStatus;
            }

            database.Games.Update(game);
        }

        public void AddPlaytime(Guid gameId, ulong seconds)
        {
            var game = database.Games.Get(gameId);
            if (game == null || seconds == 0)
            {
                return;
            }

            game.Playtime += seconds;
            database.Games.Update(game);
        }

        public void Record(Guid gameId, DateTime startUtc, DateTime endUtc, double seconds)
        {
            PlayHistory.Record(gameId, startUtc, endUtc, seconds, true);
        }

        public void SaveOpen(List<OpenSitting> open)
        {
            PlayHistory.SetOpenSittings(open);
        }
    }

    /// <summary>The count of the games opened outside Atrio, attached to the watch of the open games.</summary>
    public static class OutsidePlay
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static OutsidePlayTracker tracker;
        private static Func<bool> isEnabled;

        public static void Attach(IGameDatabaseMain database, Func<bool> enabled, Func<IEnumerable<Guid>> runByAtrio = null)
        {
            if (tracker != null || database == null)
            {
                return;
            }

            isEnabled = enabled;
            tracker = new OutsidePlayTracker(new LibraryOutsidePlayLedger(database, runByAtrio), PlayHistory.GetOpenSittings(), DateTime.UtcNow);
            OpenGames.Ticked += OpenGames_Ticked;
        }

        private static void OpenGames_Ticked(object sender, OpenGamesTickEventArgs e)
        {
            Tick(e.Open, e.ForegroundProcessId, e.ElapsedSeconds);
        }

        /// <summary>One round of the watch. The bench also calls it to play without a real game in front.</summary>
        public static void Tick(IReadOnlyDictionary<Guid, List<int>> open, int foregroundProcessId, double elapsedSeconds)
        {
            var current = tracker;
            if (current == null)
            {
                return;
            }

            try
            {
                if (isEnabled?.Invoke() == false)
                {
                    current.Tick(new Dictionary<Guid, List<int>>(), 0, 0, DateTime.UtcNow);
                    return;
                }

                current.Tick(open, foregroundProcessId, elapsedSeconds, DateTime.UtcNow);
            }
            catch (Exception exc)
            {
                logger.Error(exc, "Failed to count the time of the games opened outside Atrio.");
            }
        }

        /// <summary>Atrio is closing: what was played goes to the library.</summary>
        public static void Shutdown()
        {
            var current = tracker;
            tracker = null;
            OpenGames.Ticked -= OpenGames_Ticked;
            try
            {
                current?.FinishAll(true);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to save the time of the games opened outside Atrio.");
            }
        }
    }
}
