using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>One sitting with a game, in universal time.</summary>
    public class PlaySession
    {
        public Guid GameId { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        /// <summary>Time really played. With a game opened outside Atrio it's only the time it was in front.</summary>
        public double Seconds { get; set; }

        /// <summary>The game was opened outside Atrio: Atrio only saw it running.</summary>
        public bool Outside { get; set; }

        /// <summary>Played on another computer, which the synchronization brought: null when it was played here.</summary>
        public string Machine { get; set; }
    }

    /// <summary>A game opened outside Atrio that is still open, kept in case Atrio closes before the game.</summary>
    public class OpenSitting
    {
        public Guid GameId { get; set; }

        /// <summary>Its programs, as process number and start time, to know it's the same sitting after a restart.</summary>
        public List<string> Processes { get; set; } = new List<string>();
        public DateTime Start { get; set; }
        public DateTime LastPlayed { get; set; }

        /// <summary>Played time that isn't in the library yet.</summary>
        public double PendingSeconds { get; set; }

        /// <summary>All the time in front in this sitting, to know when it's long enough to be a play.</summary>
        public double TotalSeconds { get; set; }

        /// <summary>The last activity and the count of plays already have this sitting.</summary>
        public bool Counted { get; set; }
    }

    public class PlayHistoryData
    {
        /// <summary>When Atrio started keeping the sittings: before it, the history knows nothing.</summary>
        public DateTime Since { get; set; }
        public List<PlaySession> Sessions { get; set; } = new List<PlaySession>();
        public List<OpenSitting> Open { get; set; } = new List<OpenSitting>();
    }

    /// <summary>
    /// What was played and when. The library only keeps the total of each game; this keeps each sitting, for a
    /// bit more than a year, for the summaries of Atrio. It never leaves the folder of Atrio.
    /// </summary>
    public static class PlayHistory
    {
        public const string FileName = "atrio-sesiones.json";
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(400);

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object sync = new object();
        private static PlayHistoryData data;
        private static string filePath;

        /// <summary>Raised after a sitting is added, on the thread that added it.</summary>
        public static event EventHandler Changed;

        /// <summary>The sitting just added, on the thread that added it: for what has to happen after playing.</summary>
        public static event EventHandler<PlaySession> Recorded;

        public static string FilePath
        {
            get => filePath ?? Path.Combine(PlaynitePaths.ConfigRootPath, FileName);
            set
            {
                lock (sync)
                {
                    filePath = value;
                    data = null;
                }
            }
        }

        public static DateTime Since
        {
            get
            {
                lock (sync)
                {
                    return Load().Since;
                }
            }
        }

        public static void Record(Guid gameId, DateTime startUtc, DateTime endUtc, double seconds, bool outside)
        {
            if (gameId == Guid.Empty || seconds <= 0 || endUtc < startUtc)
            {
                return;
            }

            var session = new PlaySession
            {
                GameId = gameId,
                Start = ToUtc(startUtc),
                End = ToUtc(endUtc),
                Seconds = Math.Round(seconds, 1),
                Outside = outside
            };

            lock (sync)
            {
                var current = Load();
                current.Sessions.Add(session);
                Prune(current, DateTime.UtcNow);
                Save(current);
            }

            Changed?.Invoke(null, EventArgs.Empty);
            Recorded?.Invoke(null, session);
        }

        /// <summary>The sittings that touch this period, oldest first.</summary>
        public static List<PlaySession> GetSessions(DateTime fromUtc, DateTime toUtc)
        {
            lock (sync)
            {
                return Load().Sessions.
                    Where(a => a.End > fromUtc && a.Start < toUtc).
                    OrderBy(a => a.Start).
                    Select(a => new PlaySession { GameId = a.GameId, Start = a.Start, End = a.End, Seconds = a.Seconds, Outside = a.Outside, Machine = a.Machine }).
                    ToList();
            }
        }

        /// <summary>
        /// Sittings of other computers, which the synchronization brought, all in one save. Only the summaries change:
        /// what happens after playing, like the copies of the saves, is for the computer where it was played.
        /// Returns the ones that are in the history as sittings of that computer: the new ones, and the ones a round
        /// cut in half had already put there. A sitting of the same game that starts at the same moment and is of
        /// this computer, or of another one, is the same sitting: it's left out.
        /// </summary>
        public static List<PlaySession> AddRemoteMany(IEnumerable<PlaySession> sessions)
        {
            var recorded = new List<PlaySession>();
            var added = false;
            lock (sync)
            {
                var current = Load();
                var known = new Dictionary<string, string>();
                foreach (var session in current.Sessions)
                {
                    known[session.GameId + "|" + ToUtc(session.Start).Ticks] = session.Machine ?? string.Empty;
                }

                var limit = DateTime.UtcNow - KeepFor;
                foreach (var session in sessions ?? Enumerable.Empty<PlaySession>())
                {
                    if (session == null || session.GameId == Guid.Empty || session.Seconds <= 0 || session.Machine == null)
                    {
                        continue;
                    }

                    var start = ToUtc(session.Start);
                    var end = ToUtc(session.End);
                    var key = session.GameId + "|" + start.Ticks;
                    var copy = new PlaySession
                    {
                        GameId = session.GameId,
                        Start = start,
                        End = end,
                        Seconds = session.Seconds,
                        Outside = session.Outside,
                        Machine = session.Machine
                    };

                    if (known.TryGetValue(key, out var machine))
                    {
                        if (machine == session.Machine)
                        {
                            recorded.Add(copy);
                        }

                        continue;
                    }

                    if (end < limit)
                    {
                        continue;
                    }

                    known[key] = session.Machine;
                    current.Sessions.Add(copy);
                    recorded.Add(copy);
                    added = true;
                }

                if (added)
                {
                    Prune(current, DateTime.UtcNow);
                    Save(current);
                }
            }

            if (added)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }

            return recorded;
        }

        /// <summary>
        /// Takes sittings of other computers out of the history, so that they can come in again: the library came back
        /// from a backup older than them.
        /// </summary>
        public static void RemoveRemote(IEnumerable<PlaySession> sessions)
        {
            var keys = new HashSet<string>((sessions ?? Enumerable.Empty<PlaySession>()).
                Where(a => a?.Machine != null).
                Select(a => a.Machine + "|" + a.GameId + "|" + ToUtc(a.Start).Ticks));
            if (keys.Count == 0)
            {
                return;
            }

            lock (sync)
            {
                var current = Load();
                if (current.Sessions.RemoveAll(a => a.Machine != null && keys.Contains(a.Machine + "|" + a.GameId + "|" + ToUtc(a.Start).Ticks)) > 0)
                {
                    Save(current);
                }
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// The folder of Atrio was copied to another computer: what was played before belongs to the first one,
        /// which keeps telling it. Returns the sittings that changed hands.
        /// </summary>
        public static List<PlaySession> GiveLocalTo(string machine)
        {
            var given = new List<PlaySession>();
            lock (sync)
            {
                var current = Load();
                foreach (var session in current.Sessions.Where(a => a.Machine == null))
                {
                    session.Machine = machine;
                    given.Add(new PlaySession { GameId = session.GameId, Start = session.Start, End = session.End, Seconds = session.Seconds, Outside = session.Outside, Machine = machine });
                }

                if (given.Count > 0)
                {
                    Save(current);
                }
            }

            return given;
        }

        public static List<OpenSitting> GetOpenSittings()
        {
            lock (sync)
            {
                return Load().Open.Select(a => new OpenSitting
                {
                    GameId = a.GameId,
                    Processes = a.Processes?.ToList() ?? new List<string>(),
                    Start = a.Start,
                    LastPlayed = a.LastPlayed,
                    PendingSeconds = a.PendingSeconds,
                    TotalSeconds = a.TotalSeconds,
                    Counted = a.Counted
                }).ToList();
            }
        }

        public static void SetOpenSittings(IEnumerable<OpenSitting> sittings)
        {
            lock (sync)
            {
                var current = Load();
                current.Open = sittings?.ToList() ?? new List<OpenSitting>();
                Save(current);
            }
        }

        public static void Prune(PlayHistoryData history, DateTime nowUtc)
        {
            var limit = nowUtc - KeepFor;
            history.Sessions.RemoveAll(a => a.End < limit);
        }

        private static PlayHistoryData Load()
        {
            if (data != null)
            {
                return data;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    data = Playnite.Common.Serialization.FromJsonFile<PlayHistoryData>(FilePath);
                }
            }
            catch (Exception e)
            {
                // A broken file shouldn't stop the count: it's kept aside and a new one starts.
                logger.Error(e, "Failed to read the history of play sessions.");
                try
                {
                    File.Copy(FilePath, FilePath + ".roto", true);
                }
                catch
                {
                }
            }

            if (data == null)
            {
                data = new PlayHistoryData { Since = DateTime.UtcNow };
            }

            data.Sessions = data.Sessions?.Where(a => a != null).ToList() ?? new List<PlaySession>();
            data.Open = data.Open?.Where(a => a != null).ToList() ?? new List<OpenSitting>();

            // Every time is compared in universal time, whatever the reader of the file did with it.
            foreach (var session in data.Sessions)
            {
                session.Start = ToUtc(session.Start);
                session.End = ToUtc(session.End);
            }

            foreach (var sitting in data.Open)
            {
                sitting.Start = ToUtc(sitting.Start);
                sitting.LastPlayed = ToUtc(sitting.LastPlayed);
            }

            if (data.Since == default(DateTime))
            {
                data.Since = data.Sessions.Count > 0 ? data.Sessions.Min(a => a.Start) : DateTime.UtcNow;
            }

            data.Since = ToUtc(data.Since);
            return data;
        }

        public static DateTime ToUtc(DateTime time)
        {
            if (time == default(DateTime))
            {
                return time;
            }

            switch (time.Kind)
            {
                case DateTimeKind.Local:
                    return time.ToUniversalTime();
                case DateTimeKind.Unspecified:
                    return DateTime.SpecifyKind(time, DateTimeKind.Utc);
                default:
                    return time;
            }
        }

        private static void Save(PlayHistoryData history)
        {
            try
            {
                var path = FilePath;
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Playnite.Common.Serialization.ToJson(history, false));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to save the history of play sessions.");
            }
        }
    }
}
