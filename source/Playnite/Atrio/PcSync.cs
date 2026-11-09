using Microsoft.Win32;
using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Playnite.Atrio
{
    /// <summary>One value of one game, and when it was set. The first values a computer brings have no date.</summary>
    public class SyncValue
    {
        public string V { get; set; }
        public DateTime T { get; set; }

        public SyncValue Copy() => new SyncValue { V = V, T = T };
    }

    public class SyncSession
    {
        public string Game { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public double Seconds { get; set; }
        public bool Outside { get; set; }
    }

    /// <summary>What one computer tells the others, in its own file of the shared folder.</summary>
    public class SyncMachineFile
    {
        public int Format { get; set; } = 1;
        public string Machine { get; set; }

        /// <summary>The fingerprint of its Windows: files of this same Windows, from an identity it had before, are not news.</summary>
        public string Computer { get; set; }
        public string Name { get; set; }
        public DateTime Updated { get; set; }
        public Dictionary<string, Dictionary<string, SyncValue>> Games { get; set; } = new Dictionary<string, Dictionary<string, SyncValue>>();
        public List<SyncSession> Sessions { get; set; } = new List<SyncSession>();
    }

    /// <summary>What this computer remembers between two synchronizations. It never leaves it.</summary>
    public class SyncState
    {
        public string Machine { get; set; }

        /// <summary>A fingerprint of this Windows, to notice when the folder of Atrio was copied to another computer.</summary>
        public string Computer { get; set; }
        public DateTime? FirstSync { get; set; }
        public DateTime? LastSync { get; set; }
        public Dictionary<string, Dictionary<string, SyncValue>> Snapshot { get; set; } = new Dictionary<string, Dictionary<string, SyncValue>>();

        /// <summary>The game of this library behind each key the last time: a game removed and imported again is new.</summary>
        public Dictionary<string, Guid> LocalIds { get; set; } = new Dictionary<string, Guid>();

        /// <summary>The sittings of other computers already added here: when and to which game, to forget them after a while.</summary>
        public Dictionary<string, AppliedSitting> Applied { get; set; } = new Dictionary<string, AppliedSitting>();

        /// <summary>What the own file said the last time it was written, so that the cloud only gets it when it changes.</summary>
        public string Written { get; set; }

        /// <summary>
        /// The mark left in the folder of the library at the end of the last round. A copy of the library brought back
        /// from a backup carries an older one: then this library didn't change, it went back in time.
        /// </summary>
        public long LibraryStamp { get; set; }
    }

    public class AppliedSitting
    {
        public string Machine { get; set; }
        public Guid GameId { get; set; }
        public DateTime Start { get; set; }

        /// <summary>When its time went to this library.</summary>
        public DateTime At { get; set; }
    }

    public class SyncChange
    {
        public string Key { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }
    }

    public class SyncPlan
    {
        public Dictionary<string, Dictionary<string, SyncValue>> Snapshot { get; set; }
        public List<SyncChange> Changes { get; set; } = new List<SyncChange>();
    }

    /// <summary>
    /// How the values of several computers come together: for each value of each game, the last one set wins.
    /// The values a computer had before synchronizing for the first time have no date: between two of those,
    /// a favorite, a hidden game, a collection or a state set by hand on either side stays on both.
    /// </summary>
    public static class SyncMerge
    {
        public const string Favorite = "fav";
        public const string Hidden = "hidden";
        public const string Status = "status";
        public const string Collections = "cats";
        public const string Score = "score";
        public static readonly string[] Fields = { Favorite, Hidden, Status, Collections, Score };
        public static readonly DateTime Initial = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The states the library sets by itself (not played, played) travel with this mark in front: they never beat
        /// one set by hand, and the library moving from one to another isn't a change made by the person.
        /// </summary>
        public const string Automatic = "~";

        public static bool IsAutomatic(string status) => status.IsNullOrEmpty() || status.StartsWith(Automatic, StringComparison.Ordinal);

        public static string StatusName(string status) => status == null ? string.Empty : status.StartsWith(Automatic, StringComparison.Ordinal) ? status.Substring(1) : status;

        public static bool IsDefault(string field, string value)
        {
            if (field == Status)
            {
                return IsAutomatic(value);
            }

            return value.IsNullOrEmpty() || ((field == Favorite || field == Hidden) && value == "0");
        }

        private static List<string> Names(string value)
        {
            return (value ?? string.Empty).Split('\n').Where(a => !a.IsNullOrEmpty()).ToList();
        }

        /// <summary>The names of states and collections are the same whatever their capitals, like in the library.</summary>
        public static bool Same(string field, string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            switch (field)
            {
                case Status:
                    // Two automatic ones are the same thing: what the library would set by itself.
                    return (IsAutomatic(a) && IsAutomatic(b)) || string.Equals(StatusName(a), StatusName(b), StringComparison.OrdinalIgnoreCase);
                case Collections:
                    var left = new HashSet<string>(Names(a), StringComparer.OrdinalIgnoreCase);
                    return left.SetEquals(Names(b));
                default:
                    return a == b;
            }
        }

        /// <summary>The same for both sides, whatever the order: that way two computers end up equal.</summary>
        public static string Tie(string field, string a, string b)
        {
            a = a ?? string.Empty;
            b = b ?? string.Empty;
            switch (field)
            {
                case Favorite:
                case Hidden:
                    return a == "1" || b == "1" ? "1" : "0";
                case Collections:
                    var names = new List<string>();
                    foreach (var name in Names(a).Concat(Names(b)).OrderBy(c => c, StringComparer.Ordinal))
                    {
                        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            names.Add(name);
                        }
                    }

                    return string.Join("\n", names.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ThenBy(c => c, StringComparer.Ordinal));
                case Score:
                    if (int.TryParse(a, out var x) && int.TryParse(b, out var y))
                    {
                        return Math.Max(x, y).ToString();
                    }

                    return a.IsNullOrEmpty() ? b : a;
                default:
                    // A state set by hand always beats an automatic one, or none.
                    if (IsAutomatic(a) && !IsAutomatic(b))
                    {
                        return b;
                    }

                    if (IsAutomatic(b) && !IsAutomatic(a))
                    {
                        return a;
                    }

                    return string.CompareOrdinal(a, b) >= 0 ? a : b;
            }
        }

        private static SyncValue Better(string field, SyncValue a, SyncValue b)
        {
            if (a == null)
            {
                return b?.Copy();
            }

            if (b == null || a.T > b.T)
            {
                return a;
            }

            if (b.T > a.T)
            {
                return b.Copy();
            }

            return Same(field, a.V, b.V) ? a : new SyncValue { V = Tie(field, a.V, b.V), T = a.T };
        }

        /// <param name="local">The values of each game of this library now, by key.</param>
        /// <param name="snapshot">What this computer had after the last time.</param>
        /// <param name="wasLocal">The keys whose game was already this same game of the library the last time.</param>
        /// <param name="remotes">The values of the other computers.</param>
        public static SyncPlan Plan(
            Dictionary<string, Dictionary<string, string>> local,
            Dictionary<string, Dictionary<string, SyncValue>> snapshot,
            ICollection<string> wasLocal,
            IEnumerable<SyncMachineFile> remotes,
            DateTime nowUtc)
        {
            var plan = new SyncPlan { Snapshot = new Dictionary<string, Dictionary<string, SyncValue>>() };
            var known = new HashSet<string>(wasLocal ?? new List<string>());
            snapshot = snapshot ?? new Dictionary<string, Dictionary<string, SyncValue>>();
            var others = (remotes ?? Enumerable.Empty<SyncMachineFile>()).Where(a => a?.Games != null).ToList();
            var keys = new HashSet<string>(local.Keys);
            keys.UnionWith(snapshot.Keys);
            foreach (var other in others)
            {
                keys.UnionWith(other.Games.Keys);
            }

            foreach (var key in keys)
            {
                local.TryGetValue(key, out var localValues);
                snapshot.TryGetValue(key, out var saved);
                var result = new Dictionary<string, SyncValue>();
                foreach (var field in Fields)
                {
                    SyncValue current = null;
                    if (saved != null && saved.TryGetValue(field, out var before) && before != null)
                    {
                        current = before.Copy();
                    }

                    string here = null;
                    if (localValues != null && localValues.TryGetValue(field, out here))
                    {
                        here = here ?? string.Empty;
                        if (known.Contains(key) && current != null)
                        {
                            if (!Same(field, here, current.V))
                            {
                                // Changed here since the last time.
                                current = new SyncValue { V = here, T = nowUtc };
                            }
                        }
                        else
                        {
                            // A game new to this library brings what it had, without a date.
                            current = Better(field, current, new SyncValue { V = here, T = Initial });
                        }
                    }

                    foreach (var other in others)
                    {
                        if (other.Games.TryGetValue(key, out var theirs) && theirs != null && theirs.TryGetValue(field, out var value) && value != null)
                        {
                            current = Better(field, current, new SyncValue { V = value.V ?? string.Empty, T = value.T });
                        }
                    }

                    if (current == null)
                    {
                        continue;
                    }

                    result[field] = current;
                    if (here != null && !Same(field, current.V, here))
                    {
                        plan.Changes.Add(new SyncChange { Key = key, Field = field, Value = current.V });
                    }
                }

                if (result.Count > 0)
                {
                    plan.Snapshot[key] = result;
                }
            }

            return plan;
        }

        /// <summary>Only what says something: changed values and the ones that aren't the default.</summary>
        public static Dictionary<string, Dictionary<string, SyncValue>> ToExport(Dictionary<string, Dictionary<string, SyncValue>> snapshot)
        {
            var result = new Dictionary<string, Dictionary<string, SyncValue>>();
            // Always in the same order, so that the same values make the same file.
            foreach (var pair in snapshot.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                var fields = pair.Value.Where(a => a.Value != null && (a.Value.T > Initial || !IsDefault(a.Key, a.Value.V))).ToDictionary(a => a.Key, a => a.Value);
                if (fields.Count > 0)
                {
                    result[pair.Key] = fields;
                }
            }

            return result;
        }

        public static string SessionId(string machine, SyncSession session)
        {
            return machine + "|" + session.Game + "|" + PlayHistory.ToUtc(session.Start).Ticks;
        }

        /// <summary>Older sittings are not news anymore: the history forgets them too.</summary>
        public static DateTime ForgetBefore(DateTime nowUtc) => nowUtc - PlayHistory.KeepFor - TimeSpan.FromDays(10);
    }

    /// <summary>
    /// The library of Atrio on several computers, through a folder that the person chooses and that something
    /// else keeps in sync (OneDrive, Google Drive, Dropbox, a folder of the network). Each computer writes only
    /// its own file there and reads the others: favorites, hidden games, state of each game, collections and
    /// score, and the time played since both synchronize. No account is involved.
    /// </summary>
    public static class PcSync
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string StateFileName = "atrio-sincro.json";
        public const string FolderName = "Atrio";
        private static readonly object sync = new object();
        private static System.Threading.Timer timer;
        private static Func<IGameDatabaseMain> database;
        private static Func<string> folder;
        private static Action<Action> onWindow;
        private static int running;

        public static event EventHandler Changed;

        /// <summary>The last time it went well, and the computers seen then. Null before the first time.</summary>
        public static DateTime? LastSync { get; private set; }
        public static int OtherComputers { get; private set; }
        public static string LastError { get; private set; }

        private static string StatePath => Path.Combine(PlaynitePaths.ConfigRootPath, StateFileName);

        public static string GetMachinesFolder(string root) => Path.Combine(root, FolderName, "equipos");

        public static string GetSavesFolder(string root) => Path.Combine(root, FolderName, "Copias de partidas");

        /// <summary>
        /// The same game on any computer: its store and its id there. Games added by hand go by their name and
        /// their platform, which is all they share.
        /// </summary>
        public static string GetKey(Game game)
        {
            if (game == null)
            {
                return null;
            }

            if (game.PluginId != Guid.Empty && !game.GameId.IsNullOrEmpty())
            {
                return game.PluginId.ToString("N") + ":" + game.GameId;
            }

            var name = GameCaptures.Normalize(game.Name ?? string.Empty);
            if (name.IsNullOrWhiteSpace())
            {
                return null;
            }

            var platform = game.PlatformIds?.Count > 0 ? game.Platforms?.FirstOrDefault()?.Name : null;
            return "custom:" + name + (platform.IsNullOrWhiteSpace() ? string.Empty : "|" + GameCaptures.Normalize(platform));
        }

        /// <summary>The states the library sets by itself (not played yet, played), or none.</summary>
        private static bool IsAutomaticStatus(Guid id, CompletionStatusSettings settings)
        {
            return id == Guid.Empty || (settings != null && (id == settings.DefaultStatus || id == settings.PlayedStatus));
        }

        /// <summary>The name of the state of the game, with the mark of the automatic ones; nothing without a state.</summary>
        public static string ReadStatus(Guid id, string name, CompletionStatusSettings settings)
        {
            if (id == Guid.Empty || name.IsNullOrEmpty())
            {
                return string.Empty;
            }

            return IsAutomaticStatus(id, settings) ? SyncMerge.Automatic + name : name;
        }

        public static Dictionary<string, string> ReadValues(Game game, IGameDatabaseMain db, CompletionStatusSettings statuses)
        {
            var categories = game.CategoryIds?.Select(a => db.Categories.Get(a)?.Name).Where(a => !a.IsNullOrEmpty()).Distinct().OrderBy(a => a, StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            var status = game.CompletionStatusId == Guid.Empty ? null : db.CompletionStatuses.Get(game.CompletionStatusId)?.Name;
            return new Dictionary<string, string>
            {
                [SyncMerge.Favorite] = game.Favorite ? "1" : "0",
                [SyncMerge.Hidden] = game.Hidden ? "1" : "0",
                [SyncMerge.Status] = ReadStatus(game.CompletionStatusId, status, statuses),
                [SyncMerge.Collections] = string.Join("\n", categories),
                [SyncMerge.Score] = game.UserScore?.ToString() ?? string.Empty
            };
        }

        /// <summary>Changes a value of a game. False when the value can't be used here.</summary>
        public static bool ApplyValue(Game game, string field, string value, IGameDatabaseMain db, CompletionStatusSettings statuses)
        {
            switch (field)
            {
                case SyncMerge.Favorite:
                    game.Favorite = value == "1";
                    return true;
                case SyncMerge.Hidden:
                    game.Hidden = value == "1";
                    return true;
                case SyncMerge.Status:
                    // Automatic there and here: the library of each computer sets its own.
                    if (SyncMerge.IsAutomatic(value) && IsAutomaticStatus(game.CompletionStatusId, statuses))
                    {
                        return true;
                    }

                    // The person took the state away there.
                    if (value.IsNullOrEmpty())
                    {
                        game.CompletionStatusId = Guid.Empty;
                        return true;
                    }

                    // The same state by its name; one made on another computer is made here too, like the collections.
                    var statusName = SyncMerge.StatusName(value);
                    var status = db.CompletionStatuses.FirstOrDefault(a => string.Equals(a.Name, statusName, StringComparison.OrdinalIgnoreCase)) ?? db.CompletionStatuses.Add(statusName);
                    game.CompletionStatusId = status.Id;
                    return true;
                case SyncMerge.Collections:
                    var ids = new List<Guid>();
                    foreach (var name in (value ?? string.Empty).Split('\n').Where(a => !a.IsNullOrEmpty()))
                    {
                        var category = db.Categories.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? db.Categories.Add(name);
                        ids.Add(category.Id);
                    }

                    game.CategoryIds = ids.Count > 0 ? ids.Distinct().ToList() : null;
                    return true;
                case SyncMerge.Score:
                    if (value.IsNullOrEmpty())
                    {
                        game.UserScore = null;
                        return true;
                    }

                    if (!int.TryParse(value, out var score))
                    {
                        return false;
                    }

                    game.UserScore = score;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A fingerprint of this Windows, made from the number Windows gives itself when it's installed.</summary>
        public static string GetComputerPrint()
        {
            var test = AtrioInfo.GetTestSetting("ATRIO_SINCRO_EQUIPO");
            if (!test.IsNullOrEmpty())
            {
                return test;
            }

            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    var guid = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
                    using (var sha = SHA256.Create())
                    {
                        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("atrio:" + guid))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to read the number of this Windows.");
                return Environment.MachineName;
            }
        }

        /// <summary>Null when the file is there but can't be read: then nothing synchronizes, rather than starting over.</summary>
        private static SyncState LoadState()
        {
            SyncState state = null;
            if (File.Exists(StatePath))
            {
                try
                {
                    state = Playnite.SDK.Data.Serialization.FromJsonFile<SyncState>(StatePath);
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to read the state of the synchronization.");
                    return null;
                }

                if (state == null)
                {
                    return null;
                }
            }

            state = state ?? new SyncState();
            state.Snapshot = state.Snapshot ?? new Dictionary<string, Dictionary<string, SyncValue>>();
            state.LocalIds = state.LocalIds ?? new Dictionary<string, Guid>();
            state.Applied = state.Applied ?? new Dictionary<string, AppliedSitting>();
            return state;
        }

        private const string StampFileName = "atrio-sincro-marca.txt";

        /// <summary>The mark in the folder of the library: it goes into its backups, unlike the rest of this.</summary>
        private static string GetStampPath(IGameDatabaseMain db)
        {
            var folder = (db as GameDatabase)?.DatabasePath;
            return folder.IsNullOrEmpty() ? null : Path.Combine(folder, StampFileName);
        }

        private static long ReadStamp(IGameDatabaseMain db)
        {
            try
            {
                var path = GetStampPath(db);
                return path != null && File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var stamp) ? stamp : 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static void WriteStamp(IGameDatabaseMain db, long stamp)
        {
            var path = GetStampPath(db);
            if (path != null)
            {
                File.WriteAllText(path, stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// The library came back from a backup (its mark is older than this computer remembers): its values have no
        /// date anymore, so the ones dated in the others win, and the time of the sittings of others added after the
        /// backup goes in again, since the backup doesn't have it. True if it was so.
        /// </summary>
        private static bool CheckRestored(SyncState state, IGameDatabaseMain db)
        {
            if (state.LibraryStamp == 0)
            {
                return false;
            }

            var stamp = ReadStamp(db);
            if (stamp == state.LibraryStamp)
            {
                return false;
            }

            var backup = stamp > 0 ? new DateTime(stamp, DateTimeKind.Utc) : DateTime.MinValue;
            logger.Info($"The library came back from a backup of {backup:u}: it synchronizes as it is now.");
            state.LocalIds = new Dictionary<string, Guid>();
            var again = state.Applied.Where(a => a.Value.At > backup).ToList();
            PlayHistory.RemoveRemote(again.Select(a => new PlaySession { Machine = a.Value.Machine, GameId = a.Value.GameId, Start = a.Value.Start }));
            foreach (var pair in again)
            {
                state.Applied.Remove(pair.Key);
            }

            state.LibraryStamp = stamp;
            return true;
        }

        /// <summary>A whole new file first, then it takes the place of the old one: a cut never leaves it half written.</summary>
        private static void SaveState(SyncState state)
        {
            var temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, Playnite.SDK.Data.Serialization.ToJson(state));
            if (File.Exists(StatePath))
            {
                File.Replace(temporary, StatePath, null);
            }
            else
            {
                File.Move(temporary, StatePath);
            }
        }

        /// <summary>
        /// The same folder of Atrio on another computer (it's portable) has to be another computer for the others:
        /// it gets its own name, and what was played on the first one stays as the first one's. True if it changed.
        /// </summary>
        private static bool CheckComputer(SyncState state, IGameDatabaseMain db)
        {
            var print = GetComputerPrint();
            if (state.Machine.IsNullOrEmpty())
            {
                state.Machine = Guid.NewGuid().ToString("N");
                state.Computer = print;

                // Starting from nothing with sittings of others in the history means this memory was lost: what the
                // history has from them is already in the library.
                foreach (var session in PlayHistory.GetSessions(DateTime.MinValue, DateTime.MaxValue).Where(a => a.Machine != null))
                {
                    var key = GetKey(db?.Games.Get(session.GameId));
                    if (key != null)
                    {
                        state.Applied[SyncMerge.SessionId(session.Machine, new SyncSession { Game = key, Start = session.Start })] =
                            new AppliedSitting { Machine = session.Machine, GameId = session.GameId, Start = session.Start, At = DateTime.UtcNow };
                    }
                }

                return true;
            }

            if (state.Computer == print)
            {
                return false;
            }

            logger.Info("The folder of Atrio is on another computer now: it synchronizes as a new one.");
            var old = state.Machine;
            PlayHistory.GiveLocalTo(old);

            // Everything of the first computer is already here: its sittings never count again. Done again from the
            // history, it doesn't matter if a first attempt stopped halfway.
            foreach (var session in PlayHistory.GetSessions(DateTime.MinValue, DateTime.MaxValue).Where(a => a.Machine == old))
            {
                var key = GetKey(db?.Games.Get(session.GameId));
                if (key != null)
                {
                    state.Applied[SyncMerge.SessionId(old, new SyncSession { Game = key, Start = session.Start })] =
                        new AppliedSitting { Machine = old, GameId = session.GameId, Start = session.Start, At = DateTime.UtcNow };
                }
            }

            state.Machine = Guid.NewGuid().ToString("N");
            state.Computer = print;
            state.FirstSync = DateTime.UtcNow;
            state.LocalIds = new Dictionary<string, Guid>();
            return true;
        }

        private static List<SyncMachineFile> ReadOthers(string machines, string self, string print)
        {
            var result = new List<SyncMachineFile>();
            foreach (var file in Directory.GetFiles(machines, "*.json"))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var other = Playnite.SDK.Data.Serialization.FromJsonFile<SyncMachineFile>(file);
                    if (other?.Machine != null && other.Machine != self && other.Format == 1 && other.Computer != print)
                    {
                        result.Add(other);
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    // The cloud can leave a file half written for a moment: it's read again next time.
                    logger.Debug(e, $"Failed to read {file}.");
                }
            }

            return result;
        }

        private static string Fingerprint(SyncMachineFile own)
        {
            using (var sha = SHA256.Create())
            {
                var text = Playnite.SDK.Data.Serialization.ToJson(new { own.Machine, own.Computer, own.Games, own.Sessions });
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }

        private static void WriteOwn(string machines, SyncMachineFile own)
        {
            var path = Path.Combine(machines, own.Machine + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Playnite.SDK.Data.Serialization.ToJson(own));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        /// <summary>The store brings the time itself on every update of the library: added here too, it would count twice.</summary>
        private static bool StoreCountsTime(Game game)
        {
            return game.PluginId == SteamContent.SteamPluginId &&
                (PlayniteApplication.Current?.AppSettings?.PlaytimeImportMode ?? PlaytimeImportMode.Always) == PlaytimeImportMode.Always;
        }

        /// <summary>Atrio is closing: a round of the timer stops before touching anything, the last one goes on.</summary>
        private static volatile bool closing;

        /// <summary>One synchronization: read the others, bring what changed, and tell them what changed here.</summary>
        public static void RunOnce()
        {
            RunOnce(false);
        }

        private static void RunOnce(bool final)
        {
            var root = folder?.Invoke();
            var db = database?.Invoke();
            if (root.IsNullOrWhiteSpace() || db?.Games == null || onWindow == null || Interlocked.Exchange(ref running, 1) == 1)
            {
                return;
            }

            try
            {
                if (!Directory.Exists(root))
                {
                    LastError = string.Format(ResourceProvider.GetString("LOCAtrioSyncNoFolder"), root);
                    return;
                }

                var machines = GetMachinesFolder(root);
                Directory.CreateDirectory(machines);
                SyncState state;
                lock (sync)
                {
                    state = LoadState();
                    if (state == null)
                    {
                        LastError = string.Format(ResourceProvider.GetString("LOCAtrioSyncBrokenState"), StatePath);
                        return;
                    }

                    var changed = CheckComputer(state, db);
                    changed |= CheckRestored(state, db);
                    if (state.FirstSync == null)
                    {
                        state.FirstSync = DateTime.UtcNow;
                        changed = true;
                    }

                    if (changed)
                    {
                        SaveState(state);
                    }
                }

                var print = state.Computer;
                var others = ReadOthers(machines, state.Machine, print);

                // The library is read and changed from the thread of the window, like everything else does.
                Dictionary<string, List<Game>> byKey = null;
                Dictionary<string, Dictionary<string, string>> local = null;
                Dictionary<string, Guid> ids = null;
                CompletionStatusSettings statuses = null;
                onWindow(() =>
                {
                    statuses = db.GetCompletionStatusSettings();
                    byKey = new Dictionary<string, List<Game>>();
                    foreach (var game in db.Games)
                    {
                        var key = GetKey(game);
                        if (key == null)
                        {
                            continue;
                        }

                        if (!byKey.TryGetValue(key, out var list))
                        {
                            byKey[key] = list = new List<Game>();
                        }

                        list.Add(game);
                    }

                    // A key with two games of this library (two games by hand with the same name) can't be told apart: out.
                    foreach (var key in byKey.Where(a => a.Value.Count > 1).Select(a => a.Key).ToList())
                    {
                        byKey.Remove(key);
                    }

                    local = byKey.ToDictionary(a => a.Key, a => ReadValues(a.Value[0], db, statuses));
                    ids = byKey.ToDictionary(a => a.Key, a => a.Value[0].Id);
                });

                var now = DateTime.UtcNow;
                var wasLocal = new HashSet<string>(ids.Where(a => state.LocalIds.TryGetValue(a.Key, out var id) && id == a.Value).Select(a => a.Key));
                var plan = SyncMerge.Plan(local, state.Snapshot, wasLocal, others, now);

                // The sittings of the others that aren't here yet, for the games this library has, and not too old.
                var forget = SyncMerge.ForgetBefore(now);
                var sittings = new Dictionary<string, PlaySession>();
                foreach (var other in others)
                {
                    foreach (var session in other.Sessions ?? new List<SyncSession>())
                    {
                        var id = SyncMerge.SessionId(other.Machine, session);
                        if (session.Seconds > 0 && PlayHistory.ToUtc(session.Start) >= forget && !state.Applied.ContainsKey(id) &&
                            ids.TryGetValue(session.Game ?? string.Empty, out var gameId) && !sittings.ContainsKey(id))
                        {
                            sittings[id] = new PlaySession
                            {
                                GameId = gameId,
                                Start = session.Start,
                                End = session.End,
                                Seconds = session.Seconds,
                                Outside = session.Outside,
                                Machine = other.Machine
                            };
                        }
                    }
                }

                // Closing: the last round does what's left. A round of the timer stops here, before touching anything.
                if (closing && !final)
                {
                    return;
                }

                // Into the history all at once, out of the thread of the window. Their time goes to the library now: the
                // new ones and the ones a round cut in half had put in the history without it.
                var added = sittings.Count > 0 ? PlayHistory.AddRemoteMany(sittings.Values) : new List<PlaySession>();
                var failed = new List<SyncChange>();
                if (closing && !final)
                {
                    return;
                }

                if (plan.Changes.Count > 0 || added.Count > 0)
                {
                    onWindow(() =>
                    {
                        using (db.BufferedUpdate())
                        {
                            foreach (var group in plan.Changes.GroupBy(a => a.Key))
                            {
                                var game = db.Games.Get(ids[group.Key]);
                                if (game == null)
                                {
                                    failed.AddRange(group);
                                    continue;
                                }

                                var touched = false;
                                foreach (var change in group)
                                {
                                    if (ApplyValue(game, change.Field, change.Value, db, statuses))
                                    {
                                        touched = true;
                                    }
                                    else
                                    {
                                        failed.Add(change);
                                    }
                                }

                                if (touched)
                                {
                                    db.Games.Update(game);
                                }
                            }

                            foreach (var group in added.GroupBy(a => a.GameId))
                            {
                                var game = db.Games.Get(group.Key);
                                if (game == null)
                                {
                                    continue;
                                }

                                if (!StoreCountsTime(game))
                                {
                                    game.Playtime += (ulong)Math.Max(0, Math.Round(group.Sum(a => a.Seconds)));
                                }

                                game.PlayCount += (ulong)group.Count();
                                var end = group.Max(a => PlayHistory.ToUtc(a.End)).ToLocalTime();
                                if (game.LastActivity == null || game.LastActivity < end)
                                {
                                    game.LastActivity = end;
                                }

                                db.Games.Update(game);
                            }
                        }
                    });
                }

                // What couldn't be used here stays as it is here, with its old date: otherwise it would look like a change made here.
                foreach (var change in failed)
                {
                    SyncValue previous = null;
                    if (state.Snapshot.TryGetValue(change.Key, out var before))
                    {
                        before.TryGetValue(change.Field, out previous);
                    }

                    var value = previous?.Copy() ?? new SyncValue { V = local[change.Key][change.Field], T = SyncMerge.Initial };
                    if (plan.Snapshot.TryGetValue(change.Key, out var fields))
                    {
                        fields[change.Field] = value;
                    }
                }

                // This computer's memory is kept before anything goes to the shared folder: if that fails, nothing is counted
                // twice. The mark of the library goes first: a cut between the two only makes values lose their dates.
                lock (sync)
                {
                    state.Snapshot = plan.Snapshot;
                    state.LocalIds = ids;
                    foreach (var pair in sittings)
                    {
                        state.Applied[pair.Key] = new AppliedSitting
                        {
                            Machine = pair.Value.Machine,
                            GameId = pair.Value.GameId,
                            Start = PlayHistory.ToUtc(pair.Value.Start),
                            At = now
                        };
                    }

                    foreach (var old in state.Applied.Where(a => a.Value.Start < forget).Select(a => a.Key).ToList())
                    {
                        state.Applied.Remove(old);
                    }

                    state.LastSync = now;
                    var stamp = now.Ticks;
                    WriteStamp(db, stamp);
                    state.LibraryStamp = stamp;
                    SaveState(state);
                }

                // What changed here and the sittings played here since the first time, for the others.
                var own = new SyncMachineFile
                {
                    Machine = state.Machine,
                    Computer = print,
                    Name = Environment.MachineName,
                    Updated = now,
                    Games = SyncMerge.ToExport(plan.Snapshot)
                };

                var keyOf = ids.ToDictionary(a => a.Value, a => a.Key);
                var keep = now - PlayHistory.KeepFor;
                foreach (var session in PlayHistory.GetSessions(state.FirstSync.Value, DateTime.MaxValue).Where(a => a.Machine == null && a.End >= keep))
                {
                    if (keyOf.TryGetValue(session.GameId, out var key))
                    {
                        own.Sessions.Add(new SyncSession { Game = key, Start = session.Start, End = session.End, Seconds = session.Seconds, Outside = session.Outside });
                    }
                }

                // The date of the file doesn't count: without changes, the cloud doesn't upload it again every ten minutes.
                var written = Fingerprint(own);
                if (written != state.Written || !File.Exists(Path.Combine(machines, own.Machine + ".json")))
                {
                    WriteOwn(machines, own);
                    lock (sync)
                    {
                        state.Written = written;
                        SaveState(state);
                    }
                }

                LastSync = now;
                OtherComputers = others.Count;
                LastError = null;
                if (plan.Changes.Count > 0 || added.Count > 0)
                {
                    logger.Info($"Synchronized with {others.Count} other computers: {plan.Changes.Count - failed.Count} values, {added.Count} sittings.");
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "The synchronization failed.");
                LastError = e.Message;
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// A minute after the start, every ten minutes, and when Atrio closes. If the folder of Atrio was copied from
        /// another computer, it becomes a new computer right away, before anything is played here.
        /// </summary>
        public static void Attach(Func<IGameDatabaseMain> db, Func<string> sharedFolder, Action<Action> runOnWindow)
        {
            database = db;
            folder = sharedFolder;
            onWindow = runOnWindow;
            try
            {
                lock (sync)
                {
                    if (File.Exists(StatePath))
                    {
                        var state = LoadState();
                        if (state?.Machine != null && state.Computer != GetComputerPrint() && CheckComputer(state, db?.Invoke()))
                        {
                            SaveState(state);
                        }
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to check the computer of the synchronization.");
            }

            if (timer == null)
            {
                timer = new System.Threading.Timer(_ => RunOnce(), null, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));
            }
        }

        /// <summary>Right now, out of turn: when the folder is chosen, or with the button.</summary>
        public static void RunSoon()
        {
            timer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        }

        /// <summary>
        /// The last one, so the others get what was just played. Only if the folder answered the last time: a folder
        /// of the network that is off would keep Atrio from closing.
        /// </summary>
        public static void Shutdown()
        {
            // The timer stops whatever happens: a round of it in the middle of closing would find the window gone.
            closing = true;
            timer?.Dispose();
            timer = null;
            if (folder?.Invoke().IsNullOrWhiteSpace() == false && LastSync != null && LastError == null)
            {
                RunOnce(true);
            }
        }
    }
}
