using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.Text;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamDirectInstallTests
    {
        private const string Downloading = @"""AppState""
{
	""appid""		""620""
	""name""		""Portal 2""
	""StateFlags""		""1026""
	""installdir""		""Portal 2""
	""BytesToDownload""		""4000""
	""BytesDownloaded""		""1000""
	""InstalledDepots""
	{
		""621""
		{
			""manifest""		""123""
			""size""		""9""
		}
	}
}";

        private static SteamClientSession Session(string id, string machine)
        {
            return new SteamClientSession { client_instanceid = id, machine_name = machine };
        }

        [Test]
        public void AManifestSaysHowTheDownloadGoes()
        {
            var manifest = SteamAppManifest.Parse(Downloading);
            Assert.AreEqual(1026, manifest.StateFlags);
            Assert.AreEqual("Portal 2", manifest.InstallDir);
            Assert.AreEqual(25, manifest.Percent);
            Assert.IsFalse(manifest.IsInstalled);
        }

        [TestCase(4, true)]
        [TestCase(6, false, Description = "update required")]
        [TestCase(1026, false, Description = "downloading")]
        [TestCase(1030, false, Description = "updating an installed game")]
        [TestCase(516, false, Description = "paused")]
        [TestCase(0, false)]
        public void OnlyAFinishedGameCountsAsInstalled(int flags, bool installed)
        {
            Assert.AreEqual(installed, new SteamAppManifest { StateFlags = flags }.IsInstalled);
        }

        [Test]
        public void AManifestBeingWrittenIsNotAnError()
        {
            Assert.IsFalse(SteamAppManifest.Parse(null).IsInstalled);
            Assert.AreEqual(0, SteamAppManifest.Parse(@"""AppState"" { ""StateFl").Percent);
        }

        [Test]
        public void ThePercentNeverGoesPastTheEnd()
        {
            Assert.AreEqual(100, new SteamAppManifest { BytesToDownload = 10, BytesDownloaded = 25 }.Percent);
            Assert.AreEqual(0, new SteamAppManifest().Percent);
        }

        [Test]
        public void TheLibraryOfAGameGivesItsFolder()
        {
            var manifest = new SteamAppManifest { Library = @"D:\Juegos", InstallDir = "Portal 2" };
            Assert.AreEqual(@"D:\Juegos\steamapps\common\Portal 2", manifest.InstallPath);
            Assert.IsNull(new SteamAppManifest { Library = @"D:\Juegos" }.InstallPath);
        }

        [Test]
        public void LibrariesComeWithTheirPathsEscaped()
        {
            var libraries = SteamClientFiles.ParseLibraryFolders(@"""libraryfolders""
{
	""0""
	{
		""path""		""C:\\Program Files (x86)\\Steam""
		""apps"" { ""620"" ""1"" }
	}
	""1""
	{
		""path""		""D:\\SteamLibrary""
	}
}");
            CollectionAssert.AreEqual(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libraries);
        }

        [Test]
        public void TheClientOfThisComputerIsChosenAmongSeveral()
        {
            var sessions = new List<SteamClientSession> { Session("1", "STEAMDECK"), Session("2", "kiro-pc") };
            Assert.AreEqual("2", SteamRemoteInstall.PickSession(sessions, "KIRO-PC", true).client_instanceid);
        }

        [Test]
        public void WindowsCutsLongNamesOfComputers()
        {
            var sessions = new List<SteamClientSession> { Session("1", "SALON"), Session("2", "ordenador-del-salon") };
            Assert.AreEqual("2", SteamRemoteInstall.PickSession(sessions, "ORDENADOR-DEL-S", false).client_instanceid);
        }

        [Test]
        public void AnotherComputerIsNeverChosen()
        {
            var sessions = new List<SteamClientSession> { Session("1", "STEAMDECK"), Session("2", "SALON") };
            Assert.IsNull(SteamRemoteInstall.PickSession(sessions, "KIRO-PC", true), "there is no way to know which one it is");
            Assert.IsNull(SteamRemoteInstall.PickSession(new List<SteamClientSession> { Session("1", "SALON") }, "KIRO-PC", false), "the account isn't signed in here");
        }

        [Test]
        public void TheAccountComesInsideTheAccess()
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(@"{""iss"":""r:1"",""sub"":""76561198000000123"",""aud"":[""web""]}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.AreEqual(39734395u, SteamRemoteInstall.GetAccount($"cabecera.{payload}.firma"));
        }

        [TestCase(null)]
        [TestCase("token-de-prueba")]
        [TestCase("a.%%%.c")]
        [TestCase("a.e30.c")]
        public void AnAccessWithoutAccountIsNotAnError(string access)
        {
            Assert.IsNull(SteamRemoteInstall.GetAccount(access));
        }

        [Test]
        public void TheOnlySessionIsThisOneWithTheSameAccountHere()
        {
            var sessions = new List<SteamClientSession> { Session("1", "otro nombre") };
            Assert.AreEqual("1", SteamRemoteInstall.PickSession(sessions, "KIRO-PC", true).client_instanceid);
        }

        [Test]
        public void WithoutSessionsThereIsNoClient()
        {
            Assert.IsNull(SteamRemoteInstall.PickSession(new List<SteamClientSession>(), "KIRO-PC", true));
            Assert.IsNull(SteamRemoteInstall.PickSession(null, "KIRO-PC", true));
        }
    }
}
