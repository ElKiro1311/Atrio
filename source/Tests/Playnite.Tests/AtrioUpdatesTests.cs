using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Playnite.Tests
{
    [TestFixture]
    public class AtrioUpdatesTests
    {
        private const string Releases = @"[
            {""tag_name"":""v1.3.0"",""draft"":true,""prerelease"":false,""assets"":[{""name"":""Atrio-1.3.0.zip"",""url"":""u"",""size"":1}]},
            {""tag_name"":""v1.2.1"",""draft"":false,""prerelease"":true,""assets"":[{""name"":""Atrio-1.2.1.zip"",""url"":""u"",""size"":1}]},
            {""tag_name"":""v1.1.0"",""html_url"":""page"",""body"":""News"",""draft"":false,""prerelease"":false,""assets"":[
                {""name"":""something-else.txt"",""url"":""wrong""},
                {""name"":""Atrio-1.1.0.zip"",""url"":""https://api.github.com/repos/a/b/releases/assets/5"",""size"":200,""digest"":""sha256:abc123""}]},
            {""tag_name"":""v1.2.0"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-1.2.0.zip"",""url"":""u"",""size"":1,""digest"":null}]},
            {""tag_name"":""v1.4.0"",""draft"":false,""prerelease"":false,""assets"":[]},
            {""tag_name"":""nightly"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-x.zip"",""url"":""u""}]}
        ]";

        [Test]
        public void OnlyPublishedVersionsWithTheirPackageCountTest()
        {
            var releases = AtrioUpdates.ParseReleases(Releases);

            CollectionAssert.AreEqual(new[] { new Version(1, 2, 0), new Version(1, 1, 0) }, releases.Select(a => a.Version));
            Assert.AreEqual("https://api.github.com/repos/a/b/releases/assets/5", releases[1].PackageUrl);
            Assert.AreEqual(200, releases[1].PackageSize);
            Assert.AreEqual("abc123", releases[1].PackageHash);
            Assert.AreEqual("News", releases[1].Notes);
            Assert.AreEqual("page", releases[1].PageUrl);
            Assert.IsNull(releases[0].PackageHash);
        }

        [Test]
        public void NotesComeInOneLanguageTest()
        {
            const string notes = "What's new.\n\n- **One thing.**\n\n<details>\n<summary><b>Español</b></summary>\n\nNovedades.\n\n- **Una cosa.**\n\n</details>\n";

            Assert.AreEqual("What's new.\n\n- **One thing.**", AtrioUpdates.GetNotes(notes, false));
            Assert.AreEqual("Novedades.\n\n- **Una cosa.**", AtrioUpdates.GetNotes(notes, true));
            Assert.AreEqual("What's new.\r\n- One thing.", AtrioUpdates.GetNotes("What's new.\r\n- One thing.\r\n\r\n<details>\r\n<summary>Español</summary>\r\n\r\nNovedades.\r\n\r\n</details>", false));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NotesWithOneLanguageComeAsTheyAreTest(bool spanish)
        {
            Assert.AreEqual("Novedades.\n\n- Una.", AtrioUpdates.GetNotes("Novedades.\n\n- Una.", spanish));
            Assert.AreEqual("A <details>block</details>", AtrioUpdates.GetNotes("A <details>block</details>", spanish));
            Assert.AreEqual(string.Empty, AtrioUpdates.GetNotes(null, spanish));
        }

        [TestCase("{\r\n  \"Theme\": \"x\",\r\n  \"Language\": \"es_ES\",\r\n  \"Other\": 1\r\n}", "es_ES")]
        [TestCase("{\n  \"Language\": \"english\"\n}", "english")]
        [TestCase("{\n  \"OldLanguage\": \"es_ES\",\n  \"Languages\": \"es_ES\"\n}", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void SavedLanguageTest(string config, string expected)
        {
            Assert.AreEqual(expected, AtrioInfo.GetSavedLanguage(config));
        }

        [Test]
        public void TagsWithThePhaseAreUnderstoodTest()
        {
            // Since 1.12.2 the tags are x.y.z-beta; 1.12.2 itself is plain (the copies before it only read x.y.z).
            var releases = AtrioUpdates.ParseReleases(@"[
                {""tag_name"":""1.12.3-beta"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio.zip"",""url"":""other"",""size"":1},{""name"":""Atrio-1.12.3-beta.zip"",""url"":""a"",""size"":1}]},
                {""tag_name"":""1.12.2"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-1.12.2.zip"",""url"":""b"",""size"":1}]},
                {""tag_name"":""v1.12.1"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-1.12.1.zip"",""url"":""c"",""size"":1}]},
                {""tag_name"":""1.13.0-alfa"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-1.13.0-alfa.zip"",""url"":""d"",""size"":1}]},
                {""tag_name"":""-beta"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-x.zip"",""url"":""e"",""size"":1}]}
            ]");

            CollectionAssert.AreEqual(new[] { new Version(1, 13, 0), new Version(1, 12, 3), new Version(1, 12, 2), new Version(1, 12, 1) },
                releases.Select(a => a.Version));
            CollectionAssert.AreEqual(new[] { "1.13.0-alfa", "1.12.3-beta", "1.12.2-beta", "1.12.1-beta" }, releases.Select(a => a.Label));
            Assert.AreEqual("a", releases[1].PackageUrl);
            Assert.AreEqual("b", releases[2].PackageUrl);
            Assert.AreEqual("1.12.2-beta", AtrioInfo.Label(new Version(1, 12, 2)));
            Assert.AreEqual("1.12.3-beta", AtrioInfo.Label("1.12.3-beta"));
            Assert.IsNull(AtrioInfo.Label((string)null));
        }

        [Test]
        public void NoReleasesIsNotAnErrorTest()
        {
            Assert.AreEqual(0, AtrioUpdates.ParseReleases("[]").Count);
        }

        [TestCase("1.0", 2)]
        [TestCase("1.1.0", 1)]
        [TestCase("1.2.0", 0)]
        [TestCase("9.0.0", 0)]
        public void OnlyVersionsAboveTheInstalledOneAreOfferedTest(string installed, int offered)
        {
            var releases = AtrioUpdates.ParseReleases(Releases);
            Assert.AreEqual(offered, AtrioUpdates.GetNewerReleases(releases, Version.Parse(installed)).Count);
        }

        private static string NewFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "atrio-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void AddEntry(ZipArchive zip, string name, string text)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
            {
                writer.Write(text);
            }
        }

        [Test]
        public void PackageUnpacksWithEitherKindOfSeparatorTest()
        {
            var root = NewFolder();
            try
            {
                var path = Path.Combine(root, "package.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    AddEntry(zip, @"Themes\Desktop\theme.xaml", "barras invertidas");
                    AddEntry(zip, "Extensions/Steam/extension.yaml", "barras normales");
                    zip.CreateEntry("Emulation/Emulators/");
                }

                var folder = Path.Combine(root, "p");
                AtrioUpdates.Unpack(path, folder, CancellationToken.None);

                Assert.AreEqual("barras invertidas", File.ReadAllText(Path.Combine(folder, @"Themes\Desktop\theme.xaml")));
                Assert.AreEqual("barras normales", File.ReadAllText(Path.Combine(folder, @"Extensions\Steam\extension.yaml")));
                Assert.IsTrue(Directory.Exists(Path.Combine(folder, @"Emulation\Emulators")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestCase("../outside.txt")]
        [TestCase(@"..\outside.txt")]
        [TestCase("Themes/../../outside.txt")]
        public void PackageCantWriteOutOfItsFolderTest(string name)
        {
            var root = NewFolder();
            try
            {
                var path = Path.Combine(root, "package.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    AddEntry(zip, name, "bad");
                }

                Assert.Throws<InvalidDataException>(() => AtrioUpdates.Unpack(path, Path.Combine(root, "p"), CancellationToken.None));
                Assert.IsFalse(File.Exists(Path.Combine(root, "outside.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void PackageForUpdatesGoesBeforeTheOneToDownloadTest()
        {
            const string json = @"[
                {""tag_name"":""v1.4.8"",""draft"":false,""prerelease"":false,""assets"":[
                    {""name"":""Atrio.zip"",""url"":""download"",""size"":2},
                    {""name"":""Atrio-1.4.8.zip"",""url"":""update"",""size"":1}]},
                {""tag_name"":""v1.4.9"",""draft"":false,""prerelease"":false,""assets"":[
                    {""name"":""Atrio.zip"",""url"":""only-download"",""size"":2}]},
                {""tag_name"":""v1.5.0"",""draft"":false,""prerelease"":false,""assets"":[
                    {""name"":""Atrio-other.zip"",""url"":""other"",""size"":2},
                    {""name"":""Atrio-1.5.0.zip"",""url"":""exact"",""size"":1}]}
            ]";

            var releases = AtrioUpdates.ParseReleases(json);
            Assert.AreEqual("update", releases.Single(a => a.Version == new Version(1, 4, 8)).PackageUrl);
            Assert.AreEqual("only-download", releases.Single(a => a.Version == new Version(1, 4, 9)).PackageUrl);
            Assert.AreEqual("exact", releases.Single(a => a.Version == new Version(1, 5, 0)).PackageUrl);
        }

        [Test]
        public void PackageRootTest()
        {
            Assert.AreEqual("Atrio/", AtrioUpdates.GetPackageRoot(new[] { "Atrio/", "Atrio/Atrio.exe", @"Atrio\Themes\theme.xaml" }));
            Assert.IsNull(AtrioUpdates.GetPackageRoot(new[] { "Atrio.exe", "Themes/theme.xaml" }));
            Assert.IsNull(AtrioUpdates.GetPackageRoot(new[] { "Atrio/Atrio.exe", "README.txt" }));
            Assert.IsNull(AtrioUpdates.GetPackageRoot(new[] { "Atrio.exe", "Atrio/other.txt" }));
            Assert.IsNull(AtrioUpdates.GetPackageRoot(new string[0]));
        }

        [Test]
        public void PackageWithItsFolderUnpacksWithoutItTest()
        {
            var root = NewFolder();
            try
            {
                var path = Path.Combine(root, "Atrio.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    zip.CreateEntry("Atrio/");
                    AddEntry(zip, "Atrio/Atrio.exe", "program");
                    AddEntry(zip, "Atrio/Themes/Desktop/theme.xaml", "theme");
                }

                var folder = Path.Combine(root, "p");
                AtrioUpdates.Unpack(path, folder, CancellationToken.None);

                Assert.AreEqual("program", File.ReadAllText(Path.Combine(folder, "Atrio.exe")));
                Assert.AreEqual("theme", File.ReadAllText(Path.Combine(folder, @"Themes\Desktop\theme.xaml")));
                Assert.IsFalse(Directory.Exists(Path.Combine(folder, "Atrio")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
