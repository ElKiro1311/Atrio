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
            Assert.IsTrue(WindowsUninstallEntries.TrySplitCommand("\"C:\\Program Files\\Common Files\\EAInstaller\\Juego\\Cleanup.exe\" uninstall_game -autologging", out var file, out var arguments));
            Assert.AreEqual(@"C:\Program Files\Common Files\EAInstaller\Juego\Cleanup.exe", file);
            Assert.AreEqual("uninstall_game -autologging", arguments);

            Assert.IsTrue(WindowsUninstallEntries.TrySplitCommand(@"C:\Juegos\unins000.exe /SILENT", out file, out arguments));
            Assert.AreEqual(@"C:\Juegos\unins000.exe", file);
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
            Assert.IsTrue(WindowsUninstallEntries.SameFolder(@"C:\Program Files\EA Games\Juego\", @"c:\program files\ea games\juego"));
            Assert.IsTrue(WindowsUninstallEntries.SameFolder("\"D:\\Juego\"", @"D:\Juego\"));
            Assert.IsFalse(WindowsUninstallEntries.SameFolder(@"D:\Juego", @"D:\Juego2"));
            Assert.IsFalse(WindowsUninstallEntries.SameFolder(null, @"D:\Juego"));
        }

        private static readonly List<WindowsUninstallEntry> entries = new List<WindowsUninstallEntry>
        {
            new WindowsUninstallEntry { Key = "a", Name = "Otro", InstallLocation = @"C:\Program Files\EA Games\Juego", UninstallString = "\"C:\\Otro\\unins000.exe\"" },
            new WindowsUninstallEntry { Key = "b", Name = "Juego", InstallLocation = @"C:\Program Files\EA Games\Juego", UninstallString = "\"C:\\Program Files\\Common Files\\EAInstaller\\Juego\\Cleanup.exe\" uninstall_game -autologging" },
            new WindowsUninstallEntry { Key = "c", Name = "Aventura", InstallLocation = @"C:\Amazon Games\Library\Aventura", UninstallString = "\"C:\\Users\\U\\AppData\\Local\\Amazon Games\\App\\Amazon Game Remover.exe\" -m Game -p amzn1.adg.product.1234" },
            new WindowsUninstallEntry { Key = "d", Name = "Clásico", InstallLocation = @"D:\Amazon\Clasico", UninstallString = "\"C:\\Users\\U\\AppData\\Local\\Amazon Games\\App\\Amazon Game Remover.exe\" -m Game -p amzn1.adg.product.9999" }
        };

        [Test]
        public void FindEaTest()
        {
            Assert.AreEqual("b", WindowsUninstallEntries.FindEa(entries, @"C:\Program Files\EA Games\Juego\")?.Key);
            Assert.IsNull(WindowsUninstallEntries.FindEa(entries, @"C:\Program Files\EA Games\Otro"));
            Assert.IsNull(WindowsUninstallEntries.FindEa(entries, null));
        }

        [Test]
        public void FindAmazonTest()
        {
            Assert.AreEqual("c", WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.1234", null)?.Key, "by product");
            Assert.AreEqual("d", WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.0000", @"D:\Amazon\Clasico\")?.Key, "by folder");
            Assert.IsNull(WindowsUninstallEntries.FindAmazon(entries, "amzn1.adg.product.0000", @"D:\Otro"));
            Assert.IsNull(WindowsUninstallEntries.FindAmazon(entries, null, @"C:\Program Files\EA Games\Juego"), "an EA game isn't Amazon's");
        }
    }
}
