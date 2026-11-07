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

            Paint(615, 392, continueColor ?? buttonBlue);
            Paint(480, 392, licenseColor ?? background);
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

        [TestCase("[Portal.Shortcut]\r\nAutoCreate=False\r\nbasil:2673:Basil=True\r\n", false)]
        [TestCase("[Portal.Shortcut]\r\nbasil:2673:Basil=False\r\nAutoCreate=True\r\n", true)]
        [TestCase("[Portal.Shortcut]\r\nbasil:2673:Basil=False\r\n", true)]
        [TestCase("[Other]\r\nAutoCreate=False\r\n[Portal.Shortcut]\r\nAutoCreate=True\r\n", true)]
        [TestCase("[Portal.Shortcut]\r\nAutoCreate=True\r\n[Other]\r\nAutoCreate=False\r\n", true)]
        [TestCase("", true)]
        public void EpicShortcutBoxStartsWithTheLastAnswerTest(string settings, bool ticked)
        {
            Assert.AreEqual(ticked, EpicClientFiles.ParseShortcutBox(settings));
        }

        [TestCase("AppInstallUriHandler: Install-location selector concluded for AppId ns:item:app (accepted=true); navigating to Library", true)]
        [TestCase("AppInstallUriHandler: Install-location selector concluded for AppId ns:item:app (accepted=false); navigating to Library", false)]
        [TestCase("AppInstallUriHandler: Install-location selector concluded for AppId other:game:here (accepted=false); navigating to Library", null)]
        [TestCase("LogHttp: nothing to do with it", null)]
        public void EpicDialogAnswerComesFromItsLogTest(string line, bool? answer)
        {
            Assert.AreEqual(answer, EpicDirectInstallController.ReadDialogAnswer(line, "ns:item:app"));
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
        public void LogFollowerWaitsForTheEndOfALineTest()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "old\n");
                var log = new LogFollower(file);
                File.AppendAllText(file, "half of a li");
                Assert.IsEmpty(log.ReadNew());
                File.AppendAllText(file, "ne that ended\n");
                CollectionAssert.AreEqual(new[] { "half of a line that ended" }, log.ReadNew());
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

        [TestCase("Command line: uplay://install/109", "109", true)]
        [TestCase("Command line: uplay://install/1090", "109", false)]
        [TestCase("Command line: uplay://install/109 -lang", "109", true)]
        [TestCase("Command line: uplay://launch/109/0", "109", false)]
        public void UbisoftKnowsItsOrderInTheLogTest(string line, string game, bool order)
        {
            Assert.AreEqual(order, UbisoftClientFiles.IsInstallOrder(line, game));
        }

        [TestCase("INFO DownloadEdisonSetupNew.cpp (57) Download initiated for product: 109, build: D93F248F70E2.", "109", true)]
        [TestCase("INFO DownloadEdisonSetupNew.cpp (57) Download initiated for product: 1090, build: D93F248F70E2.", "109", false)]
        [TestCase("INFO Startup.cpp (185) Client launched with argOffline: false", "109", false)]
        public void UbisoftDownloadStartComesFromItsLogTest(string line, string game, bool start)
        {
            Assert.AreEqual(start, UbisoftClientFiles.IsDownloadStart(line, game));
        }

        [Test]
        public void UbisoftGameIsThereWhenItsDownloadFolderEmptiesTest()
        {
            var folder = Path.Combine(Path.GetTempPath(), "atrio-ubisoft-" + Guid.NewGuid().ToString("N"));
            var download = Path.Combine(folder, "uplay_download");
            try
            {
                Directory.CreateDirectory(download);
                File.WriteAllText(Path.Combine(folder, "uplay_install.state"), "state");
                Assert.IsFalse(UbisoftClientFiles.IsDownloadComplete(folder), "only the state and an empty download");

                File.WriteAllText(Path.Combine(download, "chunk"), "data");
                Assert.IsFalse(UbisoftClientFiles.IsDownloadComplete(folder), "downloading");

                Directory.CreateDirectory(Path.Combine(folder, "system"));
                Assert.IsFalse(UbisoftClientFiles.IsDownloadComplete(folder), "moving the files out");

                File.Delete(Path.Combine(download, "chunk"));
                Assert.IsTrue(UbisoftClientFiles.IsDownloadComplete(folder), "all there, the download folder stays empty");

                Directory.Delete(download);
                Assert.IsTrue(UbisoftClientFiles.IsDownloadComplete(folder), "without the download folder");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void UbisoftOnlyClicksTheTickedShortcutsTest()
        {
            using (var page = UbisoftPage(shortcuts: true))
            {
                Assert.AreEqual(2, UbisoftDirectInstallController.GetTickedShortcuts(page).Count);
                page.SetPixel(152, 300, background);
                CollectionAssert.AreEqual(new[] { new Point(152, 266) }, UbisoftDirectInstallController.GetTickedShortcuts(page));
            }
        }

        [Test]
        public void UbisoftKnowsTheLicenseEvenWithAShortButtonTest()
        {
            using (var page = UbisoftPage(continueColor: pressedBlue))
            {
                for (var y = 140; y < 300; y++)
                {
                    page.SetPixel(100, y, Color.White);
                }

                Assert.AreEqual(UbisoftStep.License, UbisoftDirectInstallController.Classify(page));
            }
        }

        [Test]
        public void UbisoftFolderPageIsNotTakenForALicenseTest()
        {
            using (var page = UbisoftPage(continueColor: pressedBlue))
            {
                for (var y = 120; y < 190; y += 2)
                {
                    page.SetPixel(100, y, Color.White);
                }

                Assert.AreEqual(UbisoftStep.Continue, UbisoftDirectInstallController.Classify(page));
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
        public void UbisoftReadsTheButtonAboveItsTextTest()
        {
            // Measured on the English page of the language: the middle of the button falls on a letter.
            using (var page = UbisoftPage())
            {
                page.SetPixel(615, 403, Color.FromArgb(189, 174, 245));
                Assert.AreEqual(UbisoftStep.Continue, UbisoftDirectInstallController.Classify(page));
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
            Assert.AreEqual(EaStep.Nothing, EaDirectInstallController.Classify(new List<StoreControl>(), "Zau"));

            Assert.AreEqual(EaStep.GameNotInstalled, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Juego no instalado", TextRole, 2),
                new FakeControl("Comprueba que Tales of Kenzera™: ZAU está instalado.", TextRole, 2),
                new FakeControl("Obtener el juego", ButtonRole, 2),
                new FakeControl("Cerrar", ButtonRole, 2)
            }, "Tales of Kenzera: ZAU"));

            Assert.AreEqual(EaStep.DownloadOptions, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Cerrar", ButtonRole, 2),
                new FakeControl("Cambiar carpeta", ButtonRole, 2),
                new FakeControl("Crear acceso directo", CheckBoxRole, 3, CheckedState),
                new FakeControl("Siguiente", ButtonRole, 2),
                new FakeControl("Cancelar", ButtonRole, 2)
            }, "Zau"));

            Assert.AreEqual(EaStep.Terms, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Acuerdo de usuario", LinkRole, 3),
                new FakeControl("Descargar", ButtonRole, 2),
                new FakeControl("Cancelar", ButtonRole, 2)
            }, "Zau"));
        }

        private const string EaGame = "Tales of Kenzera: ZAU";

        private static List<StoreControl> EaNotice(FakeControl main, FakeControl close = null)
        {
            return new List<StoreControl>
            {
                new FakeControl("Game not installed", DialogRole, 1),
                new FakeControl("Check that Tales of Kenzera™: ZAU is installed.", TextRole, 2),
                main,
                close ?? new FakeControl("Close", ButtonRole, 2)
            };
        }

        private static List<StoreControl> EaPage(FakeControl download)
        {
            return new List<StoreControl>
            {
                new FakeControl("Tales of Kenzera™: ZAU", TextRole, 3),
                new FakeControl("Standard Edition owned", TextRole, 3),
                download
            };
        }

        private static List<StoreControl> EaOptions(FakeControl shortcut, FakeControl updates, FakeControl next)
        {
            return new List<StoreControl>
            {
                new FakeControl("Download options", DialogRole, 1),
                new FakeControl("Close", ButtonRole, 2),
                new FakeControl("Change install location", ButtonRole, 2),
                new FakeControl("English", ButtonRole, 2),
                shortcut,
                updates,
                next,
                new FakeControl("Cancel", ButtonRole, 2)
            };
        }

        private static List<StoreControl> EaTerms(FakeControl download)
        {
            return new List<StoreControl>
            {
                new FakeControl("", DialogRole, 1),
                new FakeControl("Close", ButtonRole, 2),
                new FakeControl("Electronic Arts User Agreement", LinkRole, 3),
                new FakeControl("Legal disclosures", ButtonRole, 3),
                download,
                new FakeControl("Cancel", ButtonRole, 2)
            };
        }

        [Test]
        public void EaFlowPressesEachStepOnceAndStopsAtTheTermsTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, false, now);

            var getGame = new FakeControl("Get the game", ButtonRole, 2);
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(EaNotice(getGame), now));
            Assert.AreEqual(0, getGame.Presses, "A first reading is not enough to press.");
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(EaNotice(getGame), now));
            Assert.AreEqual(1, getGame.Presses);

            var download = new FakeControl("Download", ButtonRole, 4);
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(EaPage(download), now));
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(EaPage(download), now));
            Assert.AreEqual(1, download.Presses, "The download button of the page goes only once.");

            var shortcut = new FakeControl("Create desktop shortcut", CheckBoxRole, 3, CheckedState);
            var updates = new FakeControl("Update games automatically", CheckBoxRole, 3, CheckedState);
            var next = new FakeControl("NEXT", ButtonRole, 2);
            flow.Advance(EaOptions(shortcut, updates, next), now);
            flow.Advance(EaOptions(shortcut, updates, next), now);
            Assert.AreEqual(1, shortcut.Presses);
            Assert.AreEqual(0, updates.Presses);
            Assert.AreEqual(1, next.Presses);

            var accept = new FakeControl("Download", ButtonRole, 2);
            Assert.AreEqual(EaOutcome.AskPerson, flow.Advance(EaTerms(accept), now));
            Assert.AreEqual(0, accept.Presses);
        }

        [Test]
        public void EaNeverAnswersWhatComesAfterTheOptionsTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, true, now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaPage(new FakeControl("Download", ButtonRole, 4)), now);
            var next = new FakeControl("NEXT", ButtonRole, 2);
            var options = EaOptions(new FakeControl("Shortcut", CheckBoxRole, 3), new FakeControl("Updates", CheckBoxRole, 3), next);
            flow.Advance(options, now);
            flow.Advance(options, now);

            // Terms without a link, or an offer that names the game: even with the option on, it isn't Atrio's to answer.
            var main = new FakeControl("I agree", ButtonRole, 2);
            var unknown = EaNotice(main);
            Assert.AreEqual(EaOutcome.AskPerson, flow.Advance(unknown, now));
            Assert.AreEqual(0, main.Presses);
        }

        [Test]
        public void EaAcceptsTheTermsOnlyWithTheOptionTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, true, now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaPage(new FakeControl("Download", ButtonRole, 4)), now);
            var options = EaOptions(new FakeControl("Shortcut", CheckBoxRole, 3), new FakeControl("Updates", CheckBoxRole, 3), new FakeControl("NEXT", ButtonRole, 2));
            flow.Advance(options, now);
            flow.Advance(options, now);
            var accept = new FakeControl("Download", ButtonRole, 2);
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(EaTerms(accept), now));
            Assert.AreEqual(EaOutcome.Started, flow.Advance(EaTerms(accept), now));
            Assert.AreEqual(1, accept.Presses);
        }

        [Test]
        public void EaFlowStopsAtADialogOutOfPlaceTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, false, now);
            var accept = new FakeControl("Download", ButtonRole, 2);
            Assert.AreEqual(EaOutcome.Stop, flow.Advance(EaTerms(accept), now));
            Assert.AreEqual(0, accept.Presses);
        }

        [Test]
        public void EaFlowGivesUpWhenThePageNeverArrivesTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, false, now);
            var getGame = new FakeControl("Get the game", ButtonRole, 2);
            flow.Advance(EaNotice(getGame), now);
            flow.Advance(EaNotice(getGame), now);
            var otherGame = new FakeControl("Download", ButtonRole, 4);
            var page = new List<StoreControl> { new FakeControl("The Sims 4", TextRole, 3), otherGame };
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(page, now.AddSeconds(5)));
            Assert.AreEqual(EaOutcome.Stop, flow.Advance(page, now.AddSeconds(60)));
            Assert.AreEqual(0, otherGame.Presses, "The page of another game is not touched.");
        }

        [Test]
        public void EaFlowEndsWhenAGameHasNoTermsTest()
        {
            var now = new DateTime(2026, 11, 7);
            var flow = new EaInstallFlow(EaGame, false, now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaNotice(new FakeControl("Get the game", ButtonRole, 2)), now);
            flow.Advance(EaPage(new FakeControl("Download", ButtonRole, 4)), now);
            var options = EaOptions(new FakeControl("Shortcut", CheckBoxRole, 3), new FakeControl("Updates", CheckBoxRole, 3), new FakeControl("NEXT", ButtonRole, 2));
            flow.Advance(options, now);
            flow.Advance(options, now);
            var library = new List<StoreControl> { new FakeControl("Library", ButtonRole, 1) };
            Assert.AreEqual(EaOutcome.Waiting, flow.Advance(library, now.AddSeconds(2)));
            Assert.AreEqual(EaOutcome.Started, flow.Advance(library, now.AddSeconds(8)));
        }

        [Test]
        public void EaLeavesAloneANoticeAboutSomethingElseTest()
        {
            var main = new FakeControl("Join EA Play", ButtonRole, 2);
            Assert.AreEqual(EaStep.Other, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Play more with a subscription", TextRole, 2),
                main,
                new FakeControl("Not now", ButtonRole, 2)
            }, "Tales of Kenzera: ZAU"));
            Assert.AreEqual(0, main.Presses);
        }

        [Test]
        public void EaKnowsTheGameWhenTheEditionIsDifferentTest()
        {
            Assert.AreEqual(EaStep.GameNotInstalled, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("Check that STAR WARS Jedi: Fallen Order™ is installed.", TextRole, 2),
                new FakeControl("Get the game", ButtonRole, 2),
                new FakeControl("Close", ButtonRole, 2)
            }, "STAR WARS Jedi: Fallen Order Deluxe Edition"));
        }

        [Test]
        public void EaDialogWithASingleButtonStopsEverythingTest()
        {
            Assert.AreEqual(EaStep.Other, EaDirectInstallController.Classify(new List<StoreControl>
            {
                new FakeControl("You don't have access", TextRole, 2),
                new FakeControl("OK", ButtonRole, 2)
            }, "Zau"));
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
        public void ItchDoesNotTakeASoundtrackForTheGameTest()
        {
            var uploads = JArray.Parse(@"[{""id"":1,""type"":""soundtrack"",""platforms"":{}},{""id"":3,""type"":""book"",""platforms"":{}}]");
            Assert.IsNull(ItchDirectInstallController.PickUpload(uploads));
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
