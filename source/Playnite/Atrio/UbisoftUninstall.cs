using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>The two pages of the small window of Ubisoft Connect while it uninstalls a game.</summary>
    public enum UbisoftUninstallPage
    {
        Unknown,

        /// <summary>"Are you sure you want to proceed?" with a blue Yes at the bottom right.</summary>
        Question,

        /// <summary>"The game was successfully uninstalled." with a white tick and Close at the bottom left.</summary>
        Done
    }

    /// <summary>
    /// Ubisoft Connect has no silent uninstall: its add-on opens a small window that asks "Are you sure?" and
    /// later says it's done. The person already said yes in Atrio, so Atrio answers that window: it presses Yes
    /// and, at the end, Close. It only touches those two pages, recognized by their shape and colors in a copy of
    /// the window; anything else stays for the person, as without Atrio.
    /// </summary>
    public class UbisoftDirectUninstallController : StoreDirectUninstallController
    {
        private const string Process = "upc";
        private const string WindowClass = "ModalViewClassName";

        /// <summary>The window measured at 100 %: points scale with its real width.</summary>
        public const double BaseWidth = 534;
        public const double BaseHeight = 314;

        private static readonly Point yesPoint = new Point(443, 264);
        private static readonly Point[] yesColorPoints = { new Point(397, 256), new Point(490, 256), new Point(397, 272), new Point(490, 272) };
        private static readonly Point tickPoint = new Point(44, 139);
        private static readonly Point closePoint = new Point(50, 264);
        private static readonly TimeSpan questionPatience = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan removalPatience = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan noticePatience = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan checkPause = TimeSpan.FromMilliseconds(700);

        private volatile bool storeFinished;
        private bool launcherWasOpen;

        public UbisoftDirectUninstallController(Game game, UninstallController store) : base(game, store)
        {
            store.Uninstalled += (_, __) => storeFinished = true;
        }

        public static bool IsBlue(Color color)
        {
            return color.R < 40 && color.G > 70 && color.G < 150 && color.B > 200;
        }

        private static bool IsWhite(Color color)
        {
            return color.R > 200 && color.G > 200 && color.B > 200;
        }

        private static Color GetColor(Bitmap page, Point point)
        {
            var scale = page.Width / BaseWidth;
            var x = Math.Min(page.Width - 1, (int)(point.X * scale));
            var y = Math.Min(page.Height - 1, (int)(point.Y * scale));
            return page.GetPixel(x, y);
        }

        /// <summary>Which page the window shows, from a copy of it. Unknown for anything else, even another window of the same size.</summary>
        public static UbisoftUninstallPage Classify(Bitmap page)
        {
            if (page == null || page.Width < 100)
            {
                return UbisoftUninstallPage.Unknown;
            }

            // Same shape as the one measured, whatever the scale of the screen.
            var ratio = page.Width / (double)page.Height;
            if (Math.Abs(ratio - (BaseWidth / BaseHeight)) > 0.08)
            {
                return UbisoftUninstallPage.Unknown;
            }

            var yes = Array.TrueForAll(yesColorPoints, a => IsBlue(GetColor(page, a)));
            if (yes)
            {
                return UbisoftUninstallPage.Question;
            }

            // The tick: a white mark in a corner that is dark on every other page.
            var tick = false;
            for (var dx = -6; dx <= 6 && !tick; dx += 2)
            {
                for (var dy = -6; dy <= 6 && !tick; dy += 2)
                {
                    tick = IsWhite(GetColor(page, new Point(tickPoint.X + dx, tickPoint.Y + dy)));
                }
            }

            return tick ? UbisoftUninstallPage.Done : UbisoftUninstallPage.Unknown;
        }

        private static IntPtr FindWindow()
        {
            return StoreWindows.Find(Process, (_, type) => type == WindowClass);
        }

        private static void Click(IntPtr window, Bitmap page, Point point)
        {
            var scale = page.Width / BaseWidth;
            StoreWindows.PostClick(window, (int)(point.X * scale), (int)(point.Y * scale));
        }

        private static UbisoftUninstallPage Look(out IntPtr window, out Bitmap page)
        {
            page = null;
            window = FindWindow();
            if (window == IntPtr.Zero)
            {
                return UbisoftUninstallPage.Unknown;
            }

            page = StoreWindows.Capture(window);
            return Classify(page);
        }

        public override async void Uninstall(UninstallActionArgs args)
        {
            // The add-on asks Ubisoft Connect, as always, and notices by itself when the game is gone.
            launcherWasOpen = System.Diagnostics.Process.GetProcessesByName(Process).Length > 0;
            UseStore(args);
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                return;
            }

            try
            {
                await AnswerAsync(watcher.Token);
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested)
            {
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // The window stays for the person: the uninstall goes on as without Atrio.
                logger.Warn(e, $"Failed to answer Ubisoft Connect for {Game.GameId}.");
            }
        }

        private async Task AnswerAsync(CancellationToken token)
        {
            var started = DateTime.Now;
            var answered = false;
            while (!answered && DateTime.Now - started < questionPatience)
            {
                await Task.Delay(checkPause, token);
                var kind = Look(out var window, out var page);
                using (page)
                {
                    if (kind == UbisoftUninstallPage.Question)
                    {
                        Click(window, page, yesPoint);
                        answered = true;
                        logger.Info($"Ubisoft Connect asked about {Game.GameId}: Atrio said yes, as the person did.");
                    }
                }
            }

            if (!answered)
            {
                logger.Info($"Ubisoft Connect didn't show its question for {Game.GameId}: its window stays for the person.");
                return;
            }

            // From here on the watch doesn't end with the controller: Atrio lets go of it as soon as the add-on
            // says the game is gone, and the notice of Ubisoft Connect comes right after that.
            started = DateTime.Now;
            DateTime? finishedAt = null;
            while (DateTime.Now - started < removalPatience)
            {
                await Task.Delay(checkPause);
                if (storeFinished && finishedAt == null)
                {
                    finishedAt = DateTime.Now;
                }

                if (finishedAt != null && DateTime.Now - finishedAt.Value > noticePatience)
                {
                    break;
                }

                var kind = Look(out var window, out var page);
                using (page)
                {
                    if (kind == UbisoftUninstallPage.Done)
                    {
                        Click(window, page, closePoint);
                        logger.Info($"Ubisoft Connect uninstalled {Game.GameId}: Atrio closed its notice.");

                        // It was Atrio that opened the launcher: it goes back down, as if it had never come up.
                        if (!launcherWasOpen)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            StoreWindows.Minimize(StoreWindows.Find(Process, (_, type) => type != WindowClass));
                        }

                        return;
                    }
                }
            }
        }
    }
}
