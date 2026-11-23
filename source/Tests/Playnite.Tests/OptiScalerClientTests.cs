using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Tests
{
    [TestFixture]
    public class OptiScalerClientTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), $"atrio-optiscaler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            OptiScalerClient.TestRoot = root;
            OptiScalerClient.ForgetState();
        }

        [TearDown]
        public void TearDown()
        {
            OptiScalerClient.TestRoot = null;
            OptiScalerClient.TestDownloads = null;
            OptiScalerClient.ForgetState();
            Directory.Delete(root, true);
        }

        private static string Asset(string name, string hash)
        {
            return $@"{{""name"":""{name}"",""digest"":{(hash == null ? "null" : $@"""sha256:{hash}""")},""size"":10,""browser_download_url"":""https://github.com/x/{name}""}}";
        }

        [Test]
        public void ReleasesTest()
        {
            var json = $@"[
                {{""tag_name"":""OptiscalerClient-1.0.9-pre"",""prerelease"":true,""published_at"":""2026-12-01T00:00:00Z"",""assets"":[{Asset("OptiscalerClient-1.0.9-win-x64.zip", "AA")}]}},
                {{""tag_name"":""OptiscalerClient-1.0.7.2"",""prerelease"":false,""published_at"":""2026-09-23T00:00:00Z"",""assets"":[{Asset("OptiscalerClient-1.0.7.2-linux-x64.zip", "bb")},{Asset("OptiscalerClient-1.0.7.2-win-x64.zip", "CC")}]}},
                {{""tag_name"":""OptiscalerClient-1.0.8"",""draft"":false,""prerelease"":false,""html_url"":""https://github.com/x"",""assets"":[{Asset("OptiscalerClient-1.0.8-win-x64.zip", "dd")}]}},
                {{""tag_name"":""OptiscalerClient-1.0.6"",""assets"":[{Asset("OptiscalerClient-1.0.6-windows-x64.zip", null)}]}},
                {{""tag_name"":""OptiscalerClient-1.0.4"",""assets"":[{Asset("OptiscalerClient_1.0.4_Portable.zip", "ee")}]}}]";
            var hashes = new Dictionary<string, string>();
            var releases = OptiScalerClient.ParseReleases(json, hashes);
            Assert.AreEqual(new[] { "1.0.8", "1.0.7.2", "1.0.6", "1.0.4" }, releases.Select(a => a.Version).ToArray());
            Assert.AreEqual("cc", releases[1].Hash);
            Assert.IsNull(releases[2].Hash);
            Assert.AreEqual("https://github.com/x/OptiscalerClient-1.0.8-win-x64.zip", releases[0].Url);
            CollectionAssert.AreEquivalent(new[] { "aa", "cc", "dd", "ee" }, hashes.Keys, "The test versions are known packages too.");
        }

        [Test]
        public void VersionsTest()
        {
            Assert.AreEqual(new Version(1, 0, 8, 0), OptiScalerClient.ParseVersion("OptiscalerClient-1.0.8"));
            Assert.AreEqual(new Version(1, 0, 7, 2), OptiScalerClient.ParseVersion("OptiscalerClient-1.0.7.2"));
            Assert.AreEqual(new Version(1, 0, 8, 0), OptiScalerClient.ParseVersion("OptiscalerClient-1.0.8-win-x64.zip"));
            Assert.IsNull(OptiScalerClient.ParseVersion("OptiscalerClient-1.0.9-pre"));
            Assert.IsNull(OptiScalerClient.ParseVersion("latest"));
            Assert.AreEqual("1.0.8", OptiScalerClient.FormatVersion(new Version(1, 0, 8, 0)));
            Assert.AreEqual("1.0.7.2", OptiScalerClient.FormatVersion(new Version(1, 0, 7, 2)));
            Assert.Greater(OptiScalerClient.ParseVersion("1.0.10"), OptiScalerClient.ParseVersion("1.0.9.5"));
            Assert.Greater(OptiScalerClient.ParseVersion("OptiscalerClient-1.0.2b-win-x64.zip"), OptiScalerClient.ParseVersion("OptiscalerClient-1.0.2-win-x64.zip"));
            Assert.AreEqual(new Version(1, 0, 8, 0), OptiScalerClient.ParseVersion("OptiscalerClient-1.0.8win"));
        }

        private static OptiScalerCopy Copy(string version)
        {
            return new OptiScalerCopy { Number = Version.Parse(version + ".0"), Executable = $@"C:\x\{version}\OptiscalerClient.exe" };
        }

        [Test]
        public void ANewVersionThatDoesNotWorkIsNotUsedTest()
        {
            var copies = new[] { Copy("1.0.8"), Copy("1.0.9"), Copy("1.0.7") };
            var contracts = new Dictionary<string, OptiScalerContract>
            {
                { "1.0.8", new OptiScalerContract { Usable = true } },
                { "1.0.9", new OptiScalerContract { Usable = false } }
            };
            OptiScalerContract find(OptiScalerCopy copy) => contracts.TryGetValue(copy.Version, out var contract) ? contract : null;
            Assert.AreEqual("1.0.8", OptiScalerClient.Choose(copies, find).Version);

            // Once Atrio understands it, the newest one.
            contracts["1.0.9"].Usable = true;
            Assert.AreEqual("1.0.9", OptiScalerClient.Choose(copies, find).Version);

            // Without any that works, one never tried, and then the newest: its window opens anyway.
            contracts["1.0.8"].Usable = false;
            contracts["1.0.9"].Usable = false;
            Assert.AreEqual("1.0.7", OptiScalerClient.Choose(copies, find).Version);
            contracts["1.0.7"] = new OptiScalerContract();
            Assert.AreEqual("1.0.9", OptiScalerClient.Choose(copies, find).Version);
            Assert.IsNull(OptiScalerClient.Choose(new OptiScalerCopy[0], find));
        }

        [Test]
        public void ANewVersionThatDoesLessIsNotUsedTest()
        {
            var copies = new[] { Copy("1.0.8"), Copy("1.0.9"), Copy("1.1.0") };
            var contracts = new Dictionary<string, OptiScalerContract>
            {
                { "1.0.8", new OptiScalerContract { Usable = true, Functions = new List<string> { "state", "install", "frames" } } },
                { "1.0.9", new OptiScalerContract { Usable = true, Functions = new List<string> { "state", "install", "frames", "new" } } },
                { "1.1.0", new OptiScalerContract { Usable = true, Functions = new List<string> { "state", "install", "new" } } }
            };
            OptiScalerContract find(OptiScalerCopy copy) => contracts.TryGetValue(copy.Version, out var contract) ? contract : null;
            Assert.AreEqual("1.0.9", OptiScalerClient.Choose(copies, find).Version, "The newest one has lost something that worked.");
            contracts["1.1.0"].Functions.Add("frames");
            Assert.AreEqual("1.1.0", OptiScalerClient.Choose(copies, find).Version);

            // Alone, what it does is better than nothing.
            contracts["1.1.0"].Functions.Remove("frames");
            Assert.AreEqual("1.1.0", OptiScalerClient.Choose(new[] { copies[2] }, find).Version);
        }

        [Test]
        public void AProblemThatPassesTest()
        {
            Assert.IsFalse(new OptiScalerContract { Usable = true }.IsOver);
            Assert.IsFalse(new OptiScalerContract { Until = DateTime.UtcNow.AddHours(1) }.IsOver);
            Assert.IsTrue(new OptiScalerContract { Until = DateTime.UtcNow.AddHours(-1) }.IsOver);
        }

        [Test]
        public void ContractTest()
        {
            var contract = OptiScalerClient.ParseContract(JObject.Parse(@"{""usable"":true,""functions"":[""state"",""install""],""missing"":{""frames"":[""GameInstallationService.ApplyFrameGenerationSettings""]}}"));
            Assert.IsTrue(contract.Usable);
            CollectionAssert.AreEqual(new[] { "state", "install" }, contract.Functions);
            Assert.AreEqual(1, contract.Missing["frames"].Count);
            Assert.IsFalse(OptiScalerClient.ParseContract(new JObject()).Usable);
            Assert.IsFalse(OptiScalerClient.ParseContract(null).Usable);
        }

        private string MakePackage(string name, params string[] entries)
        {
            var path = Path.Combine(root, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                    {
                        writer.Write(entry);
                    }
                }
            }

            return path;
        }

        [Test]
        public void UnpackTest()
        {
            Directory.CreateDirectory(OptiScalerClient.Root);
            var copy = OptiScalerClient.Unpack(MakePackage("OptiscalerClient-1.2.3.zip", "OptiscalerClient.exe", "assets/configs/a.json"), true);
            Assert.AreEqual("1.2.3", copy.Version);
            Assert.IsTrue(File.Exists(Path.Combine(OptiScalerClient.Root, "1.2.3", "assets", "configs", "a.json")));

            // Inside a folder, and over the same version again.
            copy = OptiScalerClient.Unpack(MakePackage("OptiscalerClient-1.2.3.1.zip", "Optiscaler/OptiscalerClient.exe", "Optiscaler/b.dll", "readme.txt"), false);
            Assert.AreEqual("1.2.3.1", copy.Version);
            Assert.IsTrue(File.Exists(Path.Combine(copy.Folder, "b.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(copy.Folder, "readme.txt")));
            OptiScalerClient.Unpack(Path.Combine(root, "OptiscalerClient-1.2.3.zip"), true);
            Assert.AreEqual(new[] { "1.2.3.1", "1.2.3" }, OptiScalerClient.GetCopies().Select(a => a.Version).ToArray());
            Assert.AreEqual(0, Directory.GetDirectories(OptiScalerClient.Root, ".*").Length, "Nothing half done stays.");

            // The published number is the one that counts, not the one of the file.
            copy = OptiScalerClient.Unpack(Path.Combine(root, "OptiscalerClient-1.2.3.zip"), true, new Version(1, 2, 3, 2));
            Assert.AreEqual("1.2.3.2", copy.Version);
            Assert.AreEqual(new[] { "1.2.3.2", "1.2.3.1", "1.2.3" }, OptiScalerClient.GetCopies().Select(a => a.Version).ToArray());
        }

        [Test]
        public void UnpackOnlyInsideItsFolderTest()
        {
            Directory.CreateDirectory(OptiScalerClient.Root);
            Assert.Throws<InvalidDataException>(() => OptiScalerClient.Unpack(MakePackage("OptiscalerClient-9.9.9.zip", "OptiscalerClient.exe", "../../evil.dll"), true));
            Assert.Throws<InvalidDataException>(() => OptiScalerClient.Unpack(MakePackage("other-9.9.8.zip", "other.exe"), true));
            Assert.IsFalse(File.Exists(Path.Combine(root, "evil.dll")));
            Assert.AreEqual(0, OptiScalerClient.GetCopies().Count);
            Assert.AreEqual(0, Directory.GetDirectories(OptiScalerClient.Root).Length);
        }

        private static string Hash(string file)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private void Know(params string[] packages)
        {
            Directory.CreateDirectory(OptiScalerClient.Root);
            var hashes = new JObject();
            foreach (var package in packages)
            {
                hashes[Hash(package)] = Path.GetFileName(package);
            }

            // Asked a moment ago: GitHub isn't asked again for what it doesn't know, because there's no need to.
            File.WriteAllText(
                Path.Combine(OptiScalerClient.Root, "atrio-optiscaler.json"),
                new JObject { ["KnownHashes"] = hashes, ["ReleasesDate"] = DateTime.UtcNow }.ToString());
            OptiScalerClient.ForgetState();
        }

        [Test]
        public void DownloadedPackagesTest()
        {
            var downloads = Path.Combine(root, "Downloads");
            Directory.CreateDirectory(Path.Combine(downloads, "Compressed"));
            OptiScalerClient.TestDownloads = downloads;
            Assert.AreEqual(0, OptiScalerClient.GetDownloadedPackages().Count);

            // Only the ones named like the client, wherever the download manager puts them, and the newest first.
            MakePackage(@"Downloads\OptiscalerClient-1.0.8-win-x64.zip", "OptiscalerClient.exe", "a.dll");
            MakePackage(@"Downloads\Compressed\OptiscalerClient-1.0.10-win-x64 (1).zip", "OptiscalerClient.exe", "b.dll");
            MakePackage(@"Downloads\OptiScaler_0.9.5.zip", "OptiScaler.dll");
            MakePackage(@"Downloads\holiday.zip", "a.jpg");
            Assert.AreEqual(
                new[] { "OptiscalerClient-1.0.10-win-x64 (1).zip", "OptiscalerClient-1.0.8-win-x64.zip" },
                OptiScalerClient.GetDownloadedPackages().Select(Path.GetFileName).ToArray());
            Assert.AreEqual(0, OptiScalerClient.GetOwnPackages().Count);
        }

        [Test]
        public async Task OnlyPublishedPackagesAreUsedTest()
        {
            var downloads = Path.Combine(root, "Downloads");
            Directory.CreateDirectory(downloads);
            OptiScalerClient.TestDownloads = downloads;
            var good = MakePackage(@"Downloads\OptiscalerClient-1.0.8-win-x64.zip", "OptiscalerClient.exe", "a.dll");
            var changed = MakePackage(@"Downloads\OptiscalerClient-1.0.7-win-x64.zip", "OptiscalerClient.exe", "evil.dll");
            Know(good);
            var strange = new List<string>();
            try
            {
                strange = await OptiScalerClient.ImportPackagesAsync(CancellationToken.None);
            }
            catch (OptiScalerException)
            {
                // Without internet the one that isn't known stays waiting.
            }

            Assert.AreEqual(new[] { "1.0.8" }, OptiScalerClient.GetCopies().Select(a => a.Version).ToArray());
            Assert.IsFalse(File.Exists(Path.Combine(OptiScalerClient.Root, "1.0.7", "evil.dll")));
            Assert.IsTrue(strange.Count == 0 || strange.SequenceEqual(new[] { "OptiscalerClient-1.0.7-win-x64.zip" }));
            Assert.IsTrue(File.Exists(good) && File.Exists(changed), "The files of the person stay where they are.");
            Assert.IsFalse(OptiScalerClient.GetDownloadedPackages().Contains(good), "It isn't taken out again.");

            // Removed on purpose: it doesn't come back by itself.
            Directory.Delete(Path.Combine(OptiScalerClient.Root, "1.0.8"), true);
            Assert.IsFalse(OptiScalerClient.GetDownloadedPackages().Contains(good));
        }

        [Test]
        public void RecordsTest()
        {
            var records = OptiScalerClient.ParseRecords(@"[
                {""Name"":""One"",""InstallPath"":""D:\\Games\\One\\"",""IsOptiscalerInstalled"":true,""OptiscalerVersion"":""0.9.5""},
                {""Name"":""Two"",""InstallPath"":""D:/Games/Two"",""IsOptiscalerInstalled"":true},
                {""Name"":""Three"",""InstallPath"":""D:\\Games\\Three"",""IsOptiscalerInstalled"":false,""OptiscalerVersion"":""0.9.4""},
                {""Name"":""Four""}, 5]");
            Assert.AreEqual(2, records.Count);
            Assert.AreEqual("0.9.5", records[@"d:\games\one"]);
            Assert.AreEqual("?", records[@"d:\games\two"]);

            // From the file of the client, and without it.
            Assert.IsNull(OptiScalerClient.GetRecordedVersion(@"D:\Games\One"));
            Directory.CreateDirectory(OptiScalerClient.CurrentDataFolder);
            var file = Path.Combine(OptiScalerClient.CurrentDataFolder, "games.json");
            File.WriteAllText(file, @"[{""InstallPath"":""D:\\Games\\One"",""IsOptiscalerInstalled"":true,""OptiscalerVersion"":""0.9.5""}]");
            Assert.AreEqual("0.9.5", OptiScalerClient.GetRecordedVersion(@"D:\Games\One\"));
            Assert.IsNull(OptiScalerClient.GetRecordedVersion(@"D:\Games\Other"));
            File.WriteAllText(file, "not json");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            Assert.IsNull(OptiScalerClient.GetRecordedVersion(@"D:\Games\One"));
        }

        [Test]
        public void ProgressTest()
        {
            Assert.IsTrue(new OptiScalerProgress { Step = "download" }.CanStop);
            Assert.IsFalse(new OptiScalerProgress { Step = "install" }.CanStop);
            Assert.IsFalse(new OptiScalerProgress { Step = "uninstall" }.CanStop);
            Assert.IsFalse(new OptiScalerProgress { Step = "download", Touched = true }.CanStop, "The game was already changed.");
        }
    }
}
