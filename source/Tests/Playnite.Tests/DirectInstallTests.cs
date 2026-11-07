using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace Playnite.Tests
{
    [TestFixture]
    public class DirectInstallTests
    {
        private const int ButtonRole = 43;
        private const int CheckBoxRole = 44;
        private const int LinkRole = 30;
        private const int DialogRole = 18;
        private const int TextRole = 41;
        private const int CheckedState = 0x10;

        private class FakeControl : StoreControl
        {
            public int Presses { get; private set; }

            public FakeControl(string name, int role, int depth, int state = 0)
            {
                Name = name;
                Role = role;
                Depth = depth;
                State = state;
            }

            public override void Press()
            {
                Presses++;
            }
        }

        private class BrokenStore : SDK.Plugins.InstallController
        {
            public BrokenStore(SDK.Models.Game game) : base(game)
            {
            }

            public override void Install(SDK.Plugins.InstallActionArgs args)
            {
                throw new Exception("Epic Launcher is not installed.");
            }
        }

        private class FallingBackController : DirectInstallController
        {
            public bool Failed => StoreFailed;

            public FallingBackController(SDK.Models.Game game, SDK.Plugins.InstallController store) : base(game, store)
            {
            }

            protected override System.Threading.Tasks.Task<bool> InstallDirectlyAsync(SDK.Plugins.InstallActionArgs args)
            {
                return System.Threading.Tasks.Task.FromResult(false);
            }
        }

        private class AssistedController : AssistedInstallController
        {
            public bool Assisted { get; private set; }

            public bool Failed => StoreFailed;

            public AssistedController(SDK.Models.Game game, SDK.Plugins.InstallController store) : base(game, store)
            {
            }

            protected override System.Threading.Tasks.Task AssistAsync()
            {
                Assisted = true;
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private static readonly Color buttonBlue = Color.FromArgb(0, 110, 245);
        private static readonly Color pressedBlue = Color.FromArgb(0, 88, 196);
        private static readonly Color background = Color.FromArgb(27, 32, 38);

        private static Bitmap UbisoftPage(int width = 714, Color? continueColor = null, Color? licenseColor = null, bool shortcuts = false)
        {
            var scale = width / 714.0;
            var page = new Bitmap(width, (int)(454 * scale));
            using (var graphics = Graphics.FromImage(page))
            {
                graphics.Clear(background);
            }

            void Paint(int x, int y, Color color)
            {
                page.SetPixel((int)(x * scale), (int)(y * scale), color);
            }

            Paint(615, 403, continueColor ?? buttonBlue);
            Paint(480, 403, licenseColor ?? background);
            if (shortcuts)
            {
                Paint(152, 266, Color.White);
                Paint(152, 300, Color.White);
            }

            return page;
        }

        [Test]
        public void EpicUriNeedsTheThreeNamesTest()
        {
            var id = new EpicGameId { Namespace = "basil", CatalogItem = "2673", AppName = "Basil" };
            Assert.AreEqual("basil:2673:Basil", id.FullId);
            Assert.AreEqual("com.epicgames.launcher://apps/basil%3A2673%3ABasil?action=install&silent=true", id.InstallUri);
        }

        [Test]
        public void EpicCatalogFindsTheGameByItsReleaseTest()
        {
            var json = @"[{""id"":""aaa"",""namespace"":""one"",""releaseInfo"":[{""appId"":""Other""}]},
                          {""id"":""bbb"",""namespace"":""two"",""releaseInfo"":[{""appId"":""Basil""}]},
                          {""id"":""ccc"",""namespace"":""three""}]";
            var catalog = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var found = EpicClientFiles.ParseCatalog(catalog, "Basil");
            Assert.AreEqual("two", found.Namespace);
            Assert.AreEqual("bbb", found.CatalogItem);
            Assert.IsNull(EpicClientFiles.ParseCatalog(catalog, "Missing"));
        }

        [Test]
        public void EpicDialogEndsOnTheInstallButtonTest()
        {
            CollectionAssert.AreEqual(
                new[] { StoreWindows.TabKey, StoreWindows.TabKey, StoreWindows.TabKey, StoreWindows.TabKey, StoreWindows.SpaceKey },
                EpicDirectInstallController.GetShortcutKeys().ToList());
            CollectionAssert.AreEqual(
                new[] { StoreWindows.TabKey, StoreWindows.EnterKey },
                EpicDirectInstallController.GetInstallKeys().ToList());
        }

        [TestCase("AppInstallUriHandler: Install-location selector concluded for AppId x (accepted=true); navigating to Library", true)]
        [TestCase("AppInstallUriHandler: Install-location selector concluded for AppId x (accepted=false); navigating to Library", false)]
        [TestCase("LogHttp: nothing to do with it", null)]
        public void EpicDialogAnswerComesFromItsLogTest(string line, bool? answer)
        {
            Assert.AreEqual(answer, EpicDirectInstallController.ReadDialogAnswer(line));
        }

        [Test]
        public void LogFollowerOnlySeesWhatComesAfterTest()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "old line\n");
                var log = new LogFollower(file);
                Assert.IsFalse(log.ReadNew().Any(a => a.Contains("old")));
                File.AppendAllText(file, "new line\n");
                Assert.IsTrue(log.ReadNew().Any(a => a.Contains("new line")));
                Assert.IsFalse(log.ReadNew().Any(a => a.Contains("new line")));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void LogFollowerSurvivesARestartOfTheProgramTest()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "a long line from the previous run of the program\n");
                var log = new LogFollower(file);
                File.WriteAllText(file, "short\n");
                Assert.IsTrue(log.ReadNew().Any(a => a.Contains("short")));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void LogFollowerNoticesANewLogEvenIfItIsBiggerTest()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "Log file open, 11/07/26 01:00:00\nold\n");
                var log = new LogFollower(file);
                File.WriteAllText(file, "Log file open, 11/07/26 02:00:00\n" + new string('x', 500) + "\nwanted line\n");
                Assert.IsTrue(log.ReadNew().Any(a => a.Contains("wanted line")));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void LogFollowerWaitsForALogThatDoesNotExistYetTest()
        {
            var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            try
            {
                var log = new LogFollower(file);
                Assert.IsEmpty(log.ReadNew());
                File.WriteAllText(file, "first line\n");
                Assert.IsTrue(log.ReadNew().Any(a => a.Contains("first line")));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void XboxReadsTheProductOfTheCatalogTest()
        {
            Assert.AreEqual("9NKVX66J0ZSK", XboxDirectInstallController.ParseProductId(@"{""Products"":[{""ProductId"":""9NKVX66J0ZSK""}]}"));
            Assert.IsNull(XboxDirectInstallController.ParseProductId(@"{""Products"":[]}"));
        }

        [Test]
        public void XboxConsoleGamesHaveNothingToInstallHereTest()
        {
            Assert.IsTrue(XboxDirectInstallController.CanInstall(new SDK.Models.Game { GameId = "MotionTwin.DeadCellsWin10_rtjy889c6zgtg" }));
            Assert.IsFalse(XboxDirectInstallController.CanInstall(new SDK.Models.Game { GameId = "CONSOLE_1161890070_Xbox360Game" }));
            Assert.IsFalse(XboxDirectInstallController.CanInstall(new SDK.Models.Game { GameId = string.Empty }));
        }

        [TestCase("Completed", true, false)]
        [TestCase("Error", false, true)]
        [TestCase("Canceled", false, true)]
        [TestCase("Downloading", false, false)]
        [TestCase("Paused", false, false)]
        public void XboxStatesTest(string state, bool finished, bool failed)
        {
            var status = new StoreInstallStatus { State = state };
            Assert.AreEqual(finished, status.IsFinished);
            Assert.AreEqual(failed, status.HasFailed);
        }

        [Test]
        public void DialogIsOnlyWhatHangsFromItTest()
        {
            var controls = new List<StoreControl>
            {
                new FakeControl("Page", ButtonRole, 1),
                new FakeControl("Modal", DialogRole, 2),
                new FakeControl("Inside", ButtonRole, 3),
                new FakeControl("Deep", ButtonRole, 5),
                new FakeControl("After", ButtonRole, 2)
            };

            CollectionAssert.AreEqual(new[] { "Inside", "Deep" }, StoreDialogs.GetDialog(controls).Select(a => a.Name));
        }

        [Test]
        public void NoDialogIsNotAnErrorTest()
        {
            Assert.IsEmpty(StoreDialogs.GetDialog(new List<StoreControl> { new FakeControl("Page", ButtonRole, 1) }));
        }

        private static List<StoreControl> AmazonDialog(FakeControl shortcut, FakeControl cancel, FakeControl proceed)
        {
            return new List<StoreControl>
            {
                new FakeControl("Install", ButtonRole, 1),
                new FakeControl("Modal", DialogRole, 1),
                new FakeControl(@"C:\Amazon Games", ButtonRole, 2),
                shortcut,
                cancel,
                proceed
            };
        }

        [Test]
        public void AmazonGoesAheadWithoutAShortcutTest()
        {
            var shortcut = new FakeControl("Create a desktop shortcut", CheckBoxRole, 2, CheckedState);
            var cancel = new FakeControl("Cancel", ButtonRole, 2);
            var proceed = new FakeControl("User Account Control Shield Icon Proceed", ButtonRole, 2);
            Assert.IsTrue(AmazonDirectInstallController.AcceptDialog(AmazonDialog(shortcut, cancel, proceed)));
            Assert.AreEqual(1, shortcut.Presses);
            Assert.AreEqual(1, proceed.Presses);
            Assert.AreEqual(0, cancel.Presses);
        }

        [Test]
        public void AmazonLeavesAnUncheckedShortcutAloneTest()
        {
            var shortcut = new FakeControl("Create a desktop shortcut", CheckBoxRole, 2);
            var proceed = new FakeControl("Proceed", ButtonRole, 2);
            Assert.IsTrue(AmazonDirectInstallController.AcceptDialog(AmazonDialog(shortcut, new FakeControl("Cancel", ButtonRole, 2), proceed)));
            Assert.AreEqual(0, shortcut.Presses);
            Assert.AreEqual(1, proceed.Presses);
        }

        [Test]
        public void AmazonWaitsForTheDialogToFinishLoadingTest()
        {
            var cancel = new FakeControl("Cancel", ButtonRole, 2);
            var loading = new List<StoreControl>
            {
                new FakeControl("Modal", DialogRole, 1),
                new FakeControl(@"C:\Amazon Games", ButtonRole, 2),
                new FakeControl("Create a desktop shortcut", CheckBoxRole, 2, CheckedState),
                cancel
            };

            Assert.IsFalse(AmazonDirectInstallController.AcceptDialog(loading));
            Assert.AreEqual(0, cancel.Presses);
        }

        [Test]
        public void AmazonWaitsForTheButtonToBeEnabledTest()
        {
            var proceed = new FakeControl("Proceed", ButtonRole, 2, 0x1);
            var shortcut = new FakeControl("Create a desktop shortcut", CheckBoxRole, 2, CheckedState);
            Assert.IsFalse(AmazonDirectInstallController.AcceptDialog(AmazonDialog(shortcut, new FakeControl("Cancel", ButtonRole, 2), proceed)));
            Assert.AreEqual(0, proceed.Presses);
            Assert.AreEqual(0, shortcut.Presses);
        }

        [Test]
        public void AmazonDoesNothingWithOtherDialogsTest()
        {
            var ok = new FakeControl("OK", ButtonRole, 2);
            var controls = new List<StoreControl> { new FakeControl("Modal", DialogRole, 1), ok };
            Assert.IsFalse(AmazonDirectInstallController.AcceptDialog(controls));
            Assert.AreEqual(0, ok.Presses);
            Assert.IsFalse(AmazonDirectInstallController.AcceptDialog(new List<StoreControl> { new FakeControl("Page", ButtonRole, 1) }));
        }

        [Test]
        public void UbisoftTellsTheLicenseFromTheOtherPagesTest()
        {
            using (var page = UbisoftPage(continueColor: pressedBlue, licenseColor: pressedBlue))
            {
                Assert.AreEqual(UbisoftStep.License, UbisoftDirectInstallController.Classify(page));
            }

            using (var page = UbisoftPage())
            {
                Assert.AreEqual(UbisoftStep.Continue, UbisoftDirectInstallController.Classify(page));
            }

            using (var page = UbisoftPage(continueColor: pressedBlue, shortcuts: true))
            {
                Assert.AreEqual(UbisoftStep.Shortcuts, UbisoftDirectInstallController.Classify(page));
            }
        }

        [Test]
        public void UbisoftKnowsTheLicenseInOtherScalesTest()
        {
            using (var page = UbisoftPage(1071, licenseColor: buttonBlue))
            {
                Assert.AreEqual(UbisoftStep.License, UbisoftDirectInstallController.Classify(page));
            }
        }

        [Test]
        public void UbisoftWaitsWhenItDoesNotKnowThePageTest()
        {
            using (var page = UbisoftPage(continueColor: background))
            {
                Assert.AreEqual(UbisoftStep.Unknown, UbisoftDirectInstallController.Classify(page));
            }
        }

        [Test]
        public void EaTellsItsDialogsApartWithoutReadingThemTest()
        {
            Assert.AreEqual(EaStep.Nothing, EaDirectInstallController.Classify(new List<StoreControl>()));

            Assert.AreEqual(EaStep.GameNotInstalled, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Juego no instalado", TextRole, 2),
                new FakeControl("Obtener el juego", ButtonRole, 2),
                new FakeControl("Cerrar", ButtonRole, 2)
            }));

            Assert.AreEqual(EaStep.DownloadOptions, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Cerrar", ButtonRole, 2),
                new FakeControl("Crear acceso directo", CheckBoxRole, 3, CheckedState),
                new FakeControl("Siguiente", ButtonRole, 2),
                new FakeControl("Cancelar", ButtonRole, 2)
            }));

            Assert.AreEqual(EaStep.Terms, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Acuerdo de usuario", LinkRole, 3),
                new FakeControl("Descargar", ButtonRole, 2),
                new FakeControl("Cancelar", ButtonRole, 2)
            }));
        }

        [Test]
        public void EaDialogWithASingleButtonStopsEverythingTest()
        {
            Assert.AreEqual(EaStep.Other, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("You don't have access", TextRole, 2),
                new FakeControl("OK", ButtonRole, 2)
            }));
        }

        [Test]
        public void EaReadsTheContentOfTheOfferTest()
        {
            var answer = @"{""data"":{""legacyOffers"":[{""offerId"":""Origin.OFR.50.0005198"",""contentId"":""198422""}]}}";
            Assert.AreEqual("198422", EaDirectInstallController.ParseContentId(answer, "Origin.OFR.50.0005198"));
            Assert.IsNull(EaDirectInstallController.ParseContentId(answer, "Other"));
            Assert.IsNull(EaDirectInstallController.ParseContentId(@"{""errors"":[{}]}", "Other"));
        }

        [Test]
        public void ItchPrefersTheGameOverItsSoundtrackTest()
        {
            var uploads = JArray.Parse(@"[{""id"":1,""type"":""soundtrack"",""platforms"":{}},{""id"":2,""type"":""default"",""platforms"":{""windows"":""all""}}]");
            Assert.AreEqual(2, ItchDirectInstallController.PickUpload(uploads)["id"].Value<int>());
        }

        [Test]
        public void ItchTakesWhatThereIsTest()
        {
            var uploads = JArray.Parse(@"[{""id"":7,""type"":""html"",""platforms"":{}}]");
            Assert.AreEqual(7, ItchDirectInstallController.PickUpload(uploads)["id"].Value<int>());
            Assert.IsNull(ItchDirectInstallController.PickUpload(new JArray()));
        }

        [Test]
        public void GogPrefersTheLanguageOfThePersonTest()
        {
            var downloads = JToken.Parse(@"{""installers"":[
                {""id"":""a"",""os"":""mac"",""language"":""es""},
                {""id"":""b"",""os"":""windows"",""language"":""en""},
                {""id"":""c"",""os"":""windows"",""language"":""es""}]}");
            Assert.AreEqual("c", GogDownloads.PickInstaller(downloads, "es")["id"].ToString());
            Assert.AreEqual("b", GogDownloads.PickInstaller(downloads, "ja")["id"].ToString());
        }

        [Test]
        public void GogTakesWhatThereIsForWindowsTest()
        {
            var downloads = JToken.Parse(@"{""installers"":[{""id"":""a"",""os"":""linux"",""language"":""en""},{""id"":""b"",""os"":""windows"",""language"":""pl""}]}");
            Assert.AreEqual("b", GogDownloads.PickInstaller(downloads, "es")["id"].ToString());
            Assert.IsNull(GogDownloads.PickInstaller(JToken.Parse(@"{""installers"":[{""os"":""linux""}]}"), "es"));
            Assert.IsNull(GogDownloads.PickInstaller(null, "es"));
        }

        [Test]
        public void GogChecksumComesFromItsListTest()
        {
            var text = @"<file name=""setup_game.exe"" available=""1"" notavailablemsg="""" md5=""0123456789ABCDEF0123456789abcdef"" chunks=""2"" timestamp=""2026"" total_size=""1"">";
            Assert.AreEqual("0123456789abcdef0123456789abcdef", GogDownloads.ParseChecksum(text));
            Assert.IsNull(GogDownloads.ParseChecksum("<file />"));
            Assert.IsNull(GogDownloads.ParseChecksum(null));
        }

        [Test]
        public void AStoreThatCannotStartEndsTheInstallTest()
        {
            var game = new SDK.Models.Game { GameId = "Basil" };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());
            var controller = new FallingBackController(game, new BrokenStore(game));
            Assert.DoesNotThrow(() => controller.Install(new SDK.Plugins.InstallActionArgs()));
            Assert.IsTrue(controller.Failed);
            Assert.IsNull(DirectInstallProgress.Get(game.Id));
        }

        [Test]
        public void NothingToAssistWhenTheStoreCannotStartTest()
        {
            var game = new SDK.Models.Game { GameId = "109" };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());
            var controller = new AssistedController(game, new BrokenStore(game));
            Assert.DoesNotThrow(() => controller.Install(new SDK.Plugins.InstallActionArgs()));
            Assert.IsTrue(controller.Failed);
            Assert.IsFalse(controller.Assisted);
        }

        [Test]
        public void TermsStayInThePersonsHandsByDefaultTest()
        {
            var settings = new AtrioSettings();
            Assert.IsTrue(settings.DirectInstall);
            Assert.IsFalse(settings.AcceptStoreTerms);
        }

        [Test]
        public void ProgressKeepsThePercentOrTheNoticeTest()
        {
            var game = Guid.NewGuid();
            var changes = new List<Guid>();
            EventHandler<Guid> listener = (_, id) => changes.Add(id);
            DirectInstallProgress.Changed += listener;
            try
            {
                DirectInstallProgress.Set(game, 40);
                Assert.AreEqual(40, DirectInstallProgress.Get(game));
                Assert.IsNull(DirectInstallProgress.GetNotice(game));

                DirectInstallProgress.SetNotice(game, "Accept");
                Assert.IsNull(DirectInstallProgress.Get(game));
                Assert.AreEqual("Accept", DirectInstallProgress.GetNotice(game));

                DirectInstallProgress.Clear(game);
                Assert.IsNull(DirectInstallProgress.Get(game));
                Assert.IsNull(DirectInstallProgress.GetNotice(game));
                Assert.AreEqual(3, changes.Count(a => a == game));
            }
            finally
            {
                DirectInstallProgress.Changed -= listener;
            }
        }
    }
}
