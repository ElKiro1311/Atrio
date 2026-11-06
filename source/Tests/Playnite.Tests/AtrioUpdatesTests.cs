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
            {""tag_name"":""v1.1.0"",""html_url"":""pagina"",""body"":""Novedades"",""draft"":false,""prerelease"":false,""assets"":[
                {""name"":""otra-cosa.txt"",""url"":""mal""},
                {""name"":""Atrio-1.1.0.zip"",""url"":""https://api.github.com/repos/a/b/releases/assets/5"",""size"":200,""digest"":""sha256:abc123""}]},
            {""tag_name"":""v1.2.0"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-1.2.0.zip"",""url"":""u"",""size"":1,""digest"":null}]},
            {""tag_name"":""v1.4.0"",""draft"":false,""prerelease"":false,""assets"":[]},
            {""tag_name"":""nocturna"",""draft"":false,""prerelease"":false,""assets"":[{""name"":""Atrio-x.zip"",""url"":""u""}]}
        ]";

        [Test]
        public void OnlyPublishedVersionsWithTheirPackageCountTest()
        {
            var releases = AtrioUpdates.ParseReleases(Releases);

            CollectionAssert.AreEqual(new[] { new Version(1, 2, 0), new Version(1, 1, 0) }, releases.Select(a => a.Version));
            Assert.AreEqual("https://api.github.com/repos/a/b/releases/assets/5", releases[1].PackageUrl);
            Assert.AreEqual(200, releases[1].PackageSize);
            Assert.AreEqual("abc123", releases[1].PackageHash);
            Assert.AreEqual("Novedades", releases[1].Notes);
            Assert.AreEqual("pagina", releases[1].PageUrl);
            Assert.IsNull(releases[0].PackageHash);
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

        [Test]
        public void AccessComesFromWhatGitAnswersTest()
        {
            Assert.AreEqual("secreto", AtrioUpdates.ParseCredential("protocol=https\nhost=github.com\nusername=alguien\npassword=secreto\n"));
            Assert.AreEqual("secreto", AtrioUpdates.ParseCredential("protocol=https\r\npassword=secreto\r\n"));
            Assert.IsNull(AtrioUpdates.ParseCredential("protocol=https\nhost=github.com\n"));
            Assert.IsNull(AtrioUpdates.ParseCredential(string.Empty));
            Assert.IsNull(AtrioUpdates.ParseCredential(null));
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
                var path = Path.Combine(root, "paquete.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    AddEntry(zip, @"Themes\Desktop\tema.xaml", "barras invertidas");
                    AddEntry(zip, "Extensions/Steam/extension.yaml", "barras normales");
                    zip.CreateEntry("Emulation/Emulators/");
                }

                var folder = Path.Combine(root, "p");
                AtrioUpdates.Unpack(path, folder, CancellationToken.None);

                Assert.AreEqual("barras invertidas", File.ReadAllText(Path.Combine(folder, @"Themes\Desktop\tema.xaml")));
                Assert.AreEqual("barras normales", File.ReadAllText(Path.Combine(folder, @"Extensions\Steam\extension.yaml")));
                Assert.IsTrue(Directory.Exists(Path.Combine(folder, @"Emulation\Emulators")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestCase("../fuera.txt")]
        [TestCase(@"..\fuera.txt")]
        [TestCase("Themes/../../fuera.txt")]
        public void PackageCantWriteOutOfItsFolderTest(string name)
        {
            var root = NewFolder();
            try
            {
                var path = Path.Combine(root, "paquete.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    AddEntry(zip, name, "malo");
                }

                Assert.Throws<InvalidDataException>(() => AtrioUpdates.Unpack(path, Path.Combine(root, "p"), CancellationToken.None));
                Assert.IsFalse(File.Exists(Path.Combine(root, "fuera.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
