using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.Atrio;
using Playnite.Controls;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace Playnite.DesktopApp.Windows
{
    /// <summary>
    /// The trailers of a game of Steam, with sound. The browser of Atrio can't play their sound, so they play in the
    /// engine of Edge that Windows brings (WebView2); without it there are no trailers, and Atrio says so.
    /// </summary>
    public class TrailerWindow : WindowBase
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly WebView2 view = new WebView2();
        private readonly List<SteamTrailer> trailers;

        /// <summary>The bench plays the trailers without a sound coming out of the speakers.</summary>
        internal static bool Muted { get; set; }

        /// <summary>For the bench: what the page says about the video.</summary>
        internal System.Threading.Tasks.Task<string> RunScriptAsync(string script)
        {
            return view.CoreWebView2 == null ? System.Threading.Tasks.Task.FromResult<string>(null) : view.ExecuteScriptAsync(script);
        }

        public static bool IsEngineAvailable()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException || e is DllNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>Asks Steam for the trailers of the game and shows them, or says why it can't.</summary>
        public static async void Open(string gameName, string appId)
        {
            var dialogs = PlayniteApplication.Current?.Dialogs;
            if (!IsEngineAvailable())
            {
                dialogs?.ShowMessage(ResourceProvider.GetString("LOCAtrioTrailerNoEngine"), ResourceProvider.GetString("LOCAtrioTrailer"));
                return;
            }

            List<SteamTrailer> found;
            try
            {
                found = await SteamContent.GetTrailersAsync(appId, CancellationToken.None);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to ask Steam for the trailers of {appId}.");
                found = null;
            }

            if (found == null || found.Count == 0)
            {
                dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString(found == null ? "LOCAtrioTrailerFailed" : "LOCAtrioTrailerNone"), gameName),
                    ResourceProvider.GetString("LOCAtrioTrailer"));
                return;
            }

            var window = new TrailerWindow(gameName, found)
            {
                Owner = PlayniteApplication.CurrentNative?.Windows.Cast<Window>().FirstOrDefault(a => a.IsActive) ?? PlayniteApplication.CurrentNative?.MainWindow
            };
            window.Show();
        }

        public TrailerWindow(string gameName, List<SteamTrailer> trailers) : base()
        {
            this.trailers = trailers;
            Style = TryFindResource("StandardWindowStyle") as Style;
            Title = string.Format(ResourceProvider.GetString("LOCAtrioTrailerWindow"), gameName);
            Width = 1280;
            Height = 860;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = true;
            view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 12, 16);
            Content = view;
            Loaded += async (_, __) =>
            {
                try
                {
                    await StartAsync();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to start the player of the trailers.");
                    Close();
                }
            };
            Closed += (_, __) => view.Dispose();
        }

        /// <summary>The page of the player, with the trailers and the words written into it.</summary>
        public static string GetPage(IEnumerable<SteamTrailer> trailers)
        {
            string page;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Playnite.DesktopApp.Atrio.Trailer.html"))
            using (var reader = new StreamReader(stream))
            {
                page = reader.ReadToEnd();
            }

            var list = trailers.Select(a => new Dictionary<string, object>
            {
                { "name", a.Name },
                { "dashH264", a.DashH264 },
                { "dashAv1", a.DashAv1 },
                { "thumbnail", a.Thumbnail }
            }).ToList();
            var words = new Dictionary<string, string>
            {
                { "noSound", ResourceProvider.GetString("LOCAtrioTrailerNoSound") },
                { "failed", ResourceProvider.GetString("LOCAtrioTrailerPlayFailed") }
            };

            // The JSON goes inside a script: "</" is written so it can't close it.
            string json(object value) => Playnite.Common.Serialization.ToJson(value).Replace("</", "<\\/");
            return page.Replace("/*TRAILERS*/[]", json(list)).Replace("/*TEXTS*/{}", json(words));
        }

        private async System.Threading.Tasks.Task StartAsync()
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var folder = Path.Combine(PlaynitePaths.ConfigRootPath, "cache", "webview2");
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            if (Muted)
            {
                core.IsMuted = true;
            }

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close")
                {
                    Close();
                }
            };

            // Only this page: a link can't take the window anywhere else.
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                }
            };

            view.NavigateToString(GetPage(trailers));
        }
    }
}
