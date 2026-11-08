using NUnit.Framework;
using Playnite.Atrio;

namespace Playnite.Tests
{
    [TestFixture]
    public class DirectUninstallTests
    {
        [Test]
        public void ManifestSaysSizeAndWhetherItIsBeingDeletedTest()
        {
            var manifest = SteamAppManifest.Parse("\"AppState\"\n{\n\t\"appid\"\t\t\"10\"\n\t\"StateFlags\"\t\t\"2052\"\n\t\"installdir\"\t\t\"Juego\"\n\t\"SizeOnDisk\"\t\t\"1610612736\"\n}\n");
            Assert.AreEqual(1610612736, manifest.SizeOnDisk);
            Assert.IsTrue(manifest.IsUninstalling);

            var installed = SteamAppManifest.Parse("\"AppState\"\n{\n\t\"StateFlags\"\t\t\"4\"\n}\n");
            Assert.IsFalse(installed.IsUninstalling);
            Assert.AreEqual(0, installed.SizeOnDisk);
        }

        [Test]
        public void QuestionTest()
        {
            Assert.AreEqual("<!LOCAtrioUninstallAsk!>", DirectUninstall.GetQuestion("Juego", 1L << 30, @"D:\Steam\steamapps\common\Juego"));
            Assert.AreEqual("<!LOCAtrioUninstallAskShort!>", DirectUninstall.GetQuestion("Juego", 0, @"D:\Steam\steamapps\common\Juego"));
            Assert.AreEqual("<!LOCAtrioUninstallAskShort!>", DirectUninstall.GetQuestion("Juego", 100, null));
        }

        [Test]
        public void SizeTest()
        {
            StringAssert.EndsWith(" GB", DirectUninstall.FormatSize(3L << 30));
            StringAssert.StartsWith("3", DirectUninstall.FormatSize(3L << 30));
            Assert.AreEqual("512 MB", DirectUninstall.FormatSize(512L << 20));
            Assert.AreEqual("1 MB", DirectUninstall.FormatSize(10));
        }
    }
}
