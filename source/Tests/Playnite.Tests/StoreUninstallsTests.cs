using NUnit.Framework;
using Playnite.Atrio;
using System.Collections.Generic;

namespace Playnite.Tests
{
    [TestFixture]
    public class StoreUninstallsTests
    {
        [Test]
        public void SplitCommandTest()
        {
            Assert.IsTrue(WindowsUninstallEntries.TrySplitCommand("\"C:\\Program Files\\Common Files\\EAInstaller\\Game\\Cleanup.exe\" uninstall_game -autologging", out var file, out var arguments));
            Assert.AreEqual(@"C:\Program Files\Common Files\EAInstaller\Game\Cleanup.exe", file);
            Assert.AreEqual("uninstall_game -autologging", arguments);

            Assert.IsTrue(WindowsUninstallEntries.TrySplitCommand(@"C:\Games\unins000.exe /SILENT", out file, out arguments));
            Assert.AreEqual(@"C:\Games\unins000.exe", file);
            Assert.AreEqual("/SILENT", arguments);

            Assert.IsTrue(WindowsUninstallEntries.TrySplitCommand("\"C:\\A\\Remover.exe\"", out file, out arguments));
            Assert.AreEqual(string.Empty, arguments);

            Assert.IsFalse(WindowsUninstallEntries.TrySplitCommand(null, out _, out _));
            Assert.IsFalse(WindowsUninstallEntries.TrySplitCommand("msiexec /x {GUID}", out _, out _), "only programs, not installer packages");
            Assert.IsFalse(WindowsUninstallEntries.TrySplitCommand("\"C:\\A\\script.cmd\" x", out _, out _));
        }

        [Test]
        public void SameFolderTest()
        {
            Assert.IsTrue(WindowsUninstallEntries.SameFolder(@"C:\Program Files\EA Games\Game\", @"c:\program files\ea games\game"));
            Assert.IsTrue(WindowsUninstallEntries.SameFolder("\"D:\\Game\"", @"D:\Game\"));
            Assert.IsFalse(WindowsUninstallEntries.SameFolder(@"D:\Game", @"D:\Game2"));
            Assert.IsFalse(WindowsUninstallEntries.SameFolder(null, @"D:\Game"));
        }

        private static readonly List<WindowsUninstallEntry> entries = new List<WindowsUninstallEntry>
        {
            new WindowsUninstallEntry { Key = "a", Name = "Other", InstallLocation = @"C:\Program Files\EA Games\Game", UninstallString = "\"C:\\Other\\unins000.exe\"" },
            new WindowsUninstallEntry { Key = "b", Name = "Game", InstallLocation = @"C:\Program Files\EA Games\Game", UninstallString = "\"C:\\Program Files\\Common Files\\EAInstaller\\Game\\Cleanup.exe\" uninstall_game -autologging" },
            new WindowsUninstallEntry { Key = "c", Name = "Adventure", InstallLocation = @"C:\Amazon Games\Library\Adventure", UninstallString = "\"C:\\Users\\U\\AppData\\Local\\Amazon Games\\App\\Amazon Game Remover.exe\" -m Game -p amzn1.adg.product.1234" },
            new WindowsUninstallEntry { Key = "d", Name = "Classic", InstallLocation = @"D:\Amazon\Classic", UninstallString = "\"C:\\Users\\U\\AppData\\Local\\Amazon Games\\App\\Amazon Game Remover.exe\" -m Game -p amzn1.adg.product.9999" }
        };

        [Test]
        public void FindEaTest()
        {
            Assert.AreEqual("b", WindowsUninstallEntries.FindEa(entries, @"C:\Program Files\EA Games\Game\")?.Key);
            Assert.IsNull(WindowsUninstallEntries.FindEa(entries, @"C:\Program Files\EA Games\Other"));
            Assert.IsNull(WindowsUninstallEntries.FindEa(entries, null));
        }

        [Test]
        public void FindAmazonTest()
        {
            Assert.AreEqual("c", WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.1234", null)?.Key, "by product");
            Assert.AreEqual("d", WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.0000", @"D:\Amazon\Classic\")?.Key, "by folder");
            Assert.IsNull(WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.0000", @"D:\Other"));
            Assert.IsNull(WindowsUninstallEntries.FindAmazon(entries, null, @"C:\Program Files\EA Games\Game"), "an EA game isn't Amazon's");
        }
    }
}
