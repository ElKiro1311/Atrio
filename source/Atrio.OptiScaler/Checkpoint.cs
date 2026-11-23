using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atrio.OptiScaler
{
    /// <summary>The names that a change may touch in the folder of a game, and what it would leave in their place.</summary>
    internal sealed class Plan
    {
        private readonly Dictionary<string, HashSet<string>> names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<KeyValuePair<string, HashSet<string>>> Names => names;

        /// <summary>A name relative to the folder of the game. The ones outside of it aren't of this game.</summary>
        public void Add(string name, string source = null)
        {
            name = name?.Replace('/', '\\').TrimStart('\\');
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Split('\\').Contains(".."))
            {
                return;
            }

            if (!names.TryGetValue(name, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                names[name] = sources;
            }

            if (source != null)
            {
                sources.Add(source);
            }
        }

        /// <summary>Every file of a package, in each of the places of the game it may go to.</summary>
        public void AddFolder(string source, params string[] places)
        {
            foreach (var file in Files(source))
            {
                foreach (var place in places)
                {
                    Add(Path.Combine(place, Path.GetRelativePath(source, file)), file);
                }
            }
        }

        /// <summary>What there is in a folder now, without going through links to other folders.</summary>
        public static List<string> Files(string folder)
        {
            var result = new List<string>();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !IsLink(folder))
            {
                result.AddRange(Directory.GetFiles(folder));
                foreach (var inside in Directory.GetDirectories(folder))
                {
                    result.AddRange(Files(inside));
                }
            }

            return result;
        }

        /// <summary>The links to other folders there are inside of a folder.</summary>
        public static List<string> Links(string folder)
        {
            var result = new List<string>();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !IsLink(folder))
            {
                foreach (var inside in Directory.GetDirectories(folder))
                {
                    if (IsLink(inside))
                    {
                        result.Add(inside);
                    }
                    else
                    {
                        result.AddRange(Links(inside));
                    }
                }
            }

            return result;
        }

        private static bool IsLink(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
    }

    /// <summary>
    /// The files of a game that are about to be changed, and the copies that the client has of its originals, kept aside
    /// until the change ends. If it fails, or it's cut in the middle (the computer goes off, someone ends the program), both
    /// go back to what they were. The client undoes what fails too, but only while it's running.
    /// </summary>
    internal sealed class Checkpoint : IDisposable
    {
        private const string ListFile = "list.json";
        private const int HeadSize = 64 * 1024;

        // The names that only OptiScaler and its pieces have: whatever is in them is theirs.
        private static readonly string[] ownNames = { "dlssg_to_fsr3_amd_is_better.dll", "dlss-enabler-headless.dll", "OptiPatcher.asi", "XeFGUnlock.asi" };

        private readonly string key;
        private readonly string storeKey;
        private bool ended;

        private Checkpoint(string key, string storeKey)
        {
            this.key = key;
            this.storeKey = storeKey;
        }

        private static string Root => Path.Combine(
            Contract.DataFolder() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atrio.OptiScaler"), "Atrio");

        private static string Place(string key)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(key).TrimEnd('\\', '/').ToLowerInvariant()));
            return Path.Combine(Root, Convert.ToHexString(bytes).Substring(0, 16));
        }

        /// <summary>The folder where the client keeps the originals of a game.</summary>
        private static string Store(string key)
        {
            return Client.CanCall("BackupStoreService", "GetBackupRoot", "gameDir") ?
                Client.Call(Client.New("BackupStoreService"), "GetBackupRoot", "gameDir", key) as string : null;
        }

        /// <summary>The folder of a game and the one its copies are known by, which is nearly always the same.</summary>
        public static Checkpoint Begin(string key, string storeKey, string folder, Plan plan)
        {
            var place = Place(key);
            if (Read(key) != null)
            {
                throw new ServiceException("half", "A change of this game was cut in the middle.");
            }

            Delete(place);
            try
            {
                var store = Store(storeKey);
                var files = new JsonArray();
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in plan.Names)
                {
                    var path = Path.Combine(folder, item.Key);
                    var there = File.Exists(path);
                    var file = new JsonObject
                    {
                        ["name"] = item.Key,
                        ["kept"] = there ? Print(path) : null,
                        ["known"] = Service.Array(item.Value.Append(store == null ? null : Path.Combine(store, "files", item.Key)).
                            Where(File.Exists).Select(Print).Distinct())
                    };
                    if (there)
                    {
                        file["attributes"] = (int)File.GetAttributes(path);
                        file["created"] = File.GetCreationTimeUtc(path).Ticks;
                        file["written"] = File.GetLastWriteTimeUtc(path).Ticks;
                        Copy(path, Path.Combine(place, "files", item.Key));
                    }
                    else
                    {
                        for (var parent = Path.GetDirectoryName(item.Key); !string.IsNullOrEmpty(parent) && !Directory.Exists(Path.Combine(folder, parent)); parent = Path.GetDirectoryName(parent))
                        {
                            folders.Add(parent);
                        }
                    }

                    files.Add(file);
                }

                var stored = store != null && Directory.Exists(store);
                if (stored)
                {
                    foreach (var file in Plan.Files(store))
                    {
                        Copy(file, Path.Combine(place, "store", Path.GetRelativePath(store, file)));
                    }
                }

                // The list goes last: without it, there is nothing to put back.
                Directory.CreateDirectory(place);
                var list = new JsonObject
                {
                    ["folder"] = folder,
                    ["game"] = key,
                    ["store"] = storeKey,
                    ["date"] = DateTime.UtcNow.ToString("O"),
                    ["stored"] = stored,
                    ["folders"] = Service.Array(folders),
                    ["files"] = files
                };
                var temporary = Path.Combine(place, $"{ListFile}.tmp");
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
                {
                    stream.Write(Encoding.UTF8.GetBytes(list.ToJsonString()));
                    stream.Flush(true);
                }

                File.Move(temporary, Path.Combine(place, ListFile), true);
                return new Checkpoint(key, storeKey);
            }
            catch
            {
                Delete(place);
                throw;
            }
        }

        private static JsonObject Read(string key)
        {
            var path = Path.Combine(Place(key), ListFile);
            try
            {
                return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject list && list["folder"] != null && list["files"] is JsonArray ? list : null;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"What was kept of a game can't be read: {e.Message}");
                return null;
            }
        }

        /// <summary>The folder of a game whose last change was cut in the middle. Null when there's nothing to put back.</summary>
        public static string FolderOf(string key)
        {
            return string.IsNullOrEmpty(key) ? null : (string)Read(key)?["folder"];
        }

        /// <summary>It was cut while the files were being kept, before anything was changed.</summary>
        public static bool IsBroken(string key)
        {
            return Directory.Exists(Place(key)) && Read(key) == null;
        }

        public static void Clean(string key)
        {
            if (IsBroken(key))
            {
                Delete(Place(key));
            }
        }

        /// <summary>The client can't write over the files that can only be read, which some games have.</summary>
        public void Unlock(string folder, Plan plan)
        {
            foreach (var path in plan.Names.Select(a => Path.Combine(folder, a.Key)).Where(File.Exists))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }

        /// <summary>
        /// The originals that the client had before are the same ones now. When it can't put one of them back before an update
        /// (something has it open for a moment) it goes on, and it would take the file of OptiScaler for the original.
        /// </summary>
        public void CheckOriginals()
        {
            var store = Store(storeKey);
            var before = Path.Combine(Place(key), "store", "files");
            if (store == null)
            {
                return;
            }

            foreach (var file in Plan.Files(before))
            {
                var now = Path.Combine(store, "files", Path.GetRelativePath(before, file));
                if (File.Exists(now) && !IsSame(file, now))
                {
                    Copy(file, now);
                }
            }
        }

        /// <summary>
        /// The same when OptiScaler goes out: the client deletes its copies even if it couldn't put one of them back. Each one
        /// is looked at, with the name it has in the game and the one it has among the copies.
        /// </summary>
        public void PutOriginals(string folder, IEnumerable<KeyValuePair<string, string>> names)
        {
            foreach (var name in names)
            {
                var original = Path.Combine(Place(key), "store", "files", name.Value);
                var target = Path.Combine(folder, name.Key);
                for (var attempt = 1; File.Exists(original) && attempt <= 20; attempt++)
                {
                    try
                    {
                        if (!File.Exists(target) || !IsSame(original, target))
                        {
                            Overwrite(original, target);
                        }

                        break;
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        if (attempt == 20)
                        {
                            throw new ServiceException("gameopen", Path.GetFileName(target));
                        }

                        System.Threading.Thread.Sleep(250);
                    }
                }
            }
        }

        /// <summary>It ended well: there's nothing to go back to.</summary>
        public void End()
        {
            ended = true;
            Delete(Place(key));
        }

        public void Dispose()
        {
            if (ended)
            {
                return;
            }

            try
            {
                PutBack(key);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // It stays for the next time, and the panel says that it was left in the middle.
                Console.Error.WriteLine($"The game couldn't be put back as it was: {e.Message}");
            }
        }

        /// <summary>
        /// Everything as it was before the change. What someone else wrote since then isn't touched: its names come back.
        /// Null when there was nothing to put back. It can be cut too: the next time starts over.
        /// </summary>
        public static List<string> PutBack(string key)
        {
            var list = Read(key);
            if (list == null)
            {
                return null;
            }

            var place = Place(key);
            var folder = (string)list["folder"];
            var remaining = new List<string>();
            if (Directory.Exists(folder))
            {
                foreach (var file in ((JsonArray)list["files"]).OfType<JsonObject>())
                {
                    PutBack(folder, Path.Combine(place, "files"), file, remaining);
                }

                RemoveEmpty(Client.Strings(list["folders"]).Select(a => Path.Combine(folder, a)));
            }

            PutStore((string)list["store"] ?? key, Path.Combine(place, "store"), (bool?)list["stored"] == true);
            Delete(place);
            return remaining;
        }

        private static void PutBack(string folder, string files, JsonObject file, List<string> remaining)
        {
            var name = (string)file["name"];
            var target = Path.Combine(folder, name);
            var there = File.Exists(target);
            var print = (string)file["kept"];
            var ours = !there || IsOwn(name) || IsOurs(target, Client.Strings(file["known"]));
            if (print == null)
            {
                if (there && ours)
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                else if (there)
                {
                    remaining.Add(name);
                }

                return;
            }

            var kept = Path.Combine(files, name);
            if (there && File.Exists(kept) && IsSame(target, kept))
            {
                return;
            }

            if (!ours || !File.Exists(kept) || Print(kept) != print)
            {
                remaining.Add(name);
                return;
            }

            Overwrite(kept, target);
            File.SetCreationTimeUtc(target, new DateTime((long?)file["created"] ?? DateTime.UtcNow.Ticks, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(target, new DateTime((long?)file["written"] ?? DateTime.UtcNow.Ticks, DateTimeKind.Utc));
            File.SetAttributes(target, (FileAttributes)((int?)file["attributes"] ?? (int)FileAttributes.Normal));
        }

        /// <summary>The copies of the client, which say whether OptiScaler is in the game.</summary>
        private static void PutStore(string key, string kept, bool stored)
        {
            var store = Store(key);
            if (store == null)
            {
                return;
            }

            DeleteFolder(store);
            foreach (var file in stored ? Plan.Files(kept) : new List<string>())
            {
                Copy(file, Path.Combine(store, Path.GetRelativePath(kept, file)));
            }
        }

        /// <summary>The folders that were left with nothing inside, from the deepest one.</summary>
        public static void RemoveEmpty(IEnumerable<string> folders)
        {
            foreach (var folder in folders.OrderByDescending(a => a.Length))
            {
                try
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // An empty folder isn't worth more than this.
                    Console.Error.WriteLine($"An empty folder couldn't be removed: {e.Message}");
                }
            }
        }

        public static void Overwrite(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.Copy(source, target, true);
        }

        private static void DeleteFolder(string folder)
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Plan.Files(folder))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, true);
            }
        }

        public static bool IsOwn(string name)
        {
            var file = Path.GetFileName(name);
            return file.StartsWith("OptiScaler.", StringComparison.OrdinalIgnoreCase) || file.StartsWith("fakenvapi.", StringComparison.OrdinalIgnoreCase) ||
                ownNames.Contains(file, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>One of these files, whole or with its copy cut in the middle: what is missing at its end is zeros.</summary>
        public static bool IsOurs(string path, IEnumerable<string> prints)
        {
            var head = Head(path, out var length);
            if (head.All(a => a == 0))
            {
                return true;
            }

            var hash = Convert.ToHexString(SHA1.HashData(head));
            return prints.Select(a => a.Split('-')).Any(a => a.Length == 2 && a[1] == hash && long.TryParse(a[0], out var whole) && length <= whole);
        }

        /// <summary>How long a file is and how it starts: enough to know it among the ones of a package.</summary>
        public static string Print(string path)
        {
            var head = Head(path, out var length);
            return $"{length}-{Convert.ToHexString(SHA1.HashData(head))}";
        }

        private static byte[] Head(string path, out long length)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                length = stream.Length;
                var head = new byte[(int)Math.Min(length, HeadSize)];
                stream.ReadExactly(head);
                return head;
            }
        }

        public static bool IsSame(string one, string other)
        {
            using (var sha = SHA256.Create())
            using (var first = new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var second = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                return first.Length == second.Length && sha.ComputeHash(first).SequenceEqual(sha.ComputeHash(second));
            }
        }

        /// <summary>A copy that is on the disk when it ends, also if the computer goes off right after.</summary>
        private static void Copy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
            {
                input.CopyTo(output, 1 << 20);
                output.Flush(true);
            }

            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }

        private static void Delete(string place)
        {
            try
            {
                if (!Directory.Exists(place))
                {
                    return;
                }

                // Without its list, what may be left of it isn't anything anymore.
                for (var attempt = 1; File.Exists(Path.Combine(place, ListFile)); attempt++)
                {
                    try
                    {
                        File.Delete(Path.Combine(place, ListFile));
                    }
                    catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 20)
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }

                DeleteFolder(place);
                var root = Path.GetDirectoryName(place);
                if (!Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"What was kept of a game couldn't be removed: {e.Message}");
            }
        }
    }
}
