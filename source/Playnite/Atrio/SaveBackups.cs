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
        public static string BackupFolder => Path.Combine(Root ?? PlaynitePaths.ConfigRootPath, "Copias de partidas");
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
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                logger.Error(e, "Failed to download Ludusavi.");
                return false;
            }
            finally
            {
                installing.Release();
            }
        }

        /// <summary>Runs Ludusavi without a window, with the names of the games on its input. Null if it gave no answer.</summary>
        public static async Task<JObject> RunAsync(string arguments, IEnumerable<string> names, TimeSpan timeout, CancellationToken token)
        {
            if (!IsInstalled)
            {
                return null;
            }

            var info = new ProcessStartInfo(Executable, $"--config \"{ConfigFolder}\" --try-manifest-update {arguments}")
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
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    foreach (var name in names ?? Enumerable.Empty<string>())
                    {
                        await input.WriteLineAsync(name);
                    }
                }

                var finished = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds), token);
                if (!finished)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
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

        /// <summary>The title Ludusavi uses for the game: by its number of Steam or GOG, by the exact name, or by an approximation.</summary>
        public static async Task<string> FindTitleAsync(Game game, CancellationToken token)
        {
            var timeout = TimeSpan.FromMinutes(3);
            var tries = new List<Tuple<string, string[]>>();
            if (game.PluginId == SteamContent.SteamPluginId && int.TryParse(game.GameId, out var steamId))
            {
                tries.Add(Tuple.Create($"find --api --steam-id {steamId}", new string[0]));
            }

            if (game.PluginId == GogPluginId && long.TryParse(game.GameId, out var gogId))
            {
                tries.Add(Tuple.Create($"find --api --gog-id {gogId}", new string[0]));
            }

            tries.Add(Tuple.Create("find --api", new[] { game.Name }));
            tries.Add(Tuple.Create("find --api --normalized", new[] { game.Name }));
            foreach (var attempt in tries)
            {
                var answer = await RunAsync(attempt.Item1, attempt.Item2, timeout, token);
                var title = (answer?["games"] as JObject)?.Properties().FirstOrDefault()?.Name;
                if (!string.IsNullOrEmpty(title))
                {
                    return title;
                }
            }

            return null;
        }

        public static readonly Guid GogPluginId = Guid.Parse("aebe8b7c-6dc3-4a66-af31-e7375c6b5e9e");

        /// <summary>A copy now. The size is what the copy holds; 0 with NoSaves when there was nothing to copy.</summary>
        public static async Task<SaveBackupInfo> BackupAsync(string title, CancellationToken token)
        {
            var answer = await RunAsync(
                $"backup --api --force --path \"{BackupFolder}\" --format zip --full-limit 2 --differential-limit 4",
                new[] { title },
                TimeSpan.FromMinutes(15),
                token);
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
                NoSaves = game == null || bytes == 0
            };
        }

        public static async Task<List<SaveBackup>> GetBackupsAsync(string title, CancellationToken token)
        {
            var answer = await RunAsync($"backups --api --path \"{BackupFolder}\"", new[] { title }, TimeSpan.FromMinutes(2), token);
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
                $"restore --api --force --path \"{BackupFolder}\" --backup \"{backupName}\"",
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
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly TimeSpan settle = TimeSpan.FromSeconds(20);
        private static readonly object sync = new object();
        private static readonly SemaphoreSlim working = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<Guid, DateTime> waiting = new ConcurrentDictionary<Guid, DateTime>();
        private static Dictionary<Guid, SaveBackupInfo> infos;
        private static IGameDatabaseMain database;
        private static Func<bool> isEnabled;

        /// <summary>Raised when the copies of a game change, on any thread.</summary>
        public static event EventHandler<Guid> Changed;

        public static string FilePath => Path.Combine(Ludusavi.Root ?? PlaynitePaths.ConfigRootPath, FileName);

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
        }

        private static void PlayHistory_Recorded(object sender, PlaySession e)
        {
            if (!IsEnabled || e == null)
            {
                return;
            }

            // Several sittings in a row (a pause, Atrio closing) make one copy, after the last one.
            waiting[e.GameId] = DateTime.Now;
            _ = Task.Run(async () =>
            {
                await Task.Delay(settle);
                if (waiting.TryGetValue(e.GameId, out var at) && DateTime.Now - at >= settle - TimeSpan.FromSeconds(1))
                {
                    waiting.TryRemove(e.GameId, out _);
                    var game = database?.Games.Get(e.GameId);
                    if (game != null && !OpenGames.IsOpen(game.Id) && !game.IsRunning)
                    {
                        await BackupAsync(game, CancellationToken.None);
                    }
                }
            });
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
                logger.Info($"Copy of the saves of {game.Name} ({title}): {(result.NoSaves ? "nothing found" : result.Bytes + " bytes")}.");
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
        /// Puts back the newest copy made before now. First the current saves get a copy of their own, so going back
        /// is always possible. False if anything failed; then nothing was restored.
        /// </summary>
        public static async Task<bool> RestoreLatestAsync(Game game, CancellationToken token)
        {
            var title = await GetTitleAsync(game, token);
            if (string.IsNullOrEmpty(title))
            {
                return false;
            }

            var backups = await Ludusavi.GetBackupsAsync(title, token);
            var latest = backups.FirstOrDefault();
            if (latest == null)
            {
                return false;
            }

            if (await BackupAsync(game, token) == null)
            {
                return false;
            }

            await working.WaitAsync(token);
            Busy = game.Id;
            try
            {
                var restored = await Ludusavi.RestoreAsync(title, latest.Name, token);
                logger.Info($"Restore of the saves of {game.Name} from {latest.When}: {(restored ? "done" : "failed")}.");
                return restored;
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
