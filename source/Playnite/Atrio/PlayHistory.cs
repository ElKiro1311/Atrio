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

            lock (sync)
            {
                var current = Load();
                current.Sessions.Add(new PlaySession
                {
                    GameId = gameId,
                    Start = ToUtc(startUtc),
                    End = ToUtc(endUtc),
                    Seconds = Math.Round(seconds, 1),
                    Outside = outside
                });
                Prune(current, DateTime.UtcNow);
                Save(current);
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>The sittings that touch this period, oldest first.</summary>
        public static List<PlaySession> GetSessions(DateTime fromUtc, DateTime toUtc)
        {
            lock (sync)
            {
                return Load().Sessions.
                    Where(a => a.End > fromUtc && a.Start < toUtc).
                    OrderBy(a => a.Start).
                    Select(a => new PlaySession { GameId = a.GameId, Start = a.Start, End = a.End, Seconds = a.Seconds, Outside = a.Outside }).
                    ToList();
            }
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
