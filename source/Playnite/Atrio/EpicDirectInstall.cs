using Microsoft.Win32;
using Newtonsoft.Json;
using Playnite.Common;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>The three names the Epic launcher needs for a game. The add-on only keeps the last one.</summary>
    public class EpicGameId
    {
        public string Namespace { get; set; }
        public string CatalogItem { get; set; }
        public string AppName { get; set; }

        /// <summary>How the launcher writes them in its logs.</summary>
        public string FullId => $"{Namespace}:{CatalogItem}:{AppName}";

        /// <summary>The launcher says nothing with fewer than the three names, and with these it opens the install of that game.</summary>
        public string InstallUri => $"com.epicgames.launcher://apps/{Namespace}%3A{CatalogItem}%3A{AppName}?action=install&silent=true";
    }

    /// <summary>What the launcher keeps about a game it installed or is installing.</summary>
    public class EpicInstallItem
    {
        public string AppName { get; set; }
        public string InstallLocation { get; set; }
        public bool bIsIncompleteInstall { get; set; }
        public long InstallSize { get; set; }
    }

    /// <summary>The files of the Epic launcher on this computer. None of them have anything of the account.</summary>
    public static class EpicClientFiles
    {
        private class CatalogRelease
        {
            public string appId { get; set; }
        }

        private class CatalogItem
        {
            public string id { get; set; }
            public string @namespace { get; set; }
            public List<CatalogRelease> releaseInfo { get; set; }
        }

        private const string CatalogFile = @"Catalog\catcache.bin";

        public static string DataFolder
        {
            get
            {
                var registered = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath", null) as string;
                return registered ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data");
            }
        }

        public static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"EpicGamesLauncher\Saved\Logs\EpicGamesLauncher.log");

        /// <summary>The catalog is plain JSON in base 64. Null if the game isn't there.</summary>
        public static EpicGameId ParseCatalog(string catalog, string appName)
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(catalog));
            foreach (var item in JsonConvert.DeserializeObject<List<CatalogItem>>(text) ?? new List<CatalogItem>())
            {
                if (item.releaseInfo?.Any(a => a.appId == appName) == true)
                {
                    return new EpicGameId { Namespace = item.@namespace, CatalogItem = item.id, AppName = appName };
                }
            }

            return null;
        }

        public static EpicGameId FindGame(string appName)
        {
            var file = Path.Combine(DataFolder, CatalogFile);
            return File.Exists(file) ? ParseCatalog(File.ReadAllText(file), appName) : null;
        }

        /// <summary>Null when the launcher has nothing about the game.</summary>
        public static EpicInstallItem FindInstall(string appName)
        {
            var folder = Path.Combine(DataFolder, "Manifests");
            if (!Directory.Exists(folder))
            {
                return null;
            }

            foreach (var file in Directory.GetFiles(folder, "*.item", SearchOption.AllDirectories))
            {
                try
                {
                    var item = JsonConvert.DeserializeObject<EpicInstallItem>(ReadShared(file));
                    if (item?.AppName == appName)
                    {
                        return item;
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
                {
                    // The launcher is writing it, it's read again in the next round.
                }
            }

            return null;
        }

        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }

    /// <summary>Reads what a program adds to its log after a given moment, in whole lines.</summary>
    public class LogFollower
    {
        private const int StartLength = 256;

        private readonly string path;
        private long position;
        private byte[] start;

        public LogFollower(string path)
        {
            this.path = path;
            if (File.Exists(path))
            {
                using (var stream = Open())
                {
                    position = stream.Length;
                    start = ReadStart(stream);
                }
            }
        }

        private FileStream Open()
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private static byte[] ReadStart(FileStream stream)
        {
            var bytes = new byte[Math.Min(StartLength, stream.Length)];
            stream.Seek(0, SeekOrigin.Begin);
            stream.Read(bytes, 0, bytes.Length);
            return bytes;
        }

        public List<string> ReadNew()
        {
            if (!File.Exists(path))
            {
                return new List<string>();
            }

            using (var stream = Open())
            {
                // A program that starts leaves the old log aside and writes a new one, which can be
                // bigger than the old one by now. Its first line has the date and gives it away.
                var current = ReadStart(stream);
                if (start == null || stream.Length < position || !current.Take(start.Length).SequenceEqual(start.Take(current.Length)))
                {
                    position = 0;
                }

                start = current;
                stream.Seek(position, SeekOrigin.Begin);
                var bytes = new byte[stream.Length - position];
                var read = stream.Read(bytes, 0, bytes.Length);

                // A line that's still being written waits for the next round.
                var end = read == 0 ? -1 : Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                if (end < 0)
                {
                    return new List<string>();
                }

                position += end + 1;
                return Encoding.UTF8.GetString(bytes, 0, end).Split('\n').ToList();
            }
        }
    }

    /// <summary>
    /// The launcher takes an order to install a game, but it asks for the folder first in a dialog of its
    /// own. That dialog takes keys through messages, and its log tells when it's up and how it ended.
    /// </summary>
    public class EpicDirectInstallController : DirectInstallController
    {
        private const string Process = "EpicGamesLauncher";
        private const int TabsToShortcut = 4;

        // One dialog at a time: two installs at once would mix their keys in it.
        private static readonly SemaphoreSlim dialogTurn = new SemaphoreSlim(1, 1);

        private static readonly TimeSpan dialogPatience = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan configPatience = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan answerPatience = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan dialogPause = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan keysPause = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan keysAnswerPatience = TimeSpan.FromSeconds(2);

        private readonly Queue<string> logLines = new Queue<string>();

        public EpicDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        /// <summary>
        /// The way to the shortcut box of the dialog. With the launcher just started the first key doesn't
        /// count and this ends up pressing the install button, so the dialog can be over after these keys.
        /// </summary>
        public static IEnumerable<int> GetShortcutKeys()
        {
            for (var i = 0; i < TabsToShortcut; i++)
            {
                yield return StoreWindows.TabKey;
            }

            yield return StoreWindows.SpaceKey;
        }

        /// <summary>From the shortcut box to the install button.</summary>
        public static IEnumerable<int> GetInstallKeys()
        {
            yield return StoreWindows.TabKey;
            yield return StoreWindows.EnterKey;
        }

        /// <summary>Null while the dialog of this game is open.</summary>
        public static bool? ReadDialogAnswer(string logLine, string fullId)
        {
            if (!logLine.Contains("selector concluded") || !logLine.Contains(fullId))
            {
                return null;
            }

            return logLine.Contains("accepted=true");
        }

        /// <summary>Goes through the lines in order and leaves the ones after the wanted one for next time.</summary>
        private bool TakeLine(LogFollower log, Func<string, bool> wanted)
        {
            log.ReadNew().ForEach(logLines.Enqueue);
            while (logLines.Count > 0)
            {
                if (wanted(logLines.Dequeue()))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<bool> WaitForLineAsync(LogFollower log, TimeSpan patience, Func<string, bool> wanted)
        {
            var started = DateTime.Now;
            while (!TakeLine(log, wanted))
            {
                if (DateTime.Now - started >= patience)
                {
                    return false;
                }

                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
            }

            return true;
        }

        private async Task<bool?> WaitForAnswerAsync(LogFollower log, EpicGameId id, TimeSpan patience)
        {
            bool? answer = null;
            await WaitForLineAsync(log, patience, a =>
            {
                answer = ReadDialogAnswer(a, id.FullId);
                return answer != null;
            }).ConfigureAwait(false);
            return answer;
        }

        private bool? ReadAnswer(LogFollower log, EpicGameId id)
        {
            bool? answer = null;
            TakeLine(log, a =>
            {
                answer = ReadDialogAnswer(a, id.FullId);
                return answer != null;
            });
            return answer;
        }

        /// <summary>Null if the launcher never got to the dialog of this game.</summary>
        private async Task<bool?> GoThroughDialogAsync(EpicGameId id, LogFollower log)
        {
            ProcessStarter.StartUrl(id.InstallUri);
            if (!await WaitForLineAsync(log, dialogPatience, a => a.Contains("Dispatching install for AppId " + id.FullId)).ConfigureAwait(false) ||
                !await WaitForLineAsync(log, configPatience, a => a.Contains("PopulateInstallConfigFromCurrent: AppId=" + id.FullId)).ConfigureAwait(false) ||
                !await WaitForLineAsync(log, configPatience, a => a.Contains("FINAL DestinationPath")).ConfigureAwait(false))
            {
                Logger.Warn($"The Epic launcher didn't get to the install of {id.FullId}.");
                return null;
            }

            await Task.Delay(dialogPause, Cancellation).ConfigureAwait(false);
            var window = StoreWindows.Find(Process, (_, type) => type == "UnrealWindow");
            if (window == IntPtr.Zero)
            {
                return null;
            }

            // The launcher already came up with the order, and its dialog ignores the keys when it isn't active.
            StoreWindows.Show(window);
            await Task.Delay(keysPause, Cancellation).ConfigureAwait(false);

            // The person may have got there first, and the keys would land on the library.
            var answer = ReadAnswer(log, id);
            if (answer == null)
            {
                foreach (var key in GetShortcutKeys())
                {
                    StoreWindows.PostKey(window, key);
                }

                answer = await WaitForAnswerAsync(log, id, keysAnswerPatience).ConfigureAwait(false);
            }

            if (answer == null)
            {
                foreach (var key in GetInstallKeys())
                {
                    StoreWindows.PostKey(window, key);
                }

                answer = await WaitForAnswerAsync(log, id, answerPatience).ConfigureAwait(false);
            }

            if (answer == true)
            {
                StoreWindows.Minimize(window);
            }
            else if (answer == null)
            {
                // The dialog of this game is different. It's there with everything filled in.
                Logger.Warn($"The Epic launcher didn't take the install dialog of {id.FullId}.");
                StoreWindows.Show(window);
            }

            return answer ?? true;
        }

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            var id = await Task.Run(() => EpicClientFiles.FindGame(Game.GameId), Cancellation).ConfigureAwait(false);
            if (id == null)
            {
                Logger.Info($"The Epic catalog of this computer doesn't have {Game.GameId}.");
                return false;
            }

            var log = new LogFollower(EpicClientFiles.LogFile);
            bool? goesAhead;
            await dialogTurn.WaitAsync(Cancellation).ConfigureAwait(false);
            try
            {
                goesAhead = await GoThroughDialogAsync(id, log).ConfigureAwait(false);
            }
            finally
            {
                dialogTurn.Release();
            }

            if (goesAhead == null)
            {
                return false;
            }

            if (goesAhead == false)
            {
                Abandon();
                return true;
            }

            return await WatchInstallAsync(id, log).ConfigureAwait(false);
        }

        private async Task<bool> WatchInstallAsync(EpicGameId id, LogFollower log)
        {
            var started = DateTime.Now;
            var seen = false;
            var missing = 0;
            while (true)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                if (!seen && ReadAnswer(log, id) == false)
                {
                    // The dialog stayed for the person, and they said no.
                    Abandon();
                    return true;
                }

                var item = await Task.Run(() => EpicClientFiles.FindInstall(Game.GameId), Cancellation).ConfigureAwait(false);
                if (item == null)
                {
                    if (seen && ++missing > 2)
                    {
                        // Cancelled from the launcher, which removes everything about the game.
                        Abandon();
                        return true;
                    }

                    if (!seen && DateTime.Now - started > startPatience)
                    {
                        // Nobody knows if the person is still looking at the dialog: the add-on keeps watching.
                        Logger.Warn($"The Epic launcher didn't start to install {Game.GameId}.");
                        return false;
                    }

                    continue;
                }

                seen = true;
                missing = 0;
                if (!item.bIsIncompleteInstall)
                {
                    Finish(item.InstallLocation);
                    return true;
                }

                if (item.InstallSize > 0)
                {
                    var written = FolderSize(item.InstallLocation);
                    DirectInstallProgress.Set(Game.Id, (int)Math.Min(99, written * 100 / item.InstallSize));
                }
            }
        }

        private static long FolderSize(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(a => a.Length) : 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }
}
