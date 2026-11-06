using CefSharp;
using Playnite.Atrio;
using Playnite.Common;
using System;
using System.IO;

namespace Playnite.WebView
{
    /// <summary>
    /// What makes the browser of Atrio behave like a browser, which the views used to sign in to a store
    /// don't need: new windows, downloads, the clients of the stores and pages in the language of the application.
    /// </summary>
    public static class InAppBrowserLanguage
    {
        /// <summary>
        /// Only for this browser: add-ons read pages with the rest of the views and some of them expect English.
        /// A request can't change this header by itself, the network layer writes its own.
        /// </summary>
        public static async void Apply(IWebBrowser browser, string language)
        {
            var acceptLanguage = LinkBrowsers.GetAcceptLanguage(language);
            if (acceptLanguage == null)
            {
                return;
            }

            try
            {
                var version = Cef.ChromiumVersion.Split('.')[0];
                var userAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version}.0.0.0 Safari/537.36";
                using (var client = browser.GetDevToolsClient())
                {
                    await client.Network.SetUserAgentOverrideAsync(userAgent, acceptLanguage);
                    await client.Emulation.SetUserAgentOverrideAsync(userAgent, acceptLanguage);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Playnite.SDK.LogManager.GetLogger().Debug(e, "Failed to set the language of the browser.");
            }
        }
    }

    public class InAppBrowserRequestHandler : CefSharp.Handler.RequestHandler
    {
        protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
        {
            if (!LinkBrowsers.IsStoreClientLink(request.Url))
            {
                return false;
            }

            // A page can only start a client because the user clicked on something.
            if (userGesture)
            {
                ProcessStarter.StartUrl(request.Url)?.Dispose();
            }

            return true;
        }
    }

    public class InAppBrowserLifeSpanHandler : CefSharp.Handler.LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null;
            if (LinkBrowsers.IsStoreClientLink(targetUrl))
            {
                if (userGesture)
                {
                    ProcessStarter.StartUrl(targetUrl)?.Dispose();
                }

                return true;
            }

            // Signing in with another account needs its own small window to report back to the page.
            if (targetDisposition == WindowOpenDisposition.NewPopup)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(targetUrl))
            {
                chromiumWebBrowser.LoadUrl(targetUrl);
            }

            return true;
        }
    }

    public class InAppBrowserDownloadHandler : CefSharp.Handler.DownloadHandler
    {
        protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        {
            return true;
        }

        protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback)
        {
            if (!callback.IsDisposed)
            {
                using (callback)
                {
                    var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    callback.Continue(Path.Combine(folder, "Downloads", downloadItem.SuggestedFileName), showDialog: true);
                }
            }

            return true;
        }
    }
}
