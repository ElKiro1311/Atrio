using CefSharp;
using CefSharp.Wpf;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.Atrio;
using Playnite.Common;
using Playnite.DesktopApp.Atrio;
using Playnite.SDK;
using Playnite.WebView;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>
    /// The browser of Atrio. It is a page of the application, next to the others, and it shares its
    /// sessions with the views used to connect the stores. It runs on the engine of Edge that Windows brings, which
    /// plays the videos of the stores with their sound and carries extensions; without it, on the engine of the
    /// rest of Atrio.
    /// </summary>
    public class BrowserViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DesktopAppViewModel main;
        private FrameworkElement browser;
        private ChromiumWebBrowser chromium;
        private WebView2 edge;
        private Task edgeReady;
        private bool edgeStarted;
        private CookieBridge cookies;
        private readonly DispatcherTimer cookieTimer;
        private readonly DispatcherTimer cookieHeartbeat;
        private bool visitedSteam;
        private bool shown;

        /// <summary>Until then, the pages of extensions open by themselves (see IsUnaskedExtensionPage).</summary>
        private DateTime quietUntil = DateTime.MinValue;

        /// <summary>Applications of the extensions under way, all on the thread of the window.</summary>
        private int installing;

        /// <summary>The last page asked for by the person or by a page, not by an extension on its own.</summary>
        private string pageWanted;

        /// <summary>Created with the first page: most of the time nobody needs it.</summary>
        public FrameworkElement Browser => browser ?? (browser = CreateBrowser());

        /// <summary>True when the page runs on the engine of Edge.</summary>
        public bool UsesEdge => Browser is WebView2;

        private string address;
        public string Address
        {
            get => address;
            set => SetValue(ref address, value);
        }

        public RelayCommand<object> GoCommand { get; }
        public RelayCommand<object> OpenOutsideCommand { get; }
        public RelayCommand<object> CloseCommand { get; }

        public BrowserViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            GoCommand = new RelayCommand<object>(_ => Go());
            OpenOutsideCommand = new RelayCommand<object>(_ =>
            {
                if (!Address.IsNullOrEmpty())
                {
                    ProcessStarter.StartUrl(Address)?.Dispose();
                }
            });
            CloseCommand = new RelayCommand<object>(_ =>
            {
                main.CloseBrowser();
                Navigate("about:blank");
            });

            // A sign in done in the browser reaches the rest of Atrio soon after each page, and from time to time
            // while it stays open, for the pages that change their session without loading a new one.
            cookieTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            cookieTimer.Tick += async (_, __) =>
            {
                cookieTimer.Stop();
                await PassSessionsFromEdgeAsync();
            };

            cookieHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            cookieHeartbeat.Tick += async (_, __) => await PassSessionsFromEdgeAsync();
        }

        private FrameworkElement CreateBrowser()
        {
            if (EdgeBrowser.IsAvailable())
            {
                try
                {
                    return CreateEdge();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to create the browser of Edge, the other engine takes its place.");
                    edge = null;
                    edgeReady = null;
                }
            }

            return CreateChromium();
        }

        private ChromiumWebBrowser CreateChromium()
        {
            var view = new ChromiumWebBrowser
            {
                RequestHandler = new InAppBrowserRequestHandler(),
                LifeSpanHandler = new InAppBrowserLifeSpanHandler(),
                DownloadHandler = new InAppBrowserDownloadHandler()
            };

            view.IsBrowserInitializedChanged += (_, e) =>
            {
                if (e.NewValue is bool initialized && initialized)
                {
                    InAppBrowserLanguage.Apply(view, main.AppSettings.Language);
                    if (!shown)
                    {
                        view.GetBrowserHost()?.SetAudioMuted(true);
                    }
                }
            };

            view.AddressChanged += (_, e) => OnAddress(e.NewValue as string);
            view.PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1 && view.CanGoBack)
                {
                    view.Back();
                    e.Handled = true;
                }
                else if (e.ChangedButton == MouseButton.XButton2 && view.CanGoForward)
                {
                    view.Forward();
                    e.Handled = true;
                }
            };

            chromium = view;
            return view;
        }

        private WebView2 CreateEdge()
        {
            edge = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 27, 40, 56) };
            cookies = new CookieBridge(EdgeBrowser.SessionsPicturePath);
            edgeReady = StartEdgeSafeAsync(edge);
            return edge;
        }

        private async Task StartEdgeSafeAsync(WebView2 view)
        {
            try
            {
                await StartEdgeAsync(view);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to start the browser of Edge, the other engine takes its place.");
                FallBackToChromium();
            }
        }

        /// <summary>The page goes on with the engine of the rest of Atrio, where it was going.</summary>
        private void FallBackToChromium()
        {
            if (edge == null)
            {
                return;
            }

            var failed = edge;
            edge = null;
            edgeReady = null;
            edgeStarted = false;
            cookieTimer.Stop();
            cookieHeartbeat.Stop();
            browser = CreateChromium();
            OnPropertyChanged(nameof(Browser));
            OnPropertyChanged(nameof(UsesEdge));
            try
            {
                failed?.Dispose();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to close the browser of Edge.");
            }

            if (!Address.IsNullOrEmpty())
            {
                chromium.Address = Address;
            }
        }

        private async Task StartEdgeAsync(WebView2 view)
        {
            var environment = await EdgeBrowser.GetEnvironmentAsync(main.AppSettings.Language);
            await view.EnsureCoreWebView2Async(environment);
            if (edge != view)
            {
                return;
            }

            var core = view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.AreDevToolsEnabled = PlayniteEnvironment.IsDebugBuild || Environment.GetEnvironmentVariable("ATRIO_BANCO") == "1";
            core.IsMuted = !shown;

            core.ProcessFailed += (_, e) =>
            {
                logger.Error($"The engine of Edge failed: {e.ProcessFailedKind}, {e.Reason}.");
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited &&
                    Application.Current?.Dispatcher.HasShutdownStarted == false)
                {
                    FallBackToChromium();
                }
            };

            core.SourceChanged += (_, __) =>
            {
                // Extensions open their welcome and their news by themselves when they're installed or updated, and
                // here there's only one tab: the page the person was opening comes back.
                if (IsUnaskedExtensionPage(core.Source) && !IsExtensionPage(pageWanted) && !pageWanted.IsNullOrEmpty())
                {
                    logger.Info($"Page opened by an extension left closed: {core.Source}");
                    core.Navigate(pageWanted);
                    return;
                }

                OnAddress(core.Source);
            };

            core.NavigationStarting += (_, e) =>
            {
                // The links to the clients of the stores open the client, and only after a click.
                if (LinkBrowsers.IsStoreClientLink(e.Uri))
                {
                    e.Cancel = true;
                    if (e.IsUserInitiated)
                    {
                        ProcessStarter.StartUrl(e.Uri)?.Dispose();
                    }

                    return;
                }

                // A page of an extension the person opens, from one of its links, is theirs.
                if (!IsUnaskedExtensionPage(e.Uri))
                {
                    pageWanted = e.Uri;
                }
            };

            core.LaunchingExternalUriScheme += (_, e) =>
            {
                e.Cancel = true;
                if (e.IsUserInitiated && LinkBrowsers.IsStoreClientLink(e.Uri))
                {
                    ProcessStarter.StartUrl(e.Uri)?.Dispose();
                }
            };

            core.NewWindowRequested += (_, e) =>
            {
                if (LinkBrowsers.IsStoreClientLink(e.Uri))
                {
                    e.Handled = true;
                    if (e.IsUserInitiated)
                    {
                        ProcessStarter.StartUrl(e.Uri)?.Dispose();
                    }

                    return;
                }

                if (IsUnaskedExtensionPage(e.Uri))
                {
                    e.Handled = true;
                    logger.Info($"Page opened by an extension left closed: {e.Uri}");
                    return;
                }

                // Signing in with another account needs its own small window to report back to the page; the rest
                // opens here, like the browser of Atrio always did.
                if (e.WindowFeatures.HasSize || e.WindowFeatures.HasPosition)
                {
                    return;
                }

                e.Handled = true;
                if (!e.Uri.IsNullOrEmpty())
                {
                    core.Navigate(e.Uri);
                }
            };

            core.NavigationCompleted += (_, __) => RestartCookieTimer();
            await cookies.SyncAsync(core.CookieManager);

            // The engine could have failed meanwhile, and the other one took its place.
            if (edge != view)
            {
                return;
            }

            // The page opens right away; the extensions loaded before are in the browser already.
            edgeStarted = true;
            if (!Address.IsNullOrEmpty())
            {
                pageWanted = Address;
                core.Navigate(Address);
            }

            if (shown)
            {
                cookieHeartbeat.Start();
            }

            await RefreshExtensionsAsync();
        }

        /// <summary>
        /// Loads or removes the extensions after the person changes them. An extension that just arrived works on
        /// the open page when it loads again.
        /// </summary>
        public async Task RefreshExtensionsAsync()
        {
            installing++;
            quietUntil = DateTime.MaxValue;
            bool added;
            try
            {
                added = await ApplyExtensionsAsync();
            }
            finally
            {
                if (--installing == 0)
                {
                    quietUntil = DateTime.UtcNow.AddSeconds(10);
                }
            }

            if (added)
            {
                var core = GetCore();
                var source = core?.Source;
                if (!source.IsNullOrEmpty() && !source.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    core.Reload();
                }
            }
        }

        /// <summary>
        /// A page of an extension while extensions are being installed: their welcome and their news. Edge says those
        /// were asked for by the person, so the time is what tells them apart.
        /// </summary>
        private bool IsUnaskedExtensionPage(string url)
        {
            return IsExtensionPage(url) && DateTime.UtcNow < quietUntil;
        }

        private static void TryNavigate(CoreWebView2 core, string url)
        {
            try
            {
                core.Navigate(url);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"The browser of Edge couldn't open {url}.");
            }
        }

        private static bool IsExtensionPage(string url)
        {
            return url?.StartsWith("chrome-extension:", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>The engine of Edge, null when it isn't ready or it failed.</summary>
        private CoreWebView2 GetCore()
        {
            try
            {
                return edgeStarted ? edge?.CoreWebView2 : null;
            }
            catch (Exception e) when (e is InvalidOperationException || e is ObjectDisposedException)
            {
                return null;
            }
        }

        private void RestartCookieTimer()
        {
            cookieTimer.Stop();
            cookieTimer.Start();
        }

        private async Task PassSessionsFromEdgeAsync()
        {
            try
            {
                var core = GetCore();
                if (core != null)
                {
                    await cookies.FromEdgeAsync(core.CookieManager);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to pass the sessions of the browser.");
            }
        }

        /// <summary>Loads or removes the extensions the person turned on. True when one was added.</summary>
        private async Task<bool> ApplyExtensionsAsync()
        {
            var core = GetCore();
            if (core == null)
            {
                return false;
            }

            try
            {
                return await EdgeBrowser.ApplyExtensionsAsync(core,
                    () => new List<string>(GameUnifier.Current?.Settings.BrowserExtensions ?? new List<string>()), CancellationToken.None);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to apply the extensions of the browser.");
                return false;
            }
        }

        private void OnAddress(string newAddress)
        {
            Address = newAddress;
            visitedSteam |= Address?.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Navigate(string url)
        {
            if (chromium != null)
            {
                chromium.LoadUrl(url);
                return;
            }

            var core = GetCore();
            if (core != null)
            {
                pageWanted = url;
                TryNavigate(core, url);
            }
            else
            {
                // Still starting: it opens this instead of what it was going to open.
                Address = url;
                pageWanted = url;
            }
        }

        public void Open(string url)
        {
            Address = url;
            pageWanted = url;
            var view = Browser;
            if (chromium != null)
            {
                chromium.Address = url;
            }
            else
            {
                // Before the engine is ready, it opens Address when it is.
                var core = GetCore();
                if (core != null)
                {
                    TryNavigate(core, url);
                }
            }

            view.Focus();
        }

        private void Go()
        {
            var target = Address?.Trim();
            if (target.IsNullOrEmpty())
            {
                return;
            }

            Open(Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") ? target : "https://" + target);
        }

        public async void OnShown()
        {
            shown = true;
            SetMuted(false);
            try
            {
                var core = GetCore();
                if (core != null)
                {
                    // A sign in done meanwhile in a window of a store reaches this browser.
                    await cookies.SyncAsync(core.CookieManager);
                    if (shown)
                    {
                        cookieHeartbeat.Start();
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to pass the sessions to the browser.");
            }
        }

        /// <summary>A trailer shouldn't keep playing behind the library.</summary>
        public async void OnHidden()
        {
            shown = false;
            SetMuted(true);
            cookieTimer.Stop();
            cookieHeartbeat.Stop();
            await PassSessionsFromEdgeAsync();

            // Signing in or entering the PIN of Family View are done here: the pages of the games should notice.
            if (visitedSteam)
            {
                visitedSteam = false;
                SteamFriends.ForgetSession();
                main.SelectedGameDetails?.ReloadFriends();
            }
        }

        private void SetMuted(bool muted)
        {
            try
            {
                if (chromium?.IsBrowserInitialized == true)
                {
                    chromium.GetBrowserHost()?.SetAudioMuted(muted);
                }

                var core = GetCore();
                if (core != null)
                {
                    core.IsMuted = muted;
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to mute the browser.");
            }
        }

        /// <summary>For the bench: runs a script on the page and gives back what it returns, as JSON.</summary>
        internal async Task<string> RunScriptAsync(string script)
        {
            await WaitReadyAsync();
            var core = GetCore();
            if (core != null)
            {
                return await core.ExecuteScriptAsync(script);
            }

            if (chromium != null)
            {
                var result = await chromium.EvaluateScriptAsync(script);
                return result.Success ? Playnite.Common.Serialization.ToJson(result.Result) : "error: " + result.Message;
            }

            return null;
        }

        /// <summary>For the bench: waits until the engine of Edge is ready.</summary>
        internal Task WaitReadyAsync()
        {
            return edgeReady ?? Task.CompletedTask;
        }

        internal CoreWebView2 EdgeCore => GetCore();
    }
}
