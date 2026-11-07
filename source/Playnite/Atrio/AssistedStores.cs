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
        public static bool AcceptDialog(List<StoreControl> controls)
        {
            var dialog = StoreDialogs.GetDialog(controls);
            var shortcut = dialog.FirstOrDefault(a => a.IsCheckBox);
            var buttons = dialog.Where(a => a.IsButton).ToList();
            if (shortcut == null || buttons.Count < MinDialogButtons || !buttons.Last().IsEnabled)
            {
                return false;
            }

            if (shortcut.IsChecked)
            {
                shortcut.Press();
            }

            buttons.Last().Press();
            return true;
        }

        protected override async Task AssistAsync()
        {
            var started = DateTime.Now;
            while (DateTime.Now - started < dialogPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                var window = StoreWindows.Find(Process);
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                if (StoreWindows.WithControls(window, AcceptDialog))
                {
                    Logger.Info($"Amazon Games told to install {Game.GameId}.");
                    await Task.Delay(closePause, Cancellation).ConfigureAwait(false);
                    StoreWindows.Minimize(window);
                    return;
                }
            }

            Logger.Warn($"The Amazon Games app didn't show the install of {Game.GameId}.");
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

            if (IsButtonColor(GetColor(page, licensePoint)))
            {
                return UbisoftStep.License;
            }

            return IsTick(GetColor(page, menuShortcutPoint)) || IsTick(GetColor(page, desktopShortcutPoint)) ?
                UbisoftStep.Shortcuts :
                UbisoftStep.Continue;
        }

        /// <summary>Tells apart two moments of the window, to know if a click changed the page.</summary>
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
            long? lastPage = null;
            while (DateTime.Now - started < windowPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                var window = StoreWindows.Find(Process, (_, type) => type == WindowClass);
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
                    if (page == null)
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
                        AskPerson(window, "LOCAtrioAcceptInStore", StoreName);
                        return;
                    }

                    var fingerprint = GetFingerprint(page);
                    if (fingerprint == lastPage && ++pressesWithoutChange >= MaxPressesWithoutChange)
                    {
                        // It wants something else, like a code for a game that isn't in the account.
                        Logger.Warn($"The install window of Ubisoft Connect stopped at {Game.GameId}.");
                        StoreWindows.Show(window);
                        return;
                    }

                    if (fingerprint != lastPage)
                    {
                        pressesWithoutChange = 0;
                    }

                    if (step == UbisoftStep.Shortcuts)
                    {
                        Click(window, page, desktopShortcutPoint);
                        await Task.Delay(300, Cancellation).ConfigureAwait(false);
                        Click(window, page, menuShortcutPoint);
                        await Task.Delay(500, Cancellation).ConfigureAwait(false);
                    }

                    Click(window, page, step == UbisoftStep.License ? licensePoint : continuePoint);
                    lastPage = fingerprint;
                }

                await Task.Delay(pagePause, Cancellation).ConfigureAwait(false);
            }

            Logger.Warn($"Ubisoft Connect didn't show the install of {Game.GameId}.");
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

    /// <summary>
    /// The EA app has no order to install: to a link it answers that the game isn't installed and offers
    /// to go to its page, and the page has a button that opens the options. All of it can be read and
    /// pressed by its structure, in any language, up to the terms of the game.
    /// </summary>
    public class EaDirectInstallController : AssistedInstallController
    {
        private const string Process = "EADesktop";
        private const string StoreName = "EA app";
        private const string CatalogUrl = "https://service-aggregation-layer.juno.ea.com/graphql";
        private const string CatalogQuery = "query getLegacyCatalogDefs($offerIds: [String!]!, $locale: Locale) { legacyOffers(offerIds: $offerIds, locale: $locale) { offerId: id contentId } }";

        // The name of the button of the page of a game in the languages of the app.
        private static readonly HashSet<string> downloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Download", "Descargar", "Télécharger", "Herunterladen", "Scarica", "Baixar", "Pobierz", "Скачать", "Загрузить", "ダウンロード", "다운로드", "下载", "下載"
        };

        private static readonly TimeSpan stepsPatience = TimeSpan.FromMinutes(3);
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

        /// <summary>The dialogs are told apart by what they hold, the words depend on the language.</summary>
        public static EaStep Classify(List<StoreControl> dialog)
        {
            if (dialog.Count == 0)
            {
                return EaStep.Nothing;
            }

            if (dialog.Count(a => a.IsButton) < 2)
            {
                return EaStep.Other;
            }

            if (dialog.Any(a => a.IsLink))
            {
                return EaStep.Terms;
            }

            return dialog.Any(a => a.IsCheckBox) ? EaStep.DownloadOptions : EaStep.GameNotInstalled;
        }

        /// <summary>The main button of a dialog: the last two are always that one and the one to leave.</summary>
        private static StoreControl GetMainButton(List<StoreControl> dialog)
        {
            var buttons = dialog.Where(a => a.IsButton).ToList();
            return buttons[buttons.Count - 2];
        }

        private static void PressIfEnabled(StoreControl control)
        {
            if (control.IsEnabled)
            {
                control.Press();
            }
        }

        private static void PressDownload(List<StoreControl> controls)
        {
            controls.FirstOrDefault(a => a.IsButton && a.Name != null && downloadNames.Contains(a.Name.Trim()))?.Press();
        }

        private EaStep Advance(List<StoreControl> controls, bool pagePending)
        {
            var dialog = StoreDialogs.GetDialog(controls);
            var step = Classify(dialog);
            switch (step)
            {
                case EaStep.GameNotInstalled:
                    // With the page of the game already open the button has nowhere to go and the
                    // notice stays, but the page is right behind it.
                    if (pagePending)
                    {
                        PressDownload(controls);
                    }
                    else
                    {
                        PressIfEnabled(GetMainButton(dialog));
                    }

                    break;
                case EaStep.DownloadOptions:
                    // The first one is the shortcut. The dialog can last for several rounds and the
                    // second one, the automatic updates, is none of Atrio's business.
                    var shortcut = dialog.First(a => a.IsCheckBox);
                    if (shortcut.IsChecked)
                    {
                        shortcut.Press();
                    }

                    PressIfEnabled(GetMainButton(dialog));
                    break;
                case EaStep.Terms:
                    if (DirectInstall.AcceptsStoreTerms)
                    {
                        PressIfEnabled(GetMainButton(dialog));
                    }

                    break;
                case EaStep.Nothing:
                    if (pagePending)
                    {
                        PressDownload(controls);
                    }

                    break;
            }

            return step;
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
            var started = DateTime.Now;
            var pagePending = false;
            while (DateTime.Now - started < stepsPatience)
            {
                await Task.Delay(checkPause, Cancellation).ConfigureAwait(false);
                var window = StoreWindows.Find(Process, (title, _) => title == "EA");
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                var pending = pagePending;
                var step = StoreWindows.WithControls(window, controls => Advance(controls, pending));
                if (step == EaStep.GameNotInstalled)
                {
                    pagePending = true;
                }
                else if (step == EaStep.Other)
                {
                    Logger.Warn($"The EA app answered something else to the install of {Game.GameId}.");
                    StoreWindows.Show(window);
                    return;
                }
                else if (step == EaStep.Terms)
                {
                    if (DirectInstall.AcceptsStoreTerms)
                    {
                        StoreWindows.Minimize(window);
                    }
                    else
                    {
                        AskPerson(window, "LOCAtrioAcceptInStore", StoreName);
                    }

                    return;
                }
            }

            Logger.Warn($"The EA app didn't get to the install of {Game.GameId}.");
        }
    }
}
