using Newtonsoft.Json;
using Playnite.Common;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>What's inside the dialogs of a window, read from its controls.</summary>
    public static class StoreDialogs
    {
        /// <summary>The controls of the last dialog of the window, the one on top. Empty if there are none.</summary>
        public static List<StoreControl> GetDialog(List<StoreControl> controls)
        {
            var start = controls.FindLastIndex(a => a.IsDialog);
            if (start < 0)
            {
                return new List<StoreControl>();
            }

            var depth = controls[start].Depth;
            return controls.Skip(start + 1).TakeWhile(a => a.Depth > depth).ToList();
        }
    }

    /// <summary>
    /// The Amazon Games app asks for the folder in a dialog, which is the only thing between the
    /// link the add-on sends and the download.
    /// </summary>
    public class AmazonDirectInstallController : AssistedInstallController
    {
        private const string Process = "Amazon Games UI";
        private const string StoreName = "Amazon Games";
        private const int MinDialogButtons = 3;

        private static readonly TimeSpan dialogPatience = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan closePause = TimeSpan.FromMilliseconds(1500);

        public AmazonDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        /// <summary>
        /// The install dialog has a checkbox and three buttons: the folder, cancel and go ahead. The last one
        /// only appears when the app has read what the game needs, before that the last one is cancel.
        /// </summary>
        public static bool IsInstallDialog(List<StoreControl> controls)
        {
            var dialog = StoreDialogs.GetDialog(controls);
            var buttons = dialog.Where(a => a.IsButton).ToList();
            return dialog.Any(a => a.IsCheckBox) && buttons.Count >= MinDialogButtons && buttons.Last().IsEnabled;
        }

        public static bool AcceptDialog(List<StoreControl> controls)
        {
            if (!IsInstallDialog(controls))
            {
                return false;
            }

            var dialog = StoreDialogs.GetDialog(controls);
            var shortcut = dialog.First(a => a.IsCheckBox);
            if (shortcut.IsChecked)
            {
                shortcut.Press();
            }

            dialog.Last(a => a.IsButton).Press();
            return true;
        }

        protected override async Task AssistAsync()
        {
            var started = DateTime.Now;
            var window = IntPtr.Zero;
            var wasReady = false;
            while (DateTime.Now - started < dialogPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                window = StoreWindows.Find(Process);
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                // Two readings in a row: the dialog could still be changing under the first one.
                var ready = StoreWindows.WithControls(window, IsInstallDialog);
                if (ready && wasReady && StoreWindows.WithControls(window, AcceptDialog))
                {
                    Logger.Info($"Amazon Games told to install {Game.GameId}.");
                    await Task.Delay(closePause, Cancellation).ConfigureAwait(false);
                    StoreWindows.Minimize(window);
                    return;
                }

                wasReady = ready;
            }

            Logger.Warn($"The Amazon Games app didn't show the install of {Game.GameId}.");
            HandOver(window, StoreName);
        }
    }

    /// <summary>What the install window of Ubisoft Connect is asking, seen in its pixels: it has no controls to read.</summary>
    public enum UbisoftStep
    {
        Unknown,
        Continue,
        Shortcuts,
        License
    }

    /// <summary>
    /// Ubisoft Connect installs with a small window of several pages. It always has the same size
    /// and its button in the same place, and that button only gets wider on the page of the license.
    /// </summary>
    public class UbisoftDirectInstallController : AssistedInstallController
    {
        private const string Process = "upc";
        private const string WindowClass = "uplay_download";
        private const string StoreName = "Ubisoft Connect";

        // Measured on a window of this size, the rest come from it for other scales.
        private const double BaseWidth = 714;
        private static readonly Point continuePoint = new Point(615, 403);
        private static readonly Point licensePoint = new Point(480, 403);
        private static readonly Point menuShortcutPoint = new Point(152, 266);
        private static readonly Point desktopShortcutPoint = new Point(152, 300);

        // Measured: the license has text in 146 of these 215 lines, the other pages in 67 at most.
        private const int TextTop = 130;
        private const int TextBottom = 345;
        private const int TextLeft = 30;
        private const int TextRight = 680;
        private const double LicenseTextShare = 0.5;

        private const int MinPageWidth = 300;
        private const int MaxPressesWithoutChange = 2;
        private static readonly TimeSpan windowPatience = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan pagePause = TimeSpan.FromSeconds(2);

        public UbisoftDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        private static bool IsButtonColor(Color color)
        {
            return color.R < 40 && color.G > 60 && color.G < 140 && color.B > 170;
        }

        private static bool IsTick(Color color)
        {
            return color.R > 200 && color.G > 200 && color.B > 200;
        }

        private static bool IsText(Color color)
        {
            return color.R > 170 && color.G > 170 && color.B > 170;
        }

        /// <summary>
        /// The license fills the window with text, in any language, while the other pages leave most
        /// of the lines empty. The width of the button isn't enough: in some languages it's short.
        /// </summary>
        private static bool HasLongText(Bitmap page)
        {
            var scale = page.Width / BaseWidth;
            var top = (int)(TextTop * scale);
            var bottom = Math.Min(page.Height, (int)(TextBottom * scale));
            var left = (int)(TextLeft * scale);
            var right = Math.Min(page.Width, (int)(TextRight * scale));
            var lines = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x += 2)
                {
                    if (IsText(page.GetPixel(x, y)))
                    {
                        lines++;
                        break;
                    }
                }
            }

            return lines > (bottom - top) * LicenseTextShare;
        }

        private static Color GetColor(Bitmap page, Point point)
        {
            var scale = page.Width / BaseWidth;
            return page.GetPixel((int)(point.X * scale), (int)(point.Y * scale));
        }

        public static UbisoftStep Classify(Bitmap page)
        {
            if (!IsButtonColor(GetColor(page, continuePoint)))
            {
                return UbisoftStep.Unknown;
            }

            if (IsButtonColor(GetColor(page, licensePoint)) || HasLongText(page))
            {
                return UbisoftStep.License;
            }

            return IsTick(GetColor(page, menuShortcutPoint)) || IsTick(GetColor(page, desktopShortcutPoint)) ?
                UbisoftStep.Shortcuts :
                UbisoftStep.Continue;
        }

        /// <summary>The boxes to click so that no shortcut is left, only the ones that are ticked.</summary>
        public static List<Point> GetTickedShortcuts(Bitmap page)
        {
            return new[] { desktopShortcutPoint, menuShortcutPoint }.Where(a => IsTick(GetColor(page, a))).ToList();
        }

        /// <summary>Tells apart two moments of the window, to know if it's still drawing or if a click changed the page.</summary>
        private static long GetFingerprint(Bitmap page)
        {
            long total = 0;
            for (var x = 20; x < page.Width - 20; x += 23)
            {
                for (var y = 60; y < page.Height - 80; y += 17)
                {
                    total = (total * 31) + page.GetPixel(x, y).ToArgb();
                }
            }

            return total;
        }

        private static void Click(IntPtr window, Bitmap page, Point point)
        {
            var scale = page.Width / BaseWidth;
            StoreWindows.PostClick(window, (int)(point.X * scale), (int)(point.Y * scale));
        }

        protected override async Task AssistAsync()
        {
            var started = DateTime.Now;
            var seen = false;
            var pressesWithoutChange = 0;
            long? lastRead = null;
            long? lastPressed = null;
            var window = IntPtr.Zero;
            while (DateTime.Now - started < windowPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                window = StoreWindows.Find(Process, (_, type) => type == WindowClass);
                if (window == IntPtr.Zero)
                {
                    if (seen)
                    {
                        Logger.Info($"Ubisoft Connect took the install of {Game.GameId}.");
                        StoreWindows.Minimize(StoreWindows.Find(Process));
                        return;
                    }

                    continue;
                }

                seen = true;
                using (var page = StoreWindows.Capture(window))
                {
                    if (page == null || page.Width < MinPageWidth)
                    {
                        continue;
                    }

                    // Only on a page that stopped changing: a license without its text yet could pass for another page.
                    var fingerprint = GetFingerprint(page);
                    var settled = fingerprint == lastRead;
                    lastRead = fingerprint;
                    if (!settled)
                    {
                        continue;
                    }

                    var step = Classify(page);
                    if (step == UbisoftStep.Unknown)
                    {
                        continue;
                    }

                    if (step == UbisoftStep.License && !DirectInstall.AcceptsStoreTerms)
                    {
                        await AskPersonAsync(window, StoreName, () => StoreWindows.Find(Process, (_, type) => type == WindowClass) != IntPtr.Zero).ConfigureAwait(false);
                        return;
                    }

                    pressesWithoutChange = fingerprint == lastPressed ? pressesWithoutChange + 1 : 0;
                    if (pressesWithoutChange >= MaxPressesWithoutChange)
                    {
                        // It wants something else, like a code for a game that isn't in the account.
                        Logger.Warn($"The install window of Ubisoft Connect stopped at {Game.GameId}.");
                        HandOver(window, StoreName);
                        return;
                    }

                    foreach (var box in GetTickedShortcuts(page))
                    {
                        Click(window, page, box);
                        await Task.Delay(400, Cancellation).ConfigureAwait(false);
                    }

                    // The button is here in its two widths.
                    Click(window, page, continuePoint);
                    lastPressed = fingerprint;
                }

                await Task.Delay(pagePause, Cancellation).ConfigureAwait(false);
            }

            Logger.Warn($"Ubisoft Connect didn't get through the install of {Game.GameId}.");
            HandOver(window, StoreName);
        }
    }

    public enum EaStep
    {
        Nothing,
        GameNotInstalled,
        DownloadOptions,
        Terms,
        Other
    }

    public enum EaOutcome
    {
        Waiting,
        Started,
        AskPerson,
        Stop
    }

    /// <summary>
    /// The way from the notice of the EA app to the download. Each step happens once and in its order:
    /// anything that shows up out of place stops everything, and whatever comes after the options is
    /// the person's to answer, whatever it looks like, unless they allowed Atrio to accept the terms.
    /// </summary>
    public class EaInstallFlow
    {
        private enum Phase
        {
            Notice,
            Page,
            Options,
            Terms
        }

        private const int NearbyControls = 40;

        // The name of the button of the page of a game in the languages of the app.
        private static readonly HashSet<string> downloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Download", "Descargar", "Télécharger", "Herunterladen", "Scarica", "Baixar", "Pobierz", "Скачать", "Загрузить", "ダウンロード", "다운로드", "下载", "下載"
        };

        private static readonly TimeSpan noticePatience = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan stepPatience = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan startPause = TimeSpan.FromSeconds(6);

        private readonly string game;
        private readonly bool acceptsTerms;
        private Phase phase = Phase.Notice;
        private DateTime phaseStart;
        private EaStep lastStep = EaStep.Nothing;

        public EaInstallFlow(string game, bool acceptsTerms, DateTime now)
        {
            this.game = game;
            this.acceptsTerms = acceptsTerms;
            phaseStart = now;
        }

        /// <summary>The main button of a dialog: the last two are always that one and the one to leave.</summary>
        private static StoreControl GetMainButton(List<StoreControl> dialog)
        {
            var buttons = dialog.Where(a => a.IsButton).ToList();
            return buttons[buttons.Count - 2];
        }

        /// <summary>The button of the page of this game, by its name and with the game right above it.</summary>
        public static StoreControl FindDownload(List<StoreControl> controls, string game)
        {
            for (var i = 0; i < controls.Count; i++)
            {
                var control = controls[i];
                if (control.IsButton && control.Name != null && downloadNames.Contains(control.Name.Trim()))
                {
                    var above = controls.Skip(Math.Max(0, i - NearbyControls)).Take(Math.Min(i, NearbyControls)).ToList();
                    if (EaDirectInstallController.Mentions(above, game))
                    {
                        return control;
                    }
                }
            }

            return null;
        }

        private void Enter(Phase next, DateTime now)
        {
            phase = next;
            phaseStart = now;
            lastStep = EaStep.Nothing;
        }

        /// <summary>Two readings in a row: a dialog that's still being drawn can look like another one.</summary>
        private bool Settled(EaStep step)
        {
            var settled = step == lastStep;
            lastStep = step;
            return settled;
        }

        private EaOutcome WaitOrStop(DateTime now, TimeSpan patience)
        {
            return now - phaseStart > patience ? EaOutcome.Stop : EaOutcome.Waiting;
        }

        private EaOutcome AdvanceNotice(List<StoreControl> dialog, EaStep step, DateTime now)
        {
            if (step == EaStep.Nothing)
            {
                return WaitOrStop(now, noticePatience);
            }

            if (step != EaStep.GameNotInstalled)
            {
                return EaOutcome.Stop;
            }

            if (!Settled(step) || !GetMainButton(dialog).IsEnabled)
            {
                return EaOutcome.Waiting;
            }

            GetMainButton(dialog).Press();
            Enter(Phase.Page, now);
            return EaOutcome.Waiting;
        }

        private EaOutcome AdvancePage(List<StoreControl> controls, EaStep step, DateTime now)
        {
            // With the page of the game already open the notice stays, but the page is right behind it.
            if (step != EaStep.Nothing && step != EaStep.GameNotInstalled)
            {
                return EaOutcome.Stop;
            }

            var download = FindDownload(controls, game);
            if (download == null || !download.IsEnabled)
            {
                return WaitOrStop(now, stepPatience);
            }

            download.Press();
            Enter(Phase.Options, now);
            return EaOutcome.Waiting;
        }

        private EaOutcome AdvanceOptions(List<StoreControl> dialog, EaStep step, DateTime now)
        {
            if (step == EaStep.Nothing || step == EaStep.GameNotInstalled)
            {
                return WaitOrStop(now, stepPatience);
            }

            if (step == EaStep.Terms)
            {
                Enter(Phase.Terms, now);
                return AdvanceTerms(dialog, step, now);
            }

            if (step != EaStep.DownloadOptions)
            {
                return EaOutcome.Stop;
            }

            if (!Settled(step))
            {
                return EaOutcome.Waiting;
            }

            // The first one is the shortcut. The second one, the automatic updates, is none of Atrio's business.
            var shortcut = dialog.First(a => a.IsCheckBox);
            if (shortcut.IsChecked)
            {
                shortcut.Press();
            }

            var next = GetMainButton(dialog);
            if (!next.IsEnabled)
            {
                return WaitOrStop(now, stepPatience);
            }

            next.Press();
            Enter(Phase.Terms, now);
            return EaOutcome.Waiting;
        }

        private EaOutcome AdvanceTerms(List<StoreControl> dialog, EaStep step, DateTime now)
        {
            if (step == EaStep.Nothing)
            {
                // This game had nothing to accept and the download is on its way.
                return now - phaseStart > startPause ? EaOutcome.Started : EaOutcome.Waiting;
            }

            if (step == EaStep.DownloadOptions)
            {
                return WaitOrStop(now, stepPatience);
            }

            if (step != EaStep.Terms || !acceptsTerms)
            {
                return EaOutcome.AskPerson;
            }

            if (!Settled(step))
            {
                return EaOutcome.Waiting;
            }

            var download = GetMainButton(dialog);
            if (!download.IsEnabled)
            {
                return now - phaseStart > stepPatience ? EaOutcome.AskPerson : EaOutcome.Waiting;
            }

            download.Press();
            return EaOutcome.Started;
        }

        public EaOutcome Advance(List<StoreControl> controls, DateTime now)
        {
            var dialog = StoreDialogs.GetDialog(controls);
            var step = EaDirectInstallController.Classify(dialog, game);
            switch (phase)
            {
                case Phase.Notice:
                    return AdvanceNotice(dialog, step, now);
                case Phase.Page:
                    return AdvancePage(controls, step, now);
                case Phase.Options:
                    return AdvanceOptions(dialog, step, now);
                default:
                    return AdvanceTerms(dialog, step, now);
            }
        }
    }

    /// <summary>
    /// The EA app has no order to install: to a link it answers that the game isn't installed and offers
    /// to go to its page, and the page has a button that opens the options. All of it can be read and
    /// pressed by its structure, in any language, up to the terms of the game.
    /// </summary>
    public class EaDirectInstallController : AssistedInstallController
    {
        private const string Process = "EADesktop";
        private const string StoreName = "EA app";
        private const int MentionLength = 10;
        private const int MinOptionsButtons = 4;
        private const string CatalogUrl = "https://service-aggregation-layer.juno.ea.com/graphql";
        private const string CatalogQuery = "query getLegacyCatalogDefs($offerIds: [String!]!, $locale: Locale) { legacyOffers(offerIds: $offerIds, locale: $locale) { offerId: id contentId } }";

        private static readonly TimeSpan stepsPatience = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan checkPause = TimeSpan.FromMilliseconds(1500);
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private class OfferAnswer
        {
            public OfferData data { get; set; }
        }

        private class OfferData
        {
            public List<Offer> legacyOffers { get; set; }
        }

        private class Offer
        {
            public string offerId { get; set; }
            public string contentId { get; set; }
        }

        public EaDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        public static string ParseContentId(string answer, string offerId)
        {
            var offers = JsonConvert.DeserializeObject<OfferAnswer>(answer)?.data?.legacyOffers;
            return offers?.FirstOrDefault(a => a.offerId == offerId)?.contentId;
        }

        /// <summary>The add-on knows the offer of a game, and the app only understands its content.</summary>
        private async Task<string> FindContentAsync()
        {
            var question = JsonConvert.SerializeObject(new
            {
                query = CatalogQuery,
                operationName = "getLegacyCatalogDefs",
                variables = new { locale = "DEFAULT", offerIds = new[] { Game.GameId } }
            });

            using (var body = new StringContent(question, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(CatalogUrl, body, Cancellation).ConfigureAwait(false))
            {
                return ParseContentId(await response.Content.ReadAsStringAsync().ConfigureAwait(false), Game.GameId);
            }
        }

        /// <summary>Only letters and numbers: the app adds ™ and ® that the name in Atrio doesn't have.</summary>
        private static string Simplify(string text)
        {
            return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        /// <summary>The start of the name is enough: the app and Atrio can differ in the edition.</summary>
        public static bool Mentions(List<StoreControl> controls, string game)
        {
            var name = Simplify(game);
            name = name.Substring(0, Math.Min(MentionLength, name.Length));
            return name.Length > 0 && controls.Any(a => Simplify(a.Name).Contains(name));
        }

        /// <summary>
        /// The dialogs are told apart by what they hold, the words depend on the language. A notice with
        /// two buttons that doesn't talk about the game is something else, like an offer: it isn't touched.
        /// </summary>
        public static EaStep Classify(List<StoreControl> dialog, string game)
        {
            if (dialog.Count == 0)
            {
                return EaStep.Nothing;
            }

            var buttons = dialog.Count(a => a.IsButton);
            if (buttons < 2)
            {
                return EaStep.Other;
            }

            if (dialog.Any(a => a.IsLink))
            {
                return EaStep.Terms;
            }

            if (dialog.Any(a => a.IsCheckBox))
            {
                return buttons >= MinOptionsButtons ? EaStep.DownloadOptions : EaStep.Other;
            }

            return Mentions(dialog, game) ? EaStep.GameNotInstalled : EaStep.Other;
        }

        protected override async Task AssistAsync()
        {
            var content = await FindContentAsync().ConfigureAwait(false);
            if (content.IsNullOrEmpty())
            {
                Logger.Warn($"The catalog of EA doesn't have {Game.GameId}.");
                return;
            }

            ProcessStarter.StartUrl("origin2://game/launch/?offerIds=" + content);
            var flow = new EaInstallFlow(Game.Name, DirectInstall.AcceptsStoreTerms, DateTime.Now);
            var started = DateTime.Now;
            var window = IntPtr.Zero;
            while (DateTime.Now - started < stepsPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                window = StoreWindows.Find(Process, (title, _) => title == "EA");
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                switch (StoreWindows.WithControls(window, controls => flow.Advance(controls, DateTime.Now)))
                {
                    case EaOutcome.Started:
                        StoreWindows.Minimize(window);
                        return;
                    case EaOutcome.AskPerson:
                        await AskPersonAsync(window, StoreName, () => StoreWindows.WithControls(window, a => StoreDialogs.GetDialog(a).Count > 0)).ConfigureAwait(false);
                        return;
                    case EaOutcome.Stop:
                        Logger.Warn($"The EA app answered something else to the install of {Game.GameId}.");
                        HandOver(window, StoreName);
                        return;
                }
            }

            Logger.Warn($"The EA app didn't get to the install of {Game.GameId}.");
            HandOver(window, StoreName);
        }
    }
}
