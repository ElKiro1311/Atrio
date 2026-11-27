using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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
        [TestCase("test-token")]
        [TestCase("a.%%%.c")]
        [TestCase("a.e30.c")]
        public void AnAccessWithoutAccountIsNotAnError(string access)
        {
            Assert.IsNull(SteamRemoteInstall.GetAccount(access));
        }

        [Test]
        public void TheOnlySessionIsThisOneWithTheSameAccountHere()
        {
            var sessions = new List<SteamClientSession> { Session("1", "other name") };
            Assert.AreEqual("1", SteamRemoteInstall.PickSession(sessions, "KIRO-PC", true).client_instanceid);
        }

        [Test]
        public void WithoutSessionsThereIsNoClient()
        {
            Assert.IsNull(SteamRemoteInstall.PickSession(new List<SteamClientSession>(), "KIRO-PC", true));
            Assert.IsNull(SteamRemoteInstall.PickSession(null, "KIRO-PC", true));
        }

        [Test]
        public void TheListOfTheClientSaysHowTheDownloadGoes()
        {
            var list = """{"response":{"apps":[{"appid":"620","bytes_downloaded":"500","bytes_to_download":"1000","bytes_download_rate":"25000000","download_paused":false},{"appid":"570","bytes_downloaded":1}]}}""";
            var progress = SteamClientProgress.Parse(list, "620");
            Assert.AreEqual(500, progress.BytesDownloaded);
            Assert.AreEqual(1000, progress.BytesToDownload);
            Assert.AreEqual(25000000, progress.Rate);
            Assert.IsFalse(progress.Paused);
            Assert.AreEqual(50, progress.Percent);
        }

        [Test]
        public void TheNumbersOfTheListCanComeAsNumbers()
        {
            var list = """{"response":{"apps":[{"appid":620,"bytes_downloaded":300,"bytes_to_download":400,"bytes_download_rate":5000000,"download_paused":true}]}}""";
            var progress = SteamClientProgress.Parse(list, "620");
            Assert.AreEqual(300, progress.BytesDownloaded);
            Assert.AreEqual(75, progress.Percent);
            Assert.AreEqual(5000000, progress.Rate);
            Assert.IsTrue(progress.Paused);
        }

        [Test]
        public void AListWithoutTheGameSaysNothing()
        {
            var list = """{"response":{"apps":[{"appid":"620"}]}}""";
            Assert.IsNull(SteamClientProgress.Parse(list, "349040"));
        }

        [Test]
        public void AnAnswerBrokenOrEmptyIsNotAProgress()
        {
            Assert.IsNull(SteamClientProgress.Parse(null, "620"));
            Assert.IsNull(SteamClientProgress.Parse("", "620"));
            Assert.IsNull(SteamClientProgress.Parse("{response", "620"));
            Assert.IsNull(SteamClientProgress.Parse("[]", "620"));
            Assert.IsNull(SteamClientProgress.Parse("""{"response":{}}""", "620"));
        }

        [Test]
        public void AListThatLeavesThingsOutStillCountsWhatItHas()
        {
            var list = """{"response":{"apps":[{"appid":"620"}]}}""";
            var progress = SteamClientProgress.Parse(list, "620");
            Assert.AreEqual(0, progress.Percent);
            Assert.AreEqual(0, progress.Rate);
            Assert.IsFalse(progress.Paused);
        }

        [Test]
        public void AGameWithNothingToDownloadShowsNoPercent()
        {
            var list = """{"response":{"apps":[{"appid":"620","bytes_to_download":"0","bytes_downloaded":"100"}]}}""";
            Assert.AreEqual(0, SteamClientProgress.Parse(list, "620").Percent);
        }

        private class ClientList : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Task serving;
            private readonly string[] bodies;
            private int asked;

            public string Url { get; }
            public List<string> Calls { get; } = new List<string>();

            public ClientList(params string[] bodies)
            {
                this.bodies = bodies;
                for (var tries = 0; Url == null; tries++)
                {
                    var port = 40000 + new Random().Next(10000);
                    try
                    {
                        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                        listener.Start();
                        Url = $"http://127.0.0.1:{port}";
                    }
                    catch (HttpListenerException)
                    {
                        if (tries > 20)
                        {
                            throw;
                        }
                    }
                }

                serving = Task.Run(() => Serve());
            }

            private void Serve()
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = listener.GetContext();
                        Calls.Add(context.Request.Url.Query);
                        var body = bodies[Math.Min(asked++, bodies.Length - 1)];
                        using (var writer = new StreamWriter(context.Response.OutputStream))
                        {
                            writer.Write(body);
                        }
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }
                }
            }

            public void Dispose()
            {
                listener.Stop();
                listener.Close();
                try
                {
                    serving.Wait(2000);
                }
                catch
                {
                }
            }
        }

        private static SteamClientProgress AskProgress(string appId, string access, SteamClientSession session, string list, string other, out List<string> calls)
        {
            var previous = Environment.GetEnvironmentVariable("ATRIO_STEAM_API");
            SteamClientProgress progress;
            using (var server = new ClientList(list, other))
            {
                Environment.SetEnvironmentVariable("ATRIO_STEAM_API", server.Url);
                try
                {
                    progress = SteamRemoteInstall.GetProgressAsync(appId, System.Threading.CancellationToken.None, access, session).GetAwaiter().GetResult();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ATRIO_STEAM_API", previous);
                }

                calls = server.Calls;
            }

            return progress;
        }

        [Test]
        public void TheProgressOfTheListThatTheClientKeepsArrives()
        {
            var list = """{"response":{"apps":[{"appid":"620","bytes_downloaded":"25000000","bytes_to_download":100000000,"bytes_download_rate":"25000000"}]}}""";
            var progress = AskProgress("620", "token", Session("77", "home"), list, list, out var calls);
            Assert.AreEqual(25000000, progress.BytesDownloaded);
            Assert.AreEqual(25, progress.Percent);
            StringAssert.Contains("access_token=token", calls.Last());
            StringAssert.Contains("filters=changing", calls.Last());
        }

        [Test]
        public void TheListThatHidesTheDownloadIsAskedWithTheOtherFilter()
        {
            var list = """{"response":{"apps":[{"appid":"620","bytes_downloaded":100,"bytes_to_download":1000}]}}""";
            var empty = """{"response":{"apps":[]}}""";
            var progress = AskProgress("620", "token", Session("77", "home"), empty, list, out var calls);
            Assert.AreEqual(10, progress.Percent);
            StringAssert.Contains("filters=changing", calls[0]);
            StringAssert.Contains("filters=downloading", calls[1]);
        }

        [Test]
        public void AProgressWithoutItsOwnSessionIsNotAsked()
        {
            Assert.IsNull(SteamRemoteInstall.GetProgressAsync("620", System.Threading.CancellationToken.None, "token", null).GetAwaiter().GetResult());
        }
    }
}
