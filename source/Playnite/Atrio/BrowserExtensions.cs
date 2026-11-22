using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
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
    /// <summary>An extension of Chrome that the browser of Atrio can carry.</summary>
    public class BrowserExtensionInfo
    {
        /// <summary>Its identity in the Chrome Web Store, which its own servers expect.</summary>
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>The key of the text that says what it adds.</summary>
        public string DescriptionKey { get; set; }
        public string Homepage { get; set; }
    }

    /// <summary>
    /// The extensions of the browser of Atrio. They aren't part of Atrio: each one comes from the Chrome Web Store, the
    /// first time the person turns it on, and stays in the folder of Atrio. Their servers only answer the copy of the
    /// store, so the copy keeps the identity it has there.
    /// </summary>
    public static class BrowserExtensions
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static readonly SemaphoreSlim downloads = new SemaphoreSlim(1, 1);

        /// <summary>A copy is asked for again after this long, to get its new versions.</summary>
        public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(3);

        private const string StoreUrl = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion={0}&acceptformat=crx2,crx3&x=id%3D{1}%26uc";
        private const string ReadyFile = "atrio-extension.txt";

        public static readonly List<BrowserExtensionInfo> Catalog = new List<BrowserExtensionInfo>
        {
            new BrowserExtensionInfo
            {
                Id = "kdbmhfkmnlmbkgbabkdealhhbfhlmmon",
                Name = "SteamDB",
                DescriptionKey = "LOCAtrioExtensionSteamDb",
                Homepage = "https://steamdb.info/extension/"
            },
            new BrowserExtensionInfo
            {
                Id = "dnhpnfgdlenaccegplpojghhmaamnnfp",
                Name = "Augmented Steam",
                DescriptionKey = "LOCAtrioExtensionAugmentedSteam",
                Homepage = "https://augmentedsteam.com/"
            }
        };

        public static string RootFolder => Path.Combine(PlaynitePaths.ConfigRootPath, "browser", "extensions");

        public static BrowserExtensionInfo Find(string id)
        {
            return Catalog.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The folder of the copy ready to load, null without one.</summary>
        public static string GetInstalled(string id, string root = null)
        {
            var folder = ReadReady(id, root)?.Item1;
            return folder != null && File.Exists(Path.Combine(folder, "manifest.json")) ? folder : null;
        }

        /// <summary>The folder of the ready copy and when it was asked for.</summary>
        private static Tuple<string, DateTime> ReadReady(string id, string root)
        {
            try
            {
                var ready = Path.Combine(root ?? RootFolder, id, ReadyFile);
                if (!File.Exists(ready))
                {
                    return null;
                }

                var lines = File.ReadAllLines(ready);
                if (lines.Length < 2 || lines[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return null;
                }

                var checkedAt = DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : DateTime.MinValue;
                return Tuple.Create(Path.Combine(root ?? RootFolder, id, lines[0]), checkedAt);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read the extension {id}.");
                return null;
            }
        }

        /// <summary>
        /// The folder of a copy ready to load: the one kept, or a new one from the store when there's none or it's
        /// time to look for a new version. Without the store, the copy kept is still good.
        /// </summary>
        public static async Task<string> EnsureAsync(string id, string browserVersion, CancellationToken token, string root = null, Func<string, Task<byte[]>> download = null)
        {
            if (Find(id) == null)
            {
                return null;
            }

            await downloads.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var ready = ReadReady(id, root);
                var kept = ready != null && File.Exists(Path.Combine(ready.Item1, "manifest.json")) ? ready.Item1 : null;
                if (kept != null && DateTime.UtcNow - ready.Item2.ToUniversalTime() < CheckEvery)
                {
                    return kept;
                }

                try
                {
                    var url = string.Format(StoreUrl, GetMajor(browserVersion), id);
                    var data = download != null ? await download(url).ConfigureAwait(false) : await client.GetByteArrayAsync(url).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return Install(id, data, root);
                }
                // A store that takes too long ends in a cancellation too, without anyone cancelling.
                catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    logger.Warn(e, $"Failed to get the extension {id} from the Chrome Web Store.");
                    if (kept != null)
                    {
                        // The store will be asked again on the next day.
                        WriteReady(id, Path.GetFileName(kept), DateTime.UtcNow - CheckEvery + TimeSpan.FromDays(1), root);
                    }

                    return kept;
                }
            }
            finally
            {
                downloads.Release();
            }
        }

        /// <summary>Unpacks a copy of the store next to the others and makes it the one to load.</summary>
        public static string Install(string id, byte[] crx, string root = null)
        {
            var package = ReadPackage(crx, id);
            var manifestText = ReadManifest(package.Item1);
            JObject manifest;
            try
            {
                manifest = JObject.Parse(manifestText);
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                throw new InvalidDataException("The manifest of the extension can't be read.", e);
            }

            var version = (string)manifest["version"];
            if (version.IsNullOrWhiteSpace() || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(".."))
            {
                throw new InvalidDataException("The extension has no valid version.");
            }

            var parent = Path.Combine(root ?? RootFolder, id);
            var folder = Path.Combine(parent, version);
            var ready = ReadReady(id, root);
            if (ready == null || !string.Equals(ready.Item1, folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(folder, "manifest.json")))
            {
                var temp = Path.Combine(parent, "new-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    Extract(package.Item1, temp);

                    // The key of the store gives the copy the identity of the store.
                    manifest["key"] = Convert.ToBase64String(package.Item2);
                    File.WriteAllText(Path.Combine(temp, "manifest.json"), manifest.ToString(), new UTF8Encoding(false));
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }

                    Directory.Move(temp, folder);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
            }

            WriteReady(id, version, DateTime.UtcNow, root);
            RemoveOld(parent, folder);
            logger.Info($"Extension {id} {version} ready.");
            return folder;
        }

        private static void WriteReady(string id, string folderName, DateTime checkedAt, string root)
        {
            var parent = Path.Combine(root ?? RootFolder, id);
            Directory.CreateDirectory(parent);
            File.WriteAllLines(Path.Combine(parent, ReadyFile), new[] { folderName, checkedAt.ToString("o") });
        }

        /// <summary>The versions before stay one more time: the browser may still be using them.</summary>
        private static void RemoveOld(string parent, string current)
        {
            try
            {
                var old = Directory.GetDirectories(parent).
                    Where(a => !string.Equals(a, current, StringComparison.OrdinalIgnoreCase)).
                    OrderByDescending(a => Directory.GetLastWriteTimeUtc(a)).
                    Skip(1);
                foreach (var folder in old)
                {
                    TryDelete(folder);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to remove old versions of an extension.");
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to delete {folder}.");
            }
        }

        public static string GetMajor(string browserVersion)
        {
            var major = (browserVersion ?? string.Empty).Split('.')[0];
            return int.TryParse(major, out var number) && number > 0 ? number + ".0" : "130.0";
        }

        /// <summary>The identity Chrome gives to the extension signed with a key.</summary>
        public static string GetId(byte[] publicKey)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(publicKey);
                var id = new StringBuilder(32);
                for (var i = 0; i < 16; i++)
                {
                    id.Append((char)('a' + (hash[i] >> 4)));
                    id.Append((char)('a' + (hash[i] & 0xF)));
                }

                return id.ToString();
            }
        }

        /// <summary>
        /// The files and the key of a package of the store (CRX3). The key has to be the one of that identity:
        /// a package of something else is refused.
        /// </summary>
        public static Tuple<byte[], byte[]> ReadPackage(byte[] crx, string id)
        {
            if (crx == null || crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            {
                throw new InvalidDataException("It's not a package of an extension.");
            }

            var format = BitConverter.ToUInt32(crx, 4);
            if (format != 3)
            {
                throw new InvalidDataException($"Unknown package format {format}.");
            }

            var headerLength = BitConverter.ToUInt32(crx, 8);
            if (headerLength > crx.Length - 12)
            {
                throw new InvalidDataException("The package is cut.");
            }

            byte[] key = null;
            foreach (var proof in ReadFields(crx, 12, (int)headerLength).Where(a => a.Item1 == 2))
            {
                foreach (var field in ReadFields(crx, proof.Item2, proof.Item3).Where(a => a.Item1 == 1))
                {
                    var candidate = new byte[field.Item3];
                    Buffer.BlockCopy(crx, field.Item2, candidate, 0, field.Item3);
                    if (GetId(candidate) == id)
                    {
                        key = candidate;
                    }
                }
            }

            if (key == null)
            {
                throw new InvalidDataException($"The package isn't signed as {id}.");
            }

            var start = 12 + (int)headerLength;
            var zip = new byte[crx.Length - start];
            Buffer.BlockCopy(crx, start, zip, 0, zip.Length);
            return Tuple.Create(zip, key);
        }

        /// <summary>The fields of a protocol buffer that hold bytes: number, start and length.</summary>
        private static IEnumerable<Tuple<int, int, int>> ReadFields(byte[] data, int start, int length)
        {
            var end = start + length;
            var i = start;
            while (i < end)
            {
                var tag = ReadVarint(data, ref i, end);
                var number = (int)(tag >> 3);
                var type = (int)(tag & 7);
                switch (type)
                {
                    case 0:
                        ReadVarint(data, ref i, end);
                        break;
                    case 1:
                        i += 8;
                        break;
                    case 2:
                        var size = ReadVarint(data, ref i, end);
                        if (size > (ulong)(end - i))
                        {
                            throw new InvalidDataException("The header of the package is cut.");
                        }

                        yield return Tuple.Create(number, i, (int)size);
                        i += (int)size;
                        break;
                    case 5:
                        i += 4;
                        break;
                    default:
                        throw new InvalidDataException("The header of the package can't be read.");
                }
            }
        }

        private static ulong ReadVarint(byte[] data, ref int i, int end)
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (i >= end)
                {
                    throw new InvalidDataException("The header of the package is cut.");
                }

                var b = data[i++];
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return result;
                }
            }

            throw new InvalidDataException("The header of the package can't be read.");
        }

        private static string ReadManifest(byte[] zip)
        {
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The extension has no manifest.");
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>Unpacks the files, none of them outside the folder.</summary>
        private static void Extract(byte[] zip, string folder)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // The signatures of the store: Chrome refuses to load a folder that has them.
                    if (entry.FullName.StartsWith("_metadata/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = Path.GetFullPath(Path.Combine(folder, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"The extension has a file outside its folder: {entry.FullName}.");
                    }

                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }
    }
}
