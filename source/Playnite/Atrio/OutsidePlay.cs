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
        /// <summary>Less time in front than this isn't a play: a launcher that flashes, a misclick.</summary>
        public const double MinimumSeconds = 60;

        /// <summary>The time not yet in the library is saved this often, in case the computer turns off.</summary>
        public static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

        private readonly IOutsidePlayLedger ledger;
        private readonly object sync = new object();
        private readonly Dictionary<Guid, OpenSitting> sittings = new Dictionary<Guid, OpenSitting>();

        /// <summary>Programs of games that Atrio started: when Atrio stops counting them, they were already counted.</summary>
        private readonly HashSet<string> startedByAtrio = new HashSet<string>(StringComparer.Ordinal);
        private List<OpenSitting> leftovers;
        private DateTime lastSave;
        private bool firstTick = true;

        public OutsidePlayTracker(IOutsidePlayLedger ledger, IEnumerable<OpenSitting> previous, DateTime nowUtc)
        {
            this.ledger = ledger;
            lastSave = nowUtc;
            leftovers = previous?.Where(a => a != null).ToList() ?? new List<OpenSitting>();

            // Atrio closed without a goodbye (a crash, the power) with time not in the library: it goes in now,
            // and the file forgets it at once so another crash can't add it twice.
            var committed = false;
            foreach (var old in leftovers)
            {
                if (old.PendingSeconds > 0 && old.TotalSeconds >= MinimumSeconds)
                {
                    Commit(old);
                    committed = true;
                }
            }

            if (committed)
            {
                ledger.SaveOpen(leftovers.Select(Copy).ToList());
            }
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

                // Games that Atrio started count by themselves, and so do the programs they share with other
                // entries; the programs are remembered for when Atrio stops counting them.
                var tracked = open.Keys.Where(ledger.IsTrackedByAtrio).ToList();
                var trackedProcesses = new HashSet<int>(tracked.SelectMany(a => open[a]));
                foreach (var id in tracked)
                {
                    foreach (var key in GetKeys(open[id]))
                    {
                        startedByAtrio.Add(key);
                    }
                }

                foreach (var sitting in sittings.Values.ToList())
                {
                    if (!open.TryGetValue(sitting.GameId, out var processes) || processes.Any(trackedProcesses.Contains))
                    {
                        Finish(sitting);
                        sittings.Remove(sitting.GameId);
                    }
                }

                foreach (var id in ChooseGames(open, trackedProcesses))
                {
                    if (!sittings.ContainsKey(id))
                    {
                        sittings[id] = Begin(id, open[id]);
                    }
                }

                // What wasn't open again after a restart is over.
                if (firstTick)
                {
                    firstTick = false;
                    leftovers = new List<OpenSitting>();
                }

                if (foregroundProcessId > 0 && elapsedSeconds > 0)
                {
                    var playing = sittings.Values.FirstOrDefault(a => open[a.GameId].Contains(foregroundProcessId));
                    if (playing != null)
                    {
                        Play(playing, elapsedSeconds, nowUtc);
                    }
                }

                if (nowUtc - lastSave >= SaveEvery && sittings.Values.Any(a => a.PendingSeconds > 0))
                {
                    lastSave = nowUtc;
                    ledger.SaveOpen(sittings.Values.Select(Copy).ToList());
                }
            }
        }

        /// <summary>
        /// Atrio is closing: the time played goes to the library now, and the open games are remembered so the
        /// same sitting isn't counted as a new play when Atrio opens again.
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

                sittings.Clear();
                ledger.SaveOpen(keepForLater ? kept : new List<OpenSitting>());
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

        private List<string> GetKeys(IEnumerable<int> processIds)
        {
            return processIds.Select(ledger.GetProcessKey).Where(a => a != null).ToList();
        }

        private OpenSitting Begin(Guid gameId, List<int> processIds)
        {
            var keys = GetKeys(processIds);
            var sitting = new OpenSitting { GameId = gameId, Processes = keys };

            // The same programs as before Atrio restarted: the same sitting, already counted as a play.
            var before = leftovers.FirstOrDefault(a => a.GameId == gameId && a.Processes != null && a.Processes.Intersect(keys).Any());
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

            if (keys.Any(startedByAtrio.Contains))
            {
                sitting.Counted = true;
            }

            return sitting;
        }

        private void Play(OpenSitting sitting, double seconds, DateTime nowUtc)
        {
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
                ledger.MarkPlayed(sitting.GameId);
            }
        }

        private void Finish(OpenSitting sitting)
        {
            if (sitting.TotalSeconds >= MinimumSeconds)
            {
                Commit(sitting);
            }
        }

        private void Commit(OpenSitting sitting)
        {
            var seconds = sitting.PendingSeconds;
            if (seconds <= 0)
            {
                return;
            }

            var end = sitting.LastPlayed == default(DateTime) ? sitting.Start : sitting.LastPlayed;
            var start = sitting.Start == default(DateTime) ? end.AddSeconds(-seconds) : sitting.Start;
            ledger.AddPlaytime(sitting.GameId, (ulong)Math.Round(seconds));
            ledger.Record(sitting.GameId, start, end, seconds);
            sitting.PendingSeconds = 0;
            sitting.Start = default(DateTime);
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

        public LibraryOutsidePlayLedger(IGameDatabaseMain database)
        {
            this.database = database;
        }

        public bool IsTrackedByAtrio(Guid gameId)
        {
            var game = database.Games.Get(gameId);
            return game != null && (game.IsRunning || game.IsLaunching);
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

        public static void Attach(IGameDatabaseMain database, Func<bool> enabled)
        {
            if (tracker != null || database == null)
            {
                return;
            }

            isEnabled = enabled;
            tracker = new OutsidePlayTracker(new LibraryOutsidePlayLedger(database), PlayHistory.GetOpenSittings(), DateTime.UtcNow);
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
