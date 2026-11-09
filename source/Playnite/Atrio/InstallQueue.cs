using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Playnite.Atrio
{
    public class QueuedInstall
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public DateTime Added { get; set; }
    }

    public class InstallQueueData
    {
        public List<QueuedInstall> Items { get; set; } = new List<QueuedInstall>();

        /// <summary>The queue waits until then to start, in UTC. Null starts right away.</summary>
        public DateTime? StartAt { get; set; }

        /// <summary>The hour the person picked for it, 0 to 23, kept for the next time.</summary>
        public int StartHour { get; set; } = 3;
    }

    public enum QueueStep
    {
        /// <summary>Nothing to do now.</summary>
        Wait,
        /// <summary>Start the first one.</summary>
        Start,
        /// <summary>The first one is installed: out of the queue, and the next one.</summary>
        Done,
        /// <summary>The first one stopped without being installed, or waited too long: out of the queue.</summary>
        Failed
    }

    /// <summary>
    /// Games to install one after the other, each when the one before has finished, and if the person wants,
    /// from an hour of the night on. The queue is kept in its own file, so it survives closing Atrio; it only
    /// runs while Atrio is open.
    /// </summary>
    public static class InstallQueue
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string FileName = "atrio-cola.json";

        /// <summary>After asking to install, this long without the game installing means it didn't start.</summary>
        public static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(45);

        private static readonly object sync = new object();
        private static InstallQueueData data;
        private static System.Threading.Timer timer;
        private static Func<Guid, Game> findGame;
        private static Action<Game> install;
        private static Func<bool> canStart;

        // The one being installed now: when it was asked for, and the last time it moved.
        private static Guid current;
        private static DateTime currentSince;
        private static DateTime currentMoved;
        private static int? currentPercent;
        private static readonly List<string> installed = new List<string>();
        private static readonly List<string> failed = new List<string>();

        public static event EventHandler Changed;

        /// <summary>Set by the program: what to tell when the queue is done.</summary>
        public static Action<List<string>, List<string>> Finished { get; set; }

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        private static InstallQueueData Load()
        {
            if (data != null)
            {
                return data;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    data = Playnite.SDK.Data.Serialization.FromJsonFile<InstallQueueData>(FilePath);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to read the install queue.");
            }

            data = data ?? new InstallQueueData();
            data.Items = data.Items?.Where(a => a != null && a.GameId != Guid.Empty).ToList() ?? new List<QueuedInstall>();
            if (data.StartAt != null)
            {
                data.StartAt = PlayHistory.ToUtc(data.StartAt.Value);
            }

            return data;
        }

        private static void Save()
        {
            try
            {
                FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(data ?? new InstallQueueData(), true));
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to save the install queue.");
            }
        }

        private static void Raise()
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static List<QueuedInstall> GetItems()
        {
            lock (sync)
            {
                return Load().Items.Select(a => new QueuedInstall { GameId = a.GameId, Name = a.Name, Added = a.Added }).ToList();
            }
        }

        public static Guid Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public static DateTime? StartAt
        {
            get
            {
                lock (sync)
                {
                    return Load().StartAt;
                }
            }
        }

        public static int StartHour
        {
            get
            {
                lock (sync)
                {
                    return Load().StartHour;
                }
            }
        }

        public static bool Contains(Guid gameId)
        {
            lock (sync)
            {
                return Load().Items.Any(a => a.GameId == gameId);
            }
        }

        /// <summary>Games that can be installed: not installed, not being installed, from a store.</summary>
        public static bool CanQueue(Game game)
        {
            return game != null && !game.IsInstalled && !game.IsInstalling && !game.IsUninstalling && !game.IsCustomGame;
        }

        public static int Add(IEnumerable<Game> games)
        {
            var count = 0;
            lock (sync)
            {
                var list = Load().Items;
                foreach (var game in games ?? Enumerable.Empty<Game>())
                {
                    if (CanQueue(game) && !list.Any(a => a.GameId == game.Id))
                    {
                        list.Add(new QueuedInstall { GameId = game.Id, Name = game.Name, Added = DateTime.UtcNow });
                        count++;
                    }
                }

                if (count > 0)
                {
                    Save();
                }
            }

            if (count > 0)
            {
                Raise();
            }

            return count;
        }

        /// <summary>Out of the queue. If it's the one installing now, it keeps installing: only the waiting stops.</summary>
        public static void Remove(Guid gameId)
        {
            lock (sync)
            {
                if (Load().Items.RemoveAll(a => a.GameId == gameId) == 0)
                {
                    return;
                }

                if (current == gameId)
                {
                    current = Guid.Empty;
                }

                Save();
            }

            Raise();
        }

        public static void Move(Guid gameId, int delta)
        {
            lock (sync)
            {
                var list = Load().Items;
                var index = list.FindIndex(a => a.GameId == gameId);
                var target = index + delta;

                // The one installing now stays first.
                var first = current != Guid.Empty ? 1 : 0;
                if (index < first || target < first || target >= list.Count)
                {
                    return;
                }

                var item = list[index];
                list.RemoveAt(index);
                list.Insert(target, item);
                Save();
            }

            Raise();
        }

        public static void Clear()
        {
            lock (sync)
            {
                var list = Load().Items;
                list.RemoveAll(a => a.GameId != current);
                Save();
            }

            Raise();
        }

        /// <summary>The next time the clock of the computer says that hour, in UTC.</summary>
        public static DateTime NextTime(int hour, DateTime nowLocal)
        {
            var today = nowLocal.Date.AddHours(Math.Max(0, Math.Min(23, hour)));
            var next = today > nowLocal ? today : today.AddDays(1);
            return next.ToUniversalTime();
        }

        /// <summary>From that hour on, or right away with null.</summary>
        public static void SetStart(int? hour)
        {
            lock (sync)
            {
                var queue = Load();
                if (hour == null)
                {
                    queue.StartAt = null;
                }
                else
                {
                    queue.StartHour = Math.Max(0, Math.Min(23, hour.Value));
                    queue.StartAt = NextTime(queue.StartHour, DateTime.Now);
                }

                Save();
            }

            Raise();
        }

        /// <summary>
        /// What to do with the first of the queue now. Pure, for the tests: the state of the game, when it was
        /// asked for and when it last moved.
        /// </summary>
        public static QueueStep Decide(Game game, bool isCurrent, DateTime nowUtc, DateTime since, DateTime moved, DateTime? startAt)
        {
            if (!isCurrent)
            {
                if (startAt != null && nowUtc < startAt)
                {
                    return QueueStep.Wait;
                }

                if (game == null || game.IsInstalled)
                {
                    return QueueStep.Done;
                }

                return game.IsInstalling || game.IsUninstalling ? QueueStep.Wait : QueueStep.Start;
            }

            // Taken out of the library halfway: it didn't install.
            if (game == null)
            {
                return QueueStep.Failed;
            }

            if (game.IsInstalled)
            {
                return QueueStep.Done;
            }

            if (!game.IsInstalling)
            {
                // Asking takes a moment; after that, a game that isn't installing anymore stopped.
                return nowUtc - since > StartPatience ? QueueStep.Failed : QueueStep.Wait;
            }

            // While it installs, however long, the next one waits: two at once would share the line and the disk.
            // Taking it out of the queue is what lets the rest go on.
            return QueueStep.Wait;
        }

        /// <summary>The program gives what the queue needs: finding a game, installing it, and whether it can start one now.</summary>
        public static void Attach(Func<Guid, Game> find, Action<Game> installGame, Func<bool> allowed)
        {
            findGame = find;
            install = installGame;
            canStart = allowed;
            if (timer == null)
            {
                timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
            }
        }

        private static int ticking;

        private static void Tick()
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1)
            {
                return;
            }

            try
            {
                QueuedInstall first;
                bool isCurrent;
                DateTime? startAt;
                lock (sync)
                {
                    var queue = Load();
                    first = queue.Items.FirstOrDefault();
                    isCurrent = first != null && first.GameId == current;
                    startAt = queue.StartAt;
                }

                if (first == null || findGame == null)
                {
                    return;
                }

                var game = findGame(first.GameId);
                var now = DateTime.UtcNow;
                if (isCurrent)
                {
                    var percent = DirectInstallProgress.Get(first.GameId);
                    if (percent != currentPercent)
                    {
                        currentPercent = percent;
                        currentMoved = now;
                    }
                }

                var step = Decide(game, isCurrent, now, currentSince, currentMoved, startAt);
                switch (step)
                {
                    case QueueStep.Start:
                        if (canStart?.Invoke() == false)
                        {
                            return;
                        }

                        lock (sync)
                        {
                            current = first.GameId;
                            currentSince = now;
                            currentMoved = now;
                            currentPercent = null;
                            Load().StartAt = null;
                            Save();
                        }

                        logger.Info($"Install queue: starting {game.Name}.");
                        install?.Invoke(game);
                        Raise();
                        break;
                    case QueueStep.Done:
                    case QueueStep.Failed:
                        lock (sync)
                        {
                            Load().Items.RemoveAll(a => a.GameId == first.GameId);
                            current = Guid.Empty;
                            Save();
                            if (isCurrent)
                            {
                                (step == QueueStep.Done ? installed : failed).Add(first.Name);
                            }
                        }

                        logger.Info($"Install queue: {first.Name} {(step == QueueStep.Done ? "installed" : "didn't install")}.");
                        Raise();
                        List<string> done = null, bad = null;
                        lock (sync)
                        {
                            if (Load().Items.Count == 0 && (installed.Count > 0 || failed.Count > 0))
                            {
                                done = installed.ToList();
                                bad = failed.ToList();
                                installed.Clear();
                                failed.Clear();
                            }
                        }

                        if (done != null)
                        {
                            Finished?.Invoke(done, bad);
                        }

                        break;
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "The install queue failed a step.");
            }
            finally
            {
                Interlocked.Exchange(ref ticking, 0);
            }
        }
    }
}
