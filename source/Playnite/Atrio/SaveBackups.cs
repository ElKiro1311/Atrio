using Newtonsoft.Json.Linq;
using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>A copy of the saves of a game, as Ludusavi lists it.</summary>
    public class SaveBackup
    {
        public string Name { get; set; }
        public DateTime When { get; set; }
    }

    /// <summary>What Atrio knows of the copies of one game.</summary>
    public class SaveBackupInfo
    {
        /// <summary>The name of the game in the list of Ludusavi; empty when Ludusavi doesn't know it.</summary>
        public string Title { get; set; }
        public DateTime? LastBackup { get; set; }
        public long Bytes { get; set; }

        /// <summary>Ludusavi knows the game but found no saves of it on this computer.</summary>
        public bool NoSaves { get; set; }

        /// <summary>Ludusavi couldn't copy every file this time: one in use, without permission, or only online.</summary>
        public bool Failed { get; set; }
    }

    /// <summary>How a restore went.</summary>
    public enum SaveRestoreResult
    {
        /// <summary>The saves are as they were in the copy.</summary>
        Done,

        /// <summary>Ludusavi didn't finish well: some files may have changed. The copy made first is kept.</summary>
        Partly,

        /// <summary>The current saves couldn't all be copied first, so nothing was touched.</summary>
        NoSafetyCopy,

        /// <summary>Nothing was touched: the copy isn't there anymore, or Ludusavi couldn't tell.</summary>
        Nothing
    }

    /// <summary>
    /// Ludusavi, the open source program (MIT) that knows where thousands of games keep their saves. Atrio downloads
    /// one exact version from its page on GitHub, checks it, and keeps it with its own settings in its folder.
    /// </summary>
    public static class Ludusavi
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string Version = "v0.31.0";
        private const string Win64 = "https://github.com/mtkennerly/ludusavi/releases/download/v0.31.0/ludusavi-v0.31.0-win64.zip";
        private const string Win64Hash = "f47a8ad8c708f01d2eb124704973beffab205e292f5287a10fc4a101f8d68706";
        private const string Win32 = "https://github.com/mtkennerly/ludusavi/releases/download/v0.31.0/ludusavi-v0.31.0-win32.zip";
        private const string Win32Hash = "2f009245801ff1da0f7bb5d10253e1e4a2677b2a063cfdf3297775955622419c";
        private static readonly SemaphoreSlim installing = new SemaphoreSlim(1, 1);

        /// <summary>Tests point it somewhere else.</summary>
        public static string Root { get; set; }

        public static string Folder => Path.Combine(Root ?? PlaynitePaths.ConfigRootPath, "Herramientas", "Ludusavi");
        public static string Executable => Path.Combine(Folder, "ludusavi.exe");
        public static string ConfigFolder => Path.Combine(Folder, "config");
        public static string BackupFolder => SharedFolder?.Invoke() ?? Path.Combine(Root ?? PlaynitePaths.ConfigRootPath, "Copias de partidas");

        /// <summary>The folder shared with the other computers, when the person wants the copies there too.</summary>
        public static Func<string> SharedFolder { get; set; }
        public static bool IsInstalled => File.Exists(Executable);

        public static bool HashMatches(byte[] data, string expected)
        {
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty);
                return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Downloads Ludusavi if it isn't there yet. False if it can't, and nothing is left half done.</summary>
        public static async Task<bool> InstallAsync(CancellationToken token)
        {
            if (IsInstalled)
            {
                return true;
            }

            await installing.WaitAsync(token);
            try
            {
                if (IsInstalled)
                {
                    return true;
                }

                var url = Environment.Is64BitOperatingSystem ? Win64 : Win32;
                var expected = Environment.Is64BitOperatingSystem ? Win64Hash : Win32Hash;
                byte[] data;
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atrio");
                    data = await client.GetByteArrayAsync(url);
                }

                if (!HashMatches(data, expected))
                {
                    logger.Error($"Ludusavi {Version} didn't match its published hash: not used.");
                    return false;
                }

                Directory.CreateDirectory(Folder);
                var temporary = Executable + ".tmp";
                using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                {
                    var entry = zip.Entries.FirstOrDefault(a => string.Equals(a.Name, "ludusavi.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        logger.Error("The download of Ludusavi has no ludusavi.exe.");
                        return false;
                    }

                    using (var source = entry.Open())
                    using (var target = File.Create(temporary))
                    {
                        await source.CopyToAsync(target);
                    }
                }

                File.Move(temporary, Executable);
                logger.Info($"Ludusavi {Version} downloaded to {Folder}.");
                return true;
            }
            catch (Exception e) when (!(e is OperationCanceledException) || !token.IsCancellationRequested)
            {
                // The download that stops for good (the network goes, the computer sleeps) ends the same way:
                // HttpClient says it with a cancel of its own after its five minutes.
                logger.Error(e, "Failed to download Ludusavi.");
                return false;
            }
            finally
            {
                installing.Release();
            }
        }

        /// <summary>
        /// One argument as Windows splits them (CommandLineToArgvW): in quotes, with its quotes escaped and the
        /// backslashes before them doubled.
        /// </summary>
        public static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value ?? string.Empty)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }

                text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                text.Append(c);
                slashes = 0;
            }

            text.Append('\\', slashes * 2);
            text.Append('"');
            return text.ToString();
        }

        /// <summary>
        /// The names of the games, at the end and after "--", so none can be read as an option. They don't go
        /// through the input: Windows can put a mark of UTF-8 (BOM) before the first one, and then Ludusavi doesn't
        /// know that game.
        /// </summary>
        public static string NamesArguments(IEnumerable<string> names)
        {
            var list = names?.Where(a => !string.IsNullOrEmpty(a)).ToList() ?? new List<string>();
            return list.Count == 0 ? string.Empty : " -- " + string.Join(" ", list.Select(Quote));
        }

        /// <summary>
        /// Runs Ludusavi without a window, with the names of the games at the end. Null if it gave no answer. Names
        /// null for a command without them; a command for names never runs without one, because Ludusavi would
        /// take every game it knows.
        /// </summary>
        public static async Task<JObject> RunAsync(string arguments, IEnumerable<string> names, TimeSpan timeout, CancellationToken token)
        {
            if (!IsInstalled || (names != null && !names.Any(a => !string.IsNullOrEmpty(a))))
            {
                return null;
            }

            var info = new ProcessStartInfo(Executable, $"--config {Quote(ConfigFolder)} --try-manifest-update {arguments}{NamesArguments(names)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Folder
            };

            using (var process = new Process { StartInfo = info })
            {
                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    // An antivirus, Smart App Control or a rule of the system can keep it from starting.
                    logger.Error(e, "Ludusavi couldn't start.");
                    return null;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    // Its input closes empty: without names it would wait for them there.
                    process.StandardInput.Close();
                }
                catch (IOException e)
                {
                    logger.Debug($"Ludusavi closed before its input: {e.Message}");
                }

                var finished = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds), token);
                if (!finished)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                    {
                    }

                    logger.Warn($"Ludusavi took too long with: {arguments}");
                    return null;
                }

                var text = await output;
                var problems = await errors;
                if (!string.IsNullOrWhiteSpace(problems))
                {
                    logger.Debug($"Ludusavi said: {problems.Trim()}");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                try
                {
                    return JObject.Parse(text);
                }
                catch (Newtonsoft.Json.JsonException e)
                {
                    logger.Warn(e, "Ludusavi gave an answer that isn't JSON.");
                    return null;
                }
            }
        }

        /// <summary>
        /// The title Ludusavi uses for the game: by its number of Steam or GOG, by the exact name, or by an approximation
        /// that only differs in capitals, accents and signs. An approximation that leaves out a year or an edition
        /// ("Tomb Raider" for "Tomb Raider (2013)") could be another game, so it isn't taken.
        /// </summary>
        public static async Task<string> FindTitleAsync(Game game, CancellationToken token)
        {
            var timeout = TimeSpan.FromMinutes(3);
            var tries = new List<Tuple<string, string[], bool>>();
            if (game.PluginId == SteamContent.SteamPluginId && int.TryParse(game.GameId, out var steamId))
            {
                tries.Add(Tuple.Create($"find --api --steam-id {steamId}", (string[])null, false));
            }

            if (game.PluginId == GogPluginId && long.TryParse(game.GameId, out var gogId))
            {
                tries.Add(Tuple.Create($"find --api --gog-id {gogId}", (string[])null, false));
            }

            if (!string.IsNullOrWhiteSpace(game.Name))
            {
                tries.Add(Tuple.Create("find --api", new[] { game.Name }, false));
                tries.Add(Tuple.Create("find --api --normalized", new[] { game.Name }, true));
            }
            foreach (var attempt in tries)
            {
                var answer = await RunAsync(attempt.Item1, attempt.Item2, timeout, token);
                var titles = (answer?["games"] as JObject)?.Properties().Select(a => a.Name).Where(a => !string.IsNullOrEmpty(a)).ToList() ?? new List<string>();
                if (attempt.Item3)
                {
                    titles = titles.Where(a => SameTitle(a, game.Name)).ToList();
                    if (titles.Count != 1)
                    {
                        if (titles.Count > 1)
                        {
                            logger.Info($"Ludusavi has several games like {game.Name}: none is taken.");
                        }

                        continue;
                    }
                }

                if (titles.Count > 0)
                {
                    return titles[0];
                }
            }

            return null;
        }

        /// <summary>The same title but for capitals, accents, spaces and signs like ™, ® or ':'.</summary>
        public static bool SameTitle(string a, string b)
        {
            string key(string name) => GameCaptures.Normalize((name ?? string.Empty).Replace("&", " and ")).Replace(" ", string.Empty);
            var first = key(a);
            return first.Length > 0 && first == key(b);
        }

        /// <summary>How every copy is made: zip files in Atrio's folder, two full copies and four of changes after each.</summary>
        public static string BackupArguments => $"backup --force --path {Quote(BackupFolder)} --format zip --full-limit 2 --differential-limit 4";

        /// <summary>
        /// Starts a copy of those games and leaves it alone: it goes on after Atrio closes. Never without names,
        /// which would copy every game Ludusavi knows. False if it couldn't start.
        /// </summary>
        public static bool StartBackupAlone(IEnumerable<string> titles)
        {
            var names = titles?.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList() ?? new List<string>();
            if (!IsInstalled || names.Count == 0)
            {
                return false;
            }

            // Nothing of it goes through Atrio: what it says stays in a console of its own, without a window.
            var info = new ProcessStartInfo(Executable, $"--config {Quote(ConfigFolder)} {BackupArguments}{NamesArguments(names)}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Folder
            };

            try
            {
                using (Process.Start(info))
                {
                }

                logger.Info($"Ludusavi goes on alone with the copy of {string.Join(", ", names)}.");
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Ludusavi couldn't start the copy that goes on alone.");
                return false;
            }
        }

        /// <summary>Another Ludusavi is working, like the one left alone when Atrio closed a moment ago.</summary>
        public static bool OtherIsRunning()
        {
            try
            {
                var others = Process.GetProcessesByName("ludusavi");
                foreach (var other in others)
                {
                    other.Dispose();
                }

                return others.Length > 0;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to look for Ludusavi running.");
                return false;
            }
        }

        public static readonly Guid GogPluginId = Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");

        /// <summary>A copy now. The size is what the copy holds; 0 with NoSaves when there was nothing to copy.</summary>
        public static async Task<SaveBackupInfo> BackupAsync(string title, CancellationToken token)
        {
            var answer = await RunAsync($"{BackupArguments} --api", new[] { title }, TimeSpan.FromMinutes(15), token);
            if (answer == null)
            {
                return null;
            }

            var game = answer["games"]?[title] as JObject;
            var bytes = (game?["files"] as JObject)?.Properties().Sum(a => (long?)a.Value["bytes"] ?? 0) ?? 0;
            var failed = answer["errors"]?["someGamesFailed"]?.Type == JTokenType.Boolean && (bool)answer["errors"]["someGamesFailed"];
            if (failed)
            {
                logger.Warn($"Ludusavi couldn't copy all the saves of {title}.");
            }

            return new SaveBackupInfo
            {
                Title = title,
                LastBackup = game == null ? (DateTime?)null : DateTime.Now,
                Bytes = bytes,
                NoSaves = game == null || bytes == 0,
                Failed = failed
            };
        }

        public static async Task<List<SaveBackup>> GetBackupsAsync(string title, CancellationToken token)
        {
            var answer = await RunAsync($"backups --api --path {Quote(BackupFolder)}", new[] { title }, TimeSpan.FromMinutes(2), token);
            var list = answer?["games"]?[title]?["backups"] as JArray;
            if (list == null)
            {
                return new List<SaveBackup>();
            }

            return list.
                Select(a => new SaveBackup { Name = (string)a["name"], When = ((DateTime?)a["when"] ?? DateTime.MinValue).ToLocalTime() }).
                Where(a => !string.IsNullOrEmpty(a.Name)).
                OrderByDescending(a => a.When).
                ToList();
        }

        /// <summary>Puts back that copy. True when Ludusavi restored the game without errors.</summary>
        public static async Task<bool> RestoreAsync(string title, string backupName, CancellationToken token)
        {
            var answer = await RunAsync(
                $"restore --api --force --path {Quote(BackupFolder)} --backup {Quote(backupName)}",
                new[] { title },
                TimeSpan.FromMinutes(15),
                token);
            if (answer == null)
            {
                return false;
            }

            var failed = answer["errors"]?["someGamesFailed"]?.Type == JTokenType.Boolean && (bool)answer["errors"]["someGamesFailed"];
            return !failed && answer["games"]?[title] != null;
        }
    }

    /// <summary>
    /// A copy of the saves of each game after playing it, from Atrio or from outside: when the sitting goes to the
    /// history, the copy is made a few seconds later, when the game has finished writing its saves.
    /// </summary>
    public static class SaveBackups
    {
        public const string FileName = "atrio-copias.json";
        public const string PendingFileName = "atrio-copias-pendientes.json";
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly TimeSpan settle = TimeSpan.FromSeconds(20);
        private static readonly object sync = new object();
        private static readonly SemaphoreSlim working = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<Guid, WaitingCopy> waiting = new ConcurrentDictionary<Guid, WaitingCopy>();
        private static readonly ConcurrentDictionary<Guid, byte> restoring = new ConcurrentDictionary<Guid, byte>();
        private static Dictionary<Guid, SaveBackupInfo> infos;
        private static HashSet<Guid> pending;
        private static IGameDatabaseMain database;
        private static Func<bool> isEnabled;

        /// <summary>A copy waiting for the game to finish writing. Each sitting has its own, so only the last one copies.</summary>
        private class WaitingCopy
        {
            /// <summary>The sitting was of a game opened outside Atrio, which may still be open when Atrio closes.</summary>
            public bool Outside;
        }

        /// <summary>Raised when the copies of a game change, on any thread.</summary>
        public static event EventHandler<Guid> Changed;

        public static string FilePath => Path.Combine(Ludusavi.Root ?? PlaynitePaths.ConfigRootPath, FileName);

        /// <summary>The games owed a copy because Atrio closed before making it: the next start makes it.</summary>
        public static string PendingPath => Path.Combine(Ludusavi.Root ?? PlaynitePaths.ConfigRootPath, PendingFileName);

        public static bool IsEnabled => isEnabled?.Invoke() == true;

        /// <summary>The game being copied right now, Guid.Empty when none.</summary>
        public static Guid Busy { get; private set; }

        public static void Attach(IGameDatabaseMain db, Func<bool> enabled)
        {
            if (database != null || db == null)
            {
                return;
            }

            database = db;
            isEnabled = enabled;
            PlayHistory.Recorded += PlayHistory_Recorded;
            _ = Task.Run(() => MakeOwedCopiesAsync(TimeSpan.FromSeconds(30)));
        }

        private static void PlayHistory_Recorded(object sender, PlaySession e)
        {
            if (!IsEnabled || e == null)
            {
                return;
            }

            // Several sittings in a row (a pause, Atrio closing) make one copy, after the last one.
            var mark = new WaitingCopy { Outside = e.Outside };
            waiting[e.GameId] = mark;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(settle);

                    // Only if no later sitting took its place, and Atrio isn't closing with it.
                    if (!((ICollection<KeyValuePair<Guid, WaitingCopy>>)waiting).Remove(new KeyValuePair<Guid, WaitingCopy>(e.GameId, mark)))
                    {
                        return;
                    }

                    var game = database?.Games.Get(e.GameId);
                    if (game != null && IsEnabled && !OpenGames.IsOpen(game.Id) && !game.IsRunning && !IsRestoring(game.Id))
                    {
                        await BackupAsync(game, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to copy the saves after playing.");
                }
            });
        }

        /// <summary>
        /// Atrio closes, and the copies waiting for their seconds can't wait. The games already closed get theirs now,
        /// from a Ludusavi that goes on alone; all of them are owed to the next start, which checks them and says how
        /// it went. A game opened outside Atrio that is still open waits for that start.
        /// </summary>
        public static void Shutdown()
        {
            if (database == null || !IsEnabled)
            {
                return;
            }

            try
            {
                var owed = new List<Guid>();
                var titles = new List<string>();
                foreach (var item in waiting.ToArray())
                {
                    if (!((ICollection<KeyValuePair<Guid, WaitingCopy>>)waiting).Remove(item))
                    {
                        continue;
                    }

                    owed.Add(item.Key);
                    var game = database.Games.Get(item.Key);
                    var title = GetInfo(item.Key)?.Title;
                    if (game != null && !string.IsNullOrEmpty(title) && !game.IsRunning && !(item.Value.Outside && OpenGames.IsOpen(item.Key)))
                    {
                        titles.Add(title);
                    }
                }

                // The copy being made right now won't be able to say how it went.
                var busy = Busy;
                if (busy != Guid.Empty)
                {
                    owed.Add(busy);
                }

                if (owed.Count == 0)
                {
                    return;
                }

                AddOwed(owed);
                if (busy == Guid.Empty)
                {
                    Ludusavi.StartBackupAlone(titles);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to leave the copies of the saves for later.");
            }
        }

        /// <summary>The copies owed from the last time, a little after the start, once the Ludusavi left alone has finished.</summary>
        private static async Task MakeOwedCopiesAsync(TimeSpan wait)
        {
            try
            {
                if (GetOwed().Count == 0)
                {
                    return;
                }

                await Task.Delay(wait);
                for (var i = 0; i < 60 && Ludusavi.OtherIsRunning(); i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                foreach (var id in GetOwed())
                {
                    var game = database?.Games.Get(id);
                    if (game == null || !IsEnabled)
                    {
                        RemoveOwed(id);
                    }
                    else if (!game.IsRunning && !game.IsLaunching && !OpenGames.IsOpen(id))
                    {
                        await BackupAsync(game, CancellationToken.None);
                    }

                    // A game that is open again keeps its debt: the copy after this sitting pays it.
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to make the copies owed from the last time.");
            }
        }

        public static List<Guid> GetOwed()
        {
            lock (sync)
            {
                return LoadOwed().ToList();
            }
        }

        private static void AddOwed(IEnumerable<Guid> ids)
        {
            lock (sync)
            {
                var set = LoadOwed();
                var added = false;
                foreach (var id in ids)
                {
                    added |= set.Add(id);
                }

                if (added)
                {
                    SaveOwed();
                }
            }
        }

        private static void RemoveOwed(Guid id)
        {
            lock (sync)
            {
                if (LoadOwed().Remove(id))
                {
                    SaveOwed();
                }
            }
        }

        private static HashSet<Guid> LoadOwed()
        {
            if (pending != null)
            {
                return pending;
            }

            try
            {
                if (File.Exists(PendingPath))
                {
                    pending = new HashSet<Guid>(Playnite.Common.Serialization.FromJsonFile<List<Guid>>(PendingPath) ?? new List<Guid>());
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to read the copies of the saves owed from the last time.");
            }

            return pending = pending ?? new HashSet<Guid>();
        }

        private static void SaveOwed()
        {
            try
            {
                if (pending.Count > 0)
                {
                    FileSystem.WriteStringToFile(PendingPath, Playnite.Common.Serialization.ToJson(pending.ToList(), true));
                }
                else if (File.Exists(PendingPath))
                {
                    File.Delete(PendingPath);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to save the copies of the saves owed.");
            }
        }

        /// <summary>A restore of the game is under way, from its question until the end.</summary>
        public static bool IsRestoring(Guid gameId) => restoring.ContainsKey(gameId);

        /// <summary>Marks the start of a restore; false if the game already has one under way.</summary>
        public static bool TryStartRestore(Guid gameId)
        {
            if (!restoring.TryAdd(gameId, 0))
            {
                return false;
            }

            Changed?.Invoke(null, gameId);
            return true;
        }

        public static void EndRestore(Guid gameId)
        {
            if (restoring.TryRemove(gameId, out _))
            {
                Changed?.Invoke(null, gameId);
            }
        }

        public static SaveBackupInfo GetInfo(Guid gameId)
        {
            lock (sync)
            {
                return Load().TryGetValue(gameId, out var info) ? info : null;
            }
        }

        private static void SetInfo(Guid gameId, SaveBackupInfo info)
        {
            lock (sync)
            {
                Load()[gameId] = info;
                try
                {
                    FileSystem.WriteStringToFile(FilePath, Playnite.Common.Serialization.ToJson(infos, true));
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to save the list of the copies of the saves.");
                }
            }

            Changed?.Invoke(null, gameId);
        }

        private static Dictionary<Guid, SaveBackupInfo> Load()
        {
            if (infos != null)
            {
                return infos;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    infos = Playnite.Common.Serialization.FromJsonFile<Dictionary<Guid, SaveBackupInfo>>(FilePath);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to read the list of the copies of the saves.");
            }

            return infos = infos ?? new Dictionary<Guid, SaveBackupInfo>();
        }

        /// <summary>The title of Ludusavi for the game, found once and remembered.</summary>
        private static async Task<string> GetTitleAsync(Game game, CancellationToken token)
        {
            var known = GetInfo(game.Id)?.Title;
            if (!string.IsNullOrEmpty(known))
            {
                return known;
            }

            return await Ludusavi.FindTitleAsync(game, token);
        }

        /// <summary>A copy of the saves of the game now. Null if it couldn't be made.</summary>
        public static async Task<SaveBackupInfo> BackupAsync(Game game, CancellationToken token)
        {
            await working.WaitAsync(token);
            Busy = game.Id;
            Changed?.Invoke(null, game.Id);
            try
            {
                if (!await Ludusavi.InstallAsync(token))
                {
                    return null;
                }

                var title = await GetTitleAsync(game, token);
                if (string.IsNullOrEmpty(title))
                {
                    var unknown = new SaveBackupInfo { NoSaves = true };
                    SetInfo(game.Id, unknown);
                    RemoveOwed(game.Id);
                    logger.Info($"Ludusavi doesn't know {game.Name}: no copy of its saves.");
                    return unknown;
                }

                var result = await Ludusavi.BackupAsync(title, token);
                if (result == null)
                {
                    return null;
                }

                var previous = GetInfo(game.Id);
                if (result.NoSaves && previous?.LastBackup != null)
                {
                    // Nothing new to copy this time: the last copy is still the good one.
                    result.LastBackup = previous.LastBackup;
                    result.Bytes = previous.Bytes;
                    result.NoSaves = false;
                }

                SetInfo(game.Id, result);
                RemoveOwed(game.Id);
                logger.Info($"Copy of the saves of {game.Name} ({title}): {(result.NoSaves ? "nothing found" : result.Bytes + " bytes")}{(result.Failed ? ", not every file" : string.Empty)}.");
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to copy the saves of {game.Name}.");
                return null;
            }
            finally
            {
                Busy = Guid.Empty;
                working.Release();
                Changed?.Invoke(null, game.Id);
            }
        }

        /// <summary>
        /// Puts back the copy the person chose, and no other: a newer one may have come in the meantime. First the
        /// current saves get a copy of their own, so going back is always possible; if that copy couldn't take every
        /// file, nothing is touched.
        /// </summary>
        public static async Task<SaveRestoreResult> RestoreAsync(Game game, SaveBackup chosen, CancellationToken token)
        {
            if (game == null || string.IsNullOrEmpty(chosen?.Name))
            {
                return SaveRestoreResult.Nothing;
            }

            string title;
            try
            {
                title = await GetTitleAsync(game, token);
                if (string.IsNullOrEmpty(title))
                {
                    return SaveRestoreResult.Nothing;
                }

                var safety = await BackupAsync(game, token);
                if (safety == null || safety.Failed || string.IsNullOrEmpty(safety.Title))
                {
                    logger.Warn($"No restore of the saves of {game.Name}: the copy of the current ones didn't take every file.");
                    return SaveRestoreResult.NoSafetyCopy;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                logger.Error(e, $"Failed to prepare the restore of the saves of {game.Name}.");
                return SaveRestoreResult.Nothing;
            }

            await working.WaitAsync(token);
            Busy = game.Id;
            Changed?.Invoke(null, game.Id);
            try
            {
                // The copy that made room for the new one may have been the chosen one, if it was the oldest.
                List<SaveBackup> backups;
                try
                {
                    backups = await Ludusavi.GetBackupsAsync(title, token);
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    logger.Error(e, $"Failed to list the copies of the saves of {game.Name}.");
                    return SaveRestoreResult.Nothing;
                }

                if (!backups.Any(a => a.Name == chosen.Name))
                {
                    logger.Warn($"No restore of the saves of {game.Name}: the copy of {chosen.When} isn't there anymore.");
                    return SaveRestoreResult.Nothing;
                }

                var restored = await Ludusavi.RestoreAsync(title, chosen.Name, token);
                logger.Info($"Restore of the saves of {game.Name} from {chosen.When}: {(restored ? "done" : "failed")}.");
                return restored ? SaveRestoreResult.Done : SaveRestoreResult.Partly;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                logger.Error(e, $"Failed to restore the saves of {game.Name}.");
                return SaveRestoreResult.Partly;
            }
            finally
            {
                Busy = Guid.Empty;
                working.Release();
                Changed?.Invoke(null, game.Id);
            }
        }

        /// <summary>The date of the copy that a restore would put back, without making any copy.</summary>
        public static async Task<SaveBackup> GetLatestAsync(Game game, CancellationToken token)
        {
            var title = await GetTitleAsync(game, token);
            return string.IsNullOrEmpty(title) ? null : (await Ludusavi.GetBackupsAsync(title, token)).FirstOrDefault();
        }
    }
}
