using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Atrio.OptiScaler
{
    /// <summary>Atrio asked to stop, or went away, before anything of the game was touched.</summary>
    internal sealed class StoppedException : Exception
    {
        public StoppedException() : base("Stopped before touching the game.")
        {
        }
    }

    /// <summary>What the client wrote down about the OptiScaler of a game when it put it there.</summary>
    internal sealed class Record
    {
        public object Manifest { get; private set; }

        /// <summary>The folder the copies of the originals are kept under.</summary>
        public string Key { get; private set; }

        /// <summary>The install ended well: everything can be put back as it was.</summary>
        public bool IsWhole { get; private set; }

        /// <summary>An install that was cut in the middle: the originals it had already kept are still there.</summary>
        public bool IsHalf => Manifest != null && !IsWhole;
        public string Folder => Client.Text(Manifest, "InstalledGameDirectory");

        public static Record Find(object game)
        {
            var record = Load(Client.Text(game, "InstallPath"));
            if (record.IsWhole || !Client.CanCall("BackupStoreService", "FindBackupDirUnder", "gameRoot"))
            {
                return record;
            }

            // The client also looks for the ones kept under another folder of the game.
            var other = Client.Call(Client.New("BackupStoreService"), "FindBackupDirUnder", "gameRoot", Client.Text(game, "InstallPath")) as string;
            var found = string.IsNullOrEmpty(other) ? null : Load(other);
            return found?.IsWhole == true ? found : record;
        }

        private static Record Load(string key)
        {
            var manifest = string.IsNullOrEmpty(key) || !Client.CanCall("BackupStoreService", "LoadManifest", "gameDir") ? null :
                Client.Call(Client.New("BackupStoreService"), "LoadManifest", "gameDir", key);
            return new Record
            {
                Manifest = manifest,
                Key = key,
                IsWhole = string.Equals(Client.Text(manifest, "OperationStatus"), "committed", StringComparison.OrdinalIgnoreCase)
            };
        }

        /// <summary>Everything it says it wrote or replaced, inside its folder.</summary>
        public IEnumerable<string> Files
        {
            get
            {
                var records = new[] { "FilesCreated", "FilesOverwritten" }.SelectMany(a => Client.Items(Client.TryGet(Manifest, a))).Select(a => Client.Text(a, "RelativePath"));
                var names = new[] { "InstalledFiles", "BackedUpFiles" }.SelectMany(a => Client.Strings(Client.TryGet(Manifest, a)));
                return records.Concat(names).Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>The originals of the game it replaced, with the name each one has among the copies.</summary>
        public Dictionary<string, string> Originals
        {
            get
            {
                var result = Client.Strings(Client.TryGet(Manifest, "BackedUpFiles")).Distinct(StringComparer.OrdinalIgnoreCase).
                    ToDictionary(a => a, a => a, StringComparer.OrdinalIgnoreCase);
                foreach (var file in Client.Items(Client.TryGet(Manifest, "FilesOverwritten")))
                {
                    var name = Client.Text(file, "RelativePath");
                    if (!string.IsNullOrEmpty(name))
                    {
                        result[name] = Client.Text(file, "BackupRelativePath") is string copy && copy.Length > 0 ? copy : name;
                    }
                }

                return result;
            }
        }

        /// <summary>Where the client keeps the version that was being installed.</summary>
        public string Package
        {
            get
            {
                var version = Client.Text(Manifest, "OptiscalerVersion");
                return string.IsNullOrEmpty(version) || !Client.CanCall("ComponentManagementService", "GetOptiScalerCachePath", "version") ? null :
                    Client.Call(Client.New("ComponentManagementService"), "GetOptiScalerCachePath", "version", version) as string;
            }
        }

        /// <summary>
        /// Undoes an install that the client itself left in the middle, without what Atrio keeps before its own: what is of
        /// the package of OptiScaler and was made in the game since the install started goes away, and the originals that it
        /// had kept come back. The client doesn't know how to: it only writes the files down at the end, and another install
        /// would take the ones of OptiScaler for the originals. What stays of the package, because it could be of the game,
        /// is told. Null when there's no way to know.
        /// </summary>
        public List<string> Undo(string folder)
        {
            var package = Package;
            var store = Client.New("BackupStoreService");
            if (!Plan.Files(package).Any() || string.IsNullOrEmpty(folder) || !Directory.Exists(folder) ||
                !Client.CanCall("BackupStoreService", "GetFilesDir", "gameDir") || !Client.CanCall("BackupStoreService", "DeleteBackup", "gameDir") ||
                !DateTime.TryParse(Client.Text(Manifest, "StartedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
            {
                return null;
            }

            started = started.ToUniversalTime().AddSeconds(-2);
            var kept = Client.Call(store, "GetFilesDir", "gameDir", Key) as string;

            // A copy keeps the date of its file when it's whole: one with the date of the install was cut, and its file is intact.
            var originals = new HashSet<string>(
                Plan.Files(kept).Where(a => File.GetLastWriteTimeUtc(a) < started).Select(a => Path.GetRelativePath(kept, a)), StringComparer.OrdinalIgnoreCase);
            var remaining = new List<string>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Plan.Files(package))
            {
                var relative = Path.GetRelativePath(package, file);
                var main = relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) || relative.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase);
                foreach (var name in main ? new[] { relative, Injection } : new[] { relative })
                {
                    var target = name == null ? null : Path.Combine(folder, name);
                    if (target == null || !File.Exists(target) || originals.Contains(name) || !(Checkpoint.IsOwn(name) || Checkpoint.IsOurs(target, new[] { Checkpoint.Print(file) })))
                    {
                        continue;
                    }

                    if (File.GetCreationTimeUtc(target) < started)
                    {
                        remaining.Add(name);
                        continue;
                    }

                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                    for (var parent = Path.GetDirectoryName(target); parent.Length > folder.Length; parent = Path.GetDirectoryName(parent))
                    {
                        folders.Add(parent);
                    }
                }
            }

            foreach (var name in originals)
            {
                // One written long after the install was cut is from an update of the game, or from someone else.
                var target = Path.Combine(folder, name);
                if (!File.Exists(target) || Checkpoint.IsOwn(name) || File.GetLastWriteTimeUtc(target) < started.AddMinutes(10))
                {
                    Checkpoint.Overwrite(Path.Combine(kept, name), target);
                }
            }

            Checkpoint.RemoveEmpty(folders);

            // Everything is back in its place: the copies have nothing left to keep.
            Client.Call(store, "DeleteBackup", "gameDir", Key);
            return remaining;
        }

        private string Injection
        {
            get
            {
                var name = Client.Text(Manifest, "InjectionMethod");
                return !string.IsNullOrEmpty(name) && Path.GetFileName(name) == name ? name : null;
            }
        }
    }

    /// <summary>
    /// The files of other mods in the folders that the client removes whole when it takes OptiScaler out or puts another
    /// version («plugins» and «Licenses», which OptiScaler shares with other mods). They go aside and come back in their place.
    /// </summary>
    internal sealed class Guests : IDisposable
    {
        private const string Prefix = ".atrio-optiscaler-";
        private const string Suffix = ".atrio-";

        // The ones the client itself puts there, which it doesn't write down.
        private static readonly HashSet<string> ofTheClient = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OptiPatcher.asi", "XeFGUnlock.asi", "AMD-NR-bridge.asi"
        };

        private static readonly string[] shared = { "plugins", "Licenses", @"OptiScaler\plugins" };

        private readonly string folder;
        private readonly Func<IEnumerable<string>> own;
        private readonly string aside;

        /// <summary>The folder of a game, and what is of OptiScaler in it at each moment.</summary>
        public Guests(string folder, Func<IEnumerable<string>> own)
        {
            this.folder = folder;
            this.own = own;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            BringBack(folder, own());
            var known = Names(own());
            try
            {
                foreach (var file in shared.SelectMany(a => Content(Path.Combine(folder, a))))
                {
                    var relative = Path.GetRelativePath(folder, file);
                    if (known.Contains(relative) || ofTheClient.Contains(Path.GetFileName(file)))
                    {
                        continue;
                    }

                    aside = aside ?? Path.Combine(folder, Prefix + Guid.NewGuid().ToString("N"));
                    var target = Path.Combine(aside, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Move(file, target);
                }
            }
            catch
            {
                // Something has one of them open: nothing was done yet, and the others go back.
                BringBack(folder, own());
                throw;
            }
        }

        public void Dispose()
        {
            if (aside != null)
            {
                BringBack(folder, own());
            }
        }

        private static HashSet<string> Names(IEnumerable<string> names)
        {
            return new HashSet<string>(names.Select(a => a.Replace('/', '\\').TrimStart('\\')), StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> Content(string place)
        {
            return Plan.Files(place).Concat(Plan.Links(place)).ToList();
        }

        private static void Move(string from, string to)
        {
            // A link to a folder is a folder too: the link goes, not what there is on its other side.
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }

        /// <summary>Whether there are some that couldn't go back to their place.</summary>
        public static bool Any(string folder)
        {
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && Directory.GetDirectories(folder, Prefix + "*").Length > 0;
        }

        /// <summary>Also the ones of another time that didn't get to its end. The ones that can't go back now wait for the next time.</summary>
        public static void BringBack(string folder, IEnumerable<string> own)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            foreach (var place in Directory.GetDirectories(folder, Prefix + "*"))
            {
                foreach (var file in Content(place))
                {
                    Try(() => Return(file, Path.Combine(folder, Path.GetRelativePath(place, file))));
                }

                if (Content(place).Count == 0)
                {
                    Try(() => Directory.Delete(place, true));
                }
            }

            // Their names were taken the other time, and now they may be free: not while they are of OptiScaler, even if its
            // file isn't there at this moment.
            var taken = Names(own);
            foreach (var file in shared.SelectMany(a => Content(Path.Combine(folder, a))))
            {
                var mark = Path.GetFileName(file).LastIndexOf(Suffix, StringComparison.Ordinal);
                var name = mark <= 0 ? null : Path.Combine(Path.GetDirectoryName(file), Path.GetFileName(file).Substring(0, mark));
                if (name != null && !File.Exists(name) && !Directory.Exists(name) && !taken.Contains(Path.GetRelativePath(folder, name)))
                {
                    Try(() => Move(file, name));
                }
            }
        }

        private static void Return(string file, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                Move(file, target);
            }
            else if (File.Exists(file) && File.Exists(target) && Checkpoint.IsSame(file, target))
            {
                File.Delete(file);
            }
            else
            {
                // What OptiScaler brought with the same name is used now; the other stays next to it.
                var number = 1;
                while (File.Exists($"{target}{Suffix}{number}") || Directory.Exists($"{target}{Suffix}{number}"))
                {
                    number++;
                }

                Move(file, $"{target}{Suffix}{number}");
            }
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"A file of another mod couldn't go back to its place: {e.Message}");
            }
        }
    }

    internal static class Safety
    {
        /// <summary>
        /// One at a time on the same games and copies, also when a program that was left alone is still ending. False when
        /// there was no turn in that time.
        /// </summary>
        public static bool Alone(TimeSpan wait, Action action)
        {
            using (var only = new Mutex(false, $@"Local\Atrio.OptiScaler.{Contract.DataName()}"))
            {
                bool mine;
                try
                {
                    mine = only.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    // The one that had it ended in the middle of something.
                    mine = true;
                }

                if (!mine)
                {
                    return false;
                }

                try
                {
                    action();
                    return true;
                }
                finally
                {
                    only.ReleaseMutex();
                }
            }
        }

        /// <summary>The window of the client writes its list when it closes: what is done meanwhile would be lost.</summary>
        public static bool IsWindowOpen()
        {
            foreach (var process in Process.GetProcessesByName(Client.AssemblyName))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero)
                        {
                            return true;
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                    {
                        // It just ended.
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// A file of the game that another program has open, which is what an open game does. Changing the others would
        /// leave it half done. Null when everything is free.
        /// </summary>
        public static string FindBusy(string folder, Record record)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            var paths = record.Files.Select(a => Path.Combine(folder, a)).
                Concat(Directory.EnumerateFiles(folder, "*.exe")).
                Concat(Directory.EnumerateFiles(folder, "*.dll"));
            return paths.FirstOrDefault(IsBusy) is string busy ? Path.GetFileName(busy) : null;
        }

        private static bool IsBusy(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    {
                    }
                }

                return false;
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) == 32 || (e.HResult & 0xFFFF) == 33)
            {
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Without permission to write, the client says so with its own words.
                return false;
            }
        }
    }
}
