using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Tests
{
    [TestFixture]
    public class BrowserExtensionsTests
    {
        // The public key of SteamDB in the Chrome Web Store, as its package carries it.
        private const string SteamDbKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAvaD4yBXCoxl7bgn2OV/y2dnd/md9Lh8xYXSI9xAd9YDS+NRMd2t03MU6fHRgXRm4p944bm/PL6HevxnkQTL+RVbiqknuB+TG21um/F0Ow6eEDBfHIYKBWU78nNRMbwAfCwBRo/ZGCHENMFDgpE7rPWLlW46vTE/h87k/Zfdg3jxTL2R4uOGNVvkUxEdCyCYWXpCfQdAXJpk9Zx6/OvsicieH3p8qZeVuwMs1PVJTERXfazVxbr9XGIvjm5H1uz7b+3md8SEO33mkjMmmOwr24XlitJOWVMnX/ArJnXxiE59MUwCWMbU8g1aXyaweKjuBc5iH2Megmh1ZdqkN6PLpTwIDAQAB";

        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "atrio-ext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }

        private static byte[] Field(int number, byte[] value)
        {
            var result = new List<byte>();
            void varint(ulong v)
            {
                while (v >= 0x80)
                {
                    result.Add((byte)(v | 0x80));
                    v >>= 7;
                }

                result.Add((byte)v);
            }

            varint((ulong)((number << 3) | 2));
            varint((ulong)value.Length);
            result.AddRange(value);
            return result.ToArray();
        }

        private static byte[] Zip(Dictionary<string, string> files)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    foreach (var file in files)
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry(file.Key).Open()))
                        {
                            writer.Write(file.Value);
                        }
                    }
                }

                return memory.ToArray();
            }
        }

        /// <summary>A package like the ones of the store: signed with the key given and with another one.</summary>
        private static byte[] Crx(byte[] key, Dictionary<string, string> files)
        {
            var other = Encoding.ASCII.GetBytes("a key of the store, not of the extension");
            var header = Field(2, Field(1, other).Concat(Field(2, new byte[] { 1, 2, 3 })).ToArray()).
                Concat(Field(2, Field(1, key).Concat(Field(2, new byte[] { 4, 5 })).ToArray())).
                Concat(Field(10000, new byte[] { 9, 9, 9 })).ToArray();
            var zip = Zip(files);
            return Encoding.ASCII.GetBytes("Cr24").Concat(BitConverter.GetBytes(3u)).Concat(BitConverter.GetBytes((uint)header.Length)).
                Concat(header).Concat(zip).ToArray();
        }

        private static Dictionary<string, string> Files(string version)
        {
            return new Dictionary<string, string>
            {
                { "manifest.json", "{ \"manifest_version\": 3, \"name\": \"Prueba\", \"version\": \"" + version + "\" }" },
                { "scripts/app.js", "console.log('x');" },
                { "_metadata/verified_contents.json", "[]" }
            };
        }

        [Test]
        public void TheIdentityComesFromTheKeyLikeInChromeTest()
        {
            Assert.AreEqual("kdbmhfkmnlmbkgbabkdealhhbfhlmmon", BrowserExtensions.GetId(Convert.FromBase64String(SteamDbKey)));
            Assert.IsNotNull(BrowserExtensions.Find("KDBMHFKMNLMBKGBABKDEALHHBFHLMMON"));
        }

        [Test]
        public void ThePackageKeepsTheIdentityOfTheStoreTest()
        {
            var key = Encoding.ASCII.GetBytes("the key of this extension");
            var id = BrowserExtensions.GetId(key);
            var folder = BrowserExtensions.Install(id, Crx(key, Files("1.2.3")), root);

            Assert.AreEqual(Path.Combine(root, id, "1.2.3"), folder);
            Assert.AreEqual(folder, BrowserExtensions.GetInstalled(id, root));
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            Assert.AreEqual(Convert.ToBase64String(key), (string)manifest["key"]);
            Assert.AreEqual("Prueba", (string)manifest["name"]);
            Assert.IsTrue(File.Exists(Path.Combine(folder, "scripts", "app.js")));
            Assert.IsFalse(Directory.Exists(Path.Combine(folder, "_metadata")), "Chrome refuses a folder with the signatures of the store.");
        }

        [Test]
        public void APackageOfAnotherExtensionIsRefusedTest()
        {
            var key = Encoding.ASCII.GetBytes("the key of this extension");
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.Install("kdbmhfkmnlmbkgbabkdealhhbfhlmmon", Crx(key, Files("1.0")), root));
            Assert.IsNull(BrowserExtensions.GetInstalled("kdbmhfkmnlmbkgbabkdealhhbfhlmmon", root));
        }

        [Test]
        public void BrokenPackagesAreRefusedTest()
        {
            var key = Encoding.ASCII.GetBytes("k");
            var id = BrowserExtensions.GetId(key);
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.ReadPackage(null, id));
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.ReadPackage(Encoding.ASCII.GetBytes("PK\u0003\u0004 not a crx at all"), id));
            var good = Crx(key, Files("1.0"));
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.ReadPackage(good.Take(20).ToArray(), id));
            var wrongFormat = (byte[])good.Clone();
            wrongFormat[4] = 2;
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.ReadPackage(wrongFormat, id));
        }

        [Test]
        public void NoFileGoesOutsideTheFolderTest()
        {
            var key = Encoding.ASCII.GetBytes("the key of this extension");
            var id = BrowserExtensions.GetId(key);
            var files = Files("1.0");
            files["../../fuera.txt"] = "x";
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.Install(id, Crx(key, files), root));
            Assert.IsFalse(File.Exists(Path.Combine(root, "fuera.txt")));
            Assert.IsFalse(Directory.GetDirectories(Path.Combine(root, id)).Any(a => Path.GetFileName(a).StartsWith("new-")), "The half unpacked copy goes away.");
            Assert.IsNull(BrowserExtensions.GetInstalled(id, root));
        }

        [Test]
        public void AVersionThatIsAPathIsRefusedTest()
        {
            var key = Encoding.ASCII.GetBytes("the key of this extension");
            var id = BrowserExtensions.GetId(key);
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.Install(id, Crx(key, Files("../x")), root));
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.Install(id, Crx(key, Files("..\\..\\x")), root), "A manifest that is not JSON.");
            Assert.Throws<InvalidDataException>(() => BrowserExtensions.Install(id, Crx(key, Files("")), root));
        }

        [Test]
        public void NewVersionsReplaceTheOldOnesTest()
        {
            var key = Encoding.ASCII.GetBytes("the key of this extension");
            var id = BrowserExtensions.GetId(key);
            BrowserExtensions.Install(id, Crx(key, Files("1.0")), root);
            BrowserExtensions.Install(id, Crx(key, Files("1.1")), root);
            var newest = BrowserExtensions.Install(id, Crx(key, Files("1.2")), root);

            Assert.AreEqual(newest, BrowserExtensions.GetInstalled(id, root));
            var versions = Directory.GetDirectories(Path.Combine(root, id)).Select(Path.GetFileName).OrderBy(a => a).ToList();
            CollectionAssert.AreEqual(new[] { "1.1", "1.2" }, versions, "The one before stays once, the browser may be using it.");

            // The same version again changes nothing.
            Assert.AreEqual(newest, BrowserExtensions.Install(id, Crx(key, Files("1.2")), root));
        }

        [Test]
        public async Task TheStoreIsAskedOnlyFromTimeToTimeTest()
        {
            // An extension of the list: the others aren't downloaded.
            var id = "kdbmhfkmnlmbkgbabkdealhhbfhlmmon";
            var key = Convert.FromBase64String(SteamDbKey);
            var asked = new List<string>();
            Func<string, Task<byte[]>> store = url =>
            {
                asked.Add(url);
                return Task.FromResult(Crx(key, Files("4.37")));
            };

            var first = await BrowserExtensions.EnsureAsync(id, "157.0.4341.60", CancellationToken.None, root, store);
            var second = await BrowserExtensions.EnsureAsync(id, "157.0.4341.60", CancellationToken.None, root, store);

            Assert.AreEqual(first, second);
            Assert.AreEqual(1, asked.Count);
            StringAssert.Contains("prodversion=157.0", asked[0]);
            StringAssert.Contains("id%3D" + id, asked[0]);
            Assert.IsNull(await BrowserExtensions.EnsureAsync("aaaabbbbccccddddeeeeffffgggghhhh", "157", CancellationToken.None, root, store), "Only the extensions of the list.");
            Assert.AreEqual(1, asked.Count);
        }

        [Test]
        public async Task WithoutTheStoreTheCopyKeptStaysGoodTest()
        {
            var id = "kdbmhfkmnlmbkgbabkdealhhbfhlmmon";
            var key = Convert.FromBase64String(SteamDbKey);
            var kept = BrowserExtensions.Install(id, Crx(key, Files("4.36")), root);

            // Old enough to look for a new version.
            File.WriteAllLines(Path.Combine(root, id, "atrio-extension.txt"), new[] { "4.36", DateTime.UtcNow.AddDays(-10).ToString("o") });
            var failures = 0;
            Func<string, Task<byte[]>> offline = _ =>
            {
                failures++;
                throw new IOException("sin red");
            };

            Assert.AreEqual(kept, await BrowserExtensions.EnsureAsync(id, null, CancellationToken.None, root, offline));
            Assert.AreEqual(1, failures);

            // Not asked again right away after a failure.
            Assert.AreEqual(kept, await BrowserExtensions.EnsureAsync(id, null, CancellationToken.None, root, offline));
            Assert.AreEqual(1, failures);

            // Nothing kept and no store: nothing to load.
            Assert.IsNull(await BrowserExtensions.EnsureAsync("dnhpnfgdlenaccegplpojghhmaamnnfp", null, CancellationToken.None, root, offline));
        }

        [Test]
        public void TheMajorVersionOfTheEngineTest()
        {
            Assert.AreEqual("157.0", BrowserExtensions.GetMajor("157.0.4341.60"));
            Assert.AreEqual("130.0", BrowserExtensions.GetMajor(null));
            Assert.AreEqual("130.0", BrowserExtensions.GetMajor("beta"));
        }

        private static BridgeCookie Cookie(string domain, string name, string value, DateTime? expires = null)
        {
            return new BridgeCookie { Domain = domain, Name = name, Value = value, Path = "/", Expires = expires };
        }

        [Test]
        public void EachSideGetsWhatChangedOnTheOtherTest()
        {
            var before = CookieSync.Picture(new[]
            {
                Cookie(".steampowered.com", "steamLoginSecure", "old"),
                Cookie("store.steampowered.com", "sessionid", "a"),
                Cookie("steamcommunity.com", "gone", "x"),
                Cookie("gog.com", "same", "1", new DateTime(2027, 1, 1))
            });
            var now = new[]
            {
                Cookie(".steampowered.com", "steamLoginSecure", "new"),
                Cookie("STORE.steampowered.com", "sessionid", "a"),
                Cookie("gog.com", "same", "1", new DateTime(2028, 1, 1)),
                Cookie("epicgames.com", "EPIC_SESSION", "s"),
                Cookie("", "no domain", "x"),
                Cookie("x.com", "", "no name")
            };

            var changes = CookieSync.Changes(now, before);
            CollectionAssert.AreEquivalent(new[] { "steamLoginSecure", "same", "EPIC_SESSION" }, changes.Item1.Select(a => a.Name));
            CollectionAssert.AreEquivalent(new[] { "steamcommunity.com|/|gone" }, changes.Item2);
            var gone = CookieSync.FromKey(changes.Item2[0]);
            Assert.AreEqual("steamcommunity.com", gone.Domain);
            Assert.AreEqual("/", gone.Path);
            Assert.AreEqual("gone", gone.Name);

            // Nothing changed: nothing to pass.
            var none = CookieSync.Changes(now, CookieSync.Picture(now));
            Assert.AreEqual(0, none.Item1.Count);
            Assert.AreEqual(0, none.Item2.Count);
        }

        [Test]
        public void ThePictureKeepsNoValuesTest()
        {
            var picture = CookieSync.Picture(new[] { Cookie(".steampowered.com", "steamLoginSecure", "76561198000000000%7C%7CeyJ0eXAiOiJKV1Qi") });
            var fingerprint = picture[".steampowered.com|/|steamLoginSecure"];
            StringAssert.DoesNotContain("7656119", fingerprint);
            StringAssert.DoesNotContain("eyJ0eX", fingerprint);
            Assert.AreNotEqual(fingerprint, CookieSync.Fingerprint(Cookie(".steampowered.com", "steamLoginSecure", "other")));
        }

        [Test]
        public void OnlyWhatWasPassedChangesInThePictureOfTheOtherSideTest()
        {
            // What the other side had when it was last seen.
            var other = CookieSync.Picture(new[]
            {
                Cookie("store.steampowered.com", "sessionid", "a"),
                Cookie(".steampowered.com", "steamLoginSecure", "old"),
                Cookie("gog.com", "gone", "x")
            });

            // Meanwhile, a page there changed its sessionid; now steamLoginSecure is passed and "gone" deleted, and
            // the engine there refused a new cookie.
            var written = new[] { Cookie(".steampowered.com", "steamLoginSecure", "new"), Cookie("x.com", "__Host-refused", "1") };
            var deleted = new[] { "gog.com|/|gone" };
            var thereNow = new[] { Cookie("store.steampowered.com", "sessionid", "changed there"), Cookie(".steampowered.com", "steamLoginSecure", "new") };
            CookieSync.Update(other, written, deleted, thereNow);

            var pending = CookieSync.Changes(thereNow, other);
            CollectionAssert.AreEquivalent(new[] { "sessionid" }, pending.Item1.Select(a => a.Name), "The change made there still has to come back.");
            Assert.AreEqual(0, pending.Item2.Count, "The refused cookie isn't taken as gone from there.");
            Assert.IsFalse(other.ContainsKey("x.com|/|__Host-refused"));
            Assert.IsFalse(other.ContainsKey("gog.com|/|gone"));
        }

        [Test]
        public void ClosingTheBrowserIsNotSigningOutTest()
        {
            var before = CookieSync.Picture(new[]
            {
                Cookie("store.steampowered.com", "sessionid", "a"),
                Cookie(".steampowered.com", "steamLoginSecure", "token", new DateTime(2027, 1, 1))
            });
            StringAssert.StartsWith(CookieSync.SessionMark, before["store.steampowered.com|/|sessionid"]);
            StringAssert.DoesNotStartWith(CookieSync.SessionMark, before[".steampowered.com|/|steamLoginSecure"]);

            // After a restart both are gone: only the one with a date was taken away on purpose.
            var gone = CookieSync.Changes(new BridgeCookie[0], before).Item2;
            CollectionAssert.AreEquivalent(new[] { ".steampowered.com|/|steamLoginSecure" }, CookieSync.WithoutClosedSession(gone, before));
        }

        [Test]
        public void ARefusedChangeKeepsTheValueThatIsThereTest()
        {
            var other = CookieSync.Picture(new[] { Cookie(".a.com", "s", "old") });

            // The engine there kept its old value: it isn't sent back as a change of that side.
            CookieSync.Update(other, new[] { Cookie(".a.com", "s", "new") }, new string[0], new[] { Cookie(".a.com", "s", "old") });
            Assert.AreEqual(0, CookieSync.Changes(new[] { Cookie(".a.com", "s", "old") }, other).Item1.Count);
        }

        [Test]
        public void ACookieOfOneHostIsNotOneOfItsDomainTest()
        {
            var host = Cookie("steamcommunity.com", "sessionid", "a");
            var domain = Cookie(".steamcommunity.com", "sessionid", "a");
            Assert.AreNotEqual(host.Key, domain.Key);
            Assert.AreEqual("https://steamcommunity.com/", CookieSync.GetUrl(new BridgeCookie { Domain = ".steamcommunity.com", Path = "/", Secure = true }));
            Assert.AreEqual("http://a.com/x", CookieSync.GetUrl(new BridgeCookie { Domain = "a.com", Path = "/x" }));
        }
    }
}
