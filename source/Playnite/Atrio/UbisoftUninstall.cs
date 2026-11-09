using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

        private static List<IntPtr> FindWindows()
        {
            return StoreWindows.FindAll(Process, (_, type) => type == WindowClass);
        }

        private static void Click(IntPtr window, Bitmap page, Point point)
        {
            var scale = page.Width / BaseWidth;
            StoreWindows.PostClick(window, (int)(point.X * scale), (int)(point.Y * scale));
        }

        private static UbisoftUninstallPage Look(IntPtr window, out Bitmap page)
        {
            page = StoreWindows.Capture(window);
            return Classify(page);
        }

        /// <summary>Its small windows that were already there before the order: none of them is ever touched.</summary>
        private HashSet<IntPtr> before = new HashSet<IntPtr>();

        /// <summary>Ends the wait of the final notice if the controller goes away without the game being gone.</summary>
        private readonly CancellationTokenSource noticeWatch = new CancellationTokenSource();

        public override async void Uninstall(UninstallActionArgs args)
        {
            // The add-on asks Ubisoft Connect, as always, and notices by itself when the game is gone.
            launcherWasOpen = System.Diagnostics.Process.GetProcessesByName(Process).Length > 0;
            before = new HashSet<IntPtr>(FindWindows());
            var log = UbisoftClientFiles.LogFile is string file && System.IO.File.Exists(file) ? new LogFollower(file) : null;
            UseStore(args);
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                return;
            }

            try
            {
                await AnswerAsync(log, watcher.Token);
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested || noticeWatch.IsCancellationRequested)
            {
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // The window stays for the person: the uninstall goes on as without Atrio.
                logger.Warn(e, $"Failed to answer Ubisoft Connect for {Game.GameId}.");
            }
        }

        public override void Dispose()
        {
            if (!storeFinished)
            {
                noticeWatch.Cancel();
            }

            base.Dispose();
        }

        /// <summary>The launcher wrote that it got the order to uninstall this game, not another one.</summary>
        private bool orderSeen;

        private void ReadLog(LogFollower log)
        {
            if (log == null || orderSeen)
            {
                return;
            }

            try
            {
                orderSeen = log.ReadNew().Any(a => UbisoftClientFiles.IsUninstallOrder(a, Game.GameId));
            }
            catch (System.IO.IOException)
            {
                // The launcher is writing it, it's read again in the next round.
            }
        }

        /// <summary>
        /// Presses Yes only in a question that appeared after the order, once the log of the launcher says the order
        /// was for this game, and only when two copies in a row show the same question. If the question is still
        /// there after the click, it's left for the person.
        /// </summary>
        private async Task AnswerAsync(LogFollower log, CancellationToken token)
        {
            var started = DateTime.Now;
            IntPtr answered = IntPtr.Zero;
            IntPtr seenOnce = IntPtr.Zero;
            while (answered == IntPtr.Zero && DateTime.Now - started < questionPatience)
            {
                await Task.Delay(checkPause, token);
                ReadLog(log);
                if (log != null && !orderSeen)
                {
                    continue;
                }

                IntPtr question = IntPtr.Zero;
                Bitmap questionPage = null;
                foreach (var window in FindWindows().Where(a => !before.Contains(a)))
                {
                    var kind = Look(window, out var page);
                    if (kind == UbisoftUninstallPage.Question && question == IntPtr.Zero)
                    {
                        question = window;
                        questionPage = page;
                    }
                    else
                    {
                        page?.Dispose();
                    }
                }

                using (questionPage)
                {
                    if (question == IntPtr.Zero)
                    {
                        seenOnce = IntPtr.Zero;
                    }
                    else if (question != seenOnce)
                    {
                        // The first sight: it's pressed only if the next copy still shows it, so it's not a page drawing.
                        seenOnce = question;
                    }
                    else
                    {
                        Click(question, questionPage, yesPoint);
                        answered = question;
                    }
                }
            }

            if (answered == IntPtr.Zero)
            {
                logger.Info($"Ubisoft Connect didn't show its question for {Game.GameId}: its window stays for the person.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (FindWindows().Contains(answered) && Look(answered, out var after) == UbisoftUninstallPage.Question)
            {
                after?.Dispose();
                logger.Info($"Ubisoft Connect still asks about {Game.GameId} after the click: the question stays for the person.");
                return;
            }

            logger.Info($"Ubisoft Connect asked about {Game.GameId}: Atrio said yes, as the person did.");

            // From here on the watch doesn't end with the controller: Atrio lets go of it as soon as the add-on
            // says the game is gone, and the notice of Ubisoft Connect comes right after that. If the controller
            // goes away without that, the wait ends too.
            started = DateTime.Now;
            DateTime? finishedAt = null;
            while (DateTime.Now - started < removalPatience)
            {
                await Task.Delay(checkPause, noticeWatch.Token);
                if (storeFinished && finishedAt == null)
                {
                    finishedAt = DateTime.Now;
                }

                if (finishedAt != null && DateTime.Now - finishedAt.Value > noticePatience)
                {
                    break;
                }

                foreach (var window in FindWindows().Where(a => !before.Contains(a)))
                {
                    var kind = Look(window, out var page);
                    using (page)
                    {
                        if (kind != UbisoftUninstallPage.Done)
                        {
                            continue;
                        }

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
